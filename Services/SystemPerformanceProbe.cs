using System.Diagnostics;

namespace ChargeKeeper.Services;

/// <summary>
/// The shipped probe: what this process is actually costing, read from Windows.
/// </summary>
/// <remarks>
/// The two members are deliberately unlike each other. <see cref="ProcessorTime"/> goes through
/// <see cref="Environment.CpuUsage"/>, which queries the current process handle and allocates
/// nothing. <see cref="ReadResources"/> goes through <see cref="Process.GetCurrentProcess"/>, which
/// takes a snapshot of every process on the machine; all four values it returns come from that one
/// snapshot, so reading them together costs what reading any one of them would.
/// </remarks>
internal sealed class SystemPerformanceProbe : IPerformanceProbe
{
    public TimeSpan ProcessorTime => Environment.CpuUsage.TotalTime;

    public ResourceReading ReadResources(DateTime atUtc)
    {
        using var self = Process.GetCurrentProcess();
        return new ResourceReading(
            atUtc,
            WorkingSetKb:   (int)(self.WorkingSet64      / 1024),
            PrivateBytesKb: (int)(self.PrivateMemorySize64 / 1024),
            Handles:        self.HandleCount,
            Threads:        self.Threads.Count);
    }
}
