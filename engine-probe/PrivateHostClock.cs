namespace RootEngineProbe;

// Receipt timestamps come from transport completion, before Unity processes the
// response. A stalled frame must count as elapsed time, not network latency.
internal sealed class PrivateHostClock
{
    private long receivedAt;
    private long hostTimeAtReceipt;

    public void Observe(long hostTimestamp, long sentAt, long receiptTime)
    {
        receivedAt = receiptTime;
        hostTimeAtReceipt = hostTimestamp + Math.Clamp((receiptTime - sentAt) / 2, 0, 5000);
    }

    public long Estimate(long now) => hostTimeAtReceipt + Math.Max(0, now - receivedAt);
}
