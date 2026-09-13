using System.Diagnostics;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// The shipped probe: what this process is actually costing, read from Windows.
/// </summary>
/// <remarks>
/// <para>Every reading but one goes straight to the process handle and allocates nothing:
/// <see cref="Environment.CpuUsage"/> for processor time, and
/// <see cref="NativeMethods.CurrentProcessResources"/> for working set, private bytes, handle count
/// and the two cumulative I/O totals.</para>
/// <para>Thread count is the exception and the reason this type holds state. Every route to it is a
/// snapshot of every process on the machine — milliseconds, scaling with how many are running — so
/// it is re-read on <see cref="ThreadCountPeriod"/> and the last value stands in between. A count
/// that moves by one between two reads is worth far less than a millisecond a second.</para>
/// </remarks>
internal sealed class SystemPerformanceProbe : IPerformanceProbe
{
    /// <summary>How often the thread count is re-read. Under 1 % of what reading it every second
    /// would cost, and the figure it feeds is a readout rather than a plotted line.</summary>
    internal static readonly TimeSpan ThreadCountPeriod = TimeSpan.FromMinutes(1);

    private readonly Func<DateTime> _nowUtc;

    private int       _threads;
    private DateTime? _threadsReadAtUtc;

    // The last complete reading, so a failed query draws the previous value rather than a cliff to
    // zero. Only the timestamp moves.
    private ResourceReading _last;

    public SystemPerformanceProbe(Func<DateTime>? nowUtc = null) =>
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);

    public TimeSpan ProcessorTime => Environment.CpuUsage.TotalTime;

    public ResourceReading ReadResources(DateTime atUtc)
    {
        int threads = ThreadCount();

        var counters = NativeMethods.CurrentProcessResources();
        if (counters is null) return _last = _last with { AtUtc = atUtc, Threads = threads };

        var c = counters.Value;
        return _last = new ResourceReading(
            atUtc,
            WorkingSetKb:   (int)(c.WorkingSetBytes / 1024),
            PrivateBytesKb: (int)(c.PrivateBytes    / 1024),
            Handles:        c.Handles,
            Threads:        threads,
            ReadKb:         c.ReadBytes  / 1024,
            WriteKb:        c.WriteBytes / 1024);
    }

    private int ThreadCount()
    {
        var now = _nowUtc();
        if (_threadsReadAtUtc is { } last && now - last < ThreadCountPeriod) return _threads;

        try
        {
            using var self = Process.GetCurrentProcess();
            _threads = self.Threads.Count;
        }
        catch (Exception ex)
        {
            AppLog.Error("SystemPerformanceProbe.ThreadCount", ex);
        }

        // Stamped even when the read threw, so a failing route is not retried every second.
        _threadsReadAtUtc = now;
        return _threads;
    }
}
