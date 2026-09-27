using RootEngineProbe;

internal static class HostClockTests
{
    public static void Run()
    {
        void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); }
        var clock = new PrivateHostClock();
        // The host responds in 20 ms, then Unity cannot consume it for ten
        // seconds. Those ten seconds must not shift the displayed deadline.
        clock.Observe(1_000_000, 1000, 1020);
        Check(clock.Estimate(11_020) == 1_010_010, "A stalled frame was treated as network latency");
        Check(clock.Estimate(12_020) == 1_011_010, "Host clock stopped advancing after receipt");
        // A prompt following poll should agree with the already advancing
        // clock, preserving any timer adapted before that poll was processed.
        clock.Observe(1_010_000, 11_000, 11_020);
        Check(clock.Estimate(11_020) == 1_010_010, "Next poll changed the stalled-frame clock estimate");
        clock.Observe(2_000_000, 1000, 21_000);
        Check(clock.Estimate(21_000) == 2_005_000, "Network latency allowance exceeded five seconds");
    }
}
