using LlmOps.Tools;
using LlmOps.Middleware;

namespace LlmOps.Endpoints;

public static class ApprovalEndpoints
{
    public static void MapApprovalEndpoints(this WebApplication app)
    {
        app.MapGet("/approvals", (ApprovalStore approvals) => Results.Ok(new
        {
            pending = approvals.GetPending(),
            approved = approvals.GetApproved(),
            rejected = approvals.GetRejected(),
            expired = approvals.GetExpired()
        }));

        app.MapPost("/approvals/{id:guid}/approve", (Guid id, ApprovalStore approvals) =>
        {
            var result = approvals.Approve(id);
            if (result is null) return (IResult)Results.NotFound();
            return Results.Ok(result);
        });

        app.MapPost("/approvals/{id:guid}/cancel", (Guid id, RejectApprovalRequest body, ApprovalStore approvals) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(body.User)) errors["user"] = ["User is required."];
            if (string.IsNullOrWhiteSpace(body.Reason)) errors["reason"] = ["Reason is required."];
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);

            var approval = approvals.Cancel(id, body.User!.Trim(), body.Reason!.Trim());
            if (approval is null) return (IResult)Results.NotFound();
            InfrastructureLog.Write(app.Logger, LogLevel.Information,
                new EventId(1600, "approval.rejected"), "Approval rejected", null,
                ("approval_id", approval.Id), ("chat_request_id", approval.RequestId),
                ("action", approval.Action), ("rejected_by", approval.RejectedBy),
                ("rejected_at", approval.RejectedAt), ("rejection_reason", approval.RejectionReason));
            return Results.NoContent();
        });
    }
}

public sealed record RejectApprovalRequest(string? User, string? Reason);
