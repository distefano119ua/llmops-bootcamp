using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace LlmOps.Tools;

public sealed record Approval(Guid Id,
    [property: JsonPropertyName("request_id")] Guid RequestId,
    string Action, string Arguments,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    string? Result = null,
    [property: JsonPropertyName("approved_at")] DateTimeOffset? ApprovedAt = null,
    [property: JsonPropertyName("rejected_by")] string? RejectedBy = null,
    [property: JsonPropertyName("rejected_at")] DateTimeOffset? RejectedAt = null,
    [property: JsonPropertyName("rejection_reason")] string? RejectionReason = null,
    string Status = "pending",
    [property: JsonPropertyName("expired_at")] DateTimeOffset? ExpiredAt = null);

public sealed class ApprovalStore : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Approval> pending = new();
    private readonly ConcurrentDictionary<Guid, Approval> approved = new();
    private readonly ConcurrentDictionary<Guid, Approval> rejected = new();
    private readonly ConcurrentDictionary<Guid, Approval> expired = new();
    private readonly TimeSpan pendingTtl;
    private readonly Timer expiryTimer;
    private long ticketSequence;

    public ApprovalStore(TimeSpan pendingTtl)
    {
        this.pendingTtl = pendingTtl;
        expiryTimer = new Timer(_ => ExpirePending(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public Approval EnqueueTicket(Guid requestId, string arguments)
    {
        var approval = new Approval(Guid.NewGuid(), requestId, "create_ticket", arguments,
            DateTimeOffset.UtcNow);
        pending[approval.Id] = approval;
        return approval;
    }

    public Approval[] GetPending()
    {
        ExpirePending();
        return pending.Values.OrderBy(a => a.CreatedAt).ToArray();
    }

    public Approval[] GetApproved() => approved.Values.OrderByDescending(a => a.ApprovedAt).ToArray();

    public Approval[] GetRejected() => rejected.Values.OrderByDescending(a => a.RejectedAt).ToArray();

    public Approval[] GetExpired()
    {
        ExpirePending();
        return expired.Values.OrderByDescending(a => a.ExpiredAt).ToArray();
    }

    // Перевірка TTL також на шляху рішення, навіть якщо таймер ще не спрацював.
    private Approval? TakePending(Guid id)
    {
        if (!pending.TryRemove(id, out var approval)) return null;
        if (DateTimeOffset.UtcNow < approval.CreatedAt + pendingTtl) return approval;

        expired[id] = approval with { Status = "expired", ExpiredAt = approval.CreatedAt + pendingTtl };
        return null;
    }

    private void ExpirePending()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in pending)
            if (now >= entry.Value.CreatedAt + pendingTtl)
                TakePending(entry.Key);
    }

    public Approval? Cancel(Guid id, string user, string reason)
    {
        var approval = TakePending(id);
        if (approval is null) return null;

        var decision = approval with
        {
            Status = "rejected",
            RejectedBy = user,
            RejectedAt = DateTimeOffset.UtcNow,
            RejectionReason = reason
        };
        rejected[id] = decision;
        return decision;
    }

    public Approval? Approve(Guid id)
    {
        // Лише один запит може забрати заявку та виконати дію.
        var approval = TakePending(id);
        if (approval is null) return null;

        // Навчальне створення тікета: результат зберігається лише після approve.
        var result = approval with
        {
            Result = $"TICKET-{Interlocked.Increment(ref ticketSequence):D6}",
            Status = "approved",
            ApprovedAt = DateTimeOffset.UtcNow
        };
        approved[id] = result;
        return result;
    }

    public void Dispose() => expiryTimer.Dispose();
}
