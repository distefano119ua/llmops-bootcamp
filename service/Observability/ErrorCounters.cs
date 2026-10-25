namespace LlmOps.Observability;

public sealed class ErrorCounters
{
    private long provider429;
    private long toolTimeout;
    private long guardrailRefusal;
    private long guardrailInspectionTimeout;

    public void RecordProvider429() => Interlocked.Increment(ref provider429);
    public void RecordToolTimeout() => Interlocked.Increment(ref toolTimeout);
    public void RecordGuardrailRefusal() => Interlocked.Increment(ref guardrailRefusal);
    public void RecordGuardrailInspectionTimeout() => Interlocked.Increment(ref guardrailInspectionTimeout);

    public object GetSnapshot() => new
    {
        provider_429 = Interlocked.Read(ref provider429),
        tool_timeout = Interlocked.Read(ref toolTimeout),
        guardrail_refusal = Interlocked.Read(ref guardrailRefusal),
        guardrail_inspection_timeout = Interlocked.Read(ref guardrailInspectionTimeout)
    };
}
