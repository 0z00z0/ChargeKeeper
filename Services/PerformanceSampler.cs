namespace ChargeKeeper.Services;

/// <summary>One processor-time reading: the share of the whole machine this process used across the
/// interval that ended at <paramref name="AtUtc"/>.</summary>
internal readonly record struct ProcessorReading(DateTime AtUtc, double Percent);

/// <summary>One resource reading. All four come from a single process snapshot, which is why they
/// are one record and are sampled together.</summary>
internal readonly record struct ResourceReading(
    DateTime AtUtc, int WorkingSetKb, int PrivateBytesKb, int Handles, int Threads);

/// <summary>Where readings go. Behind an interface so the sampler can be exercised without touching
/// a file.</summary>
internal interface IPerformanceSink
{
    void Add(ProcessorReading reading);
    void Add(ResourceReading reading);

    /// <summary>Writes whatever has accumulated. Called from the once-a-second tick, so a 10 Hz run
    /// costs one file open a second rather than ten.</summary>
    void Flush();
}

/// <summary>What is measured. Two members, because they cost wildly different amounts.</summary>
internal interface IPerformanceProbe
{
    /// <summary>Cumulative processor time for this process. Read from the process handle directly:
    /// a few microseconds, no allocation, no machine-wide snapshot.</summary>
    TimeSpan ProcessorTime { get; }

    /// <summary>Memory, handles and threads. Forces a snapshot of every process on the machine, so
    /// all four are taken from that one snapshot and never at the processor rate.</summary>
    ResourceReading ReadResources(DateTime atUtc);
}

/// <summary>Starts a repeating callback. Disposing the returned handle stops it.</summary>
internal interface IPeriodicScheduler
{
    IDisposable Schedule(TimeSpan period, Action callback);
}

/// <summary>The shipped scheduler: one <see cref="System.Threading.Timer"/> per series.</summary>
internal sealed class TimerScheduler : IPeriodicScheduler
{
    public IDisposable Schedule(TimeSpan period, Action callback) =>
        new System.Threading.Timer(_ => callback(), null, period, period);
}

/// <summary>The processor share of the whole machine, as Task Manager reports it.</summary>
internal static class ProcessorLoad
{
    /// <summary>
    /// Processor time used over an interval, as a percentage of what every core could have supplied
    /// in it. Zero for a non-positive interval — a clock step backwards, or two ticks inside the
    /// timer's resolution — rather than a division that reports an impossible spike.
    /// </summary>
    public static double Percent(TimeSpan used, TimeSpan elapsed, int processorCount)
    {
        if (elapsed <= TimeSpan.Zero || processorCount <= 0) return 0;
        double percent = used.TotalMilliseconds / (elapsed.TotalMilliseconds * processorCount) * 100.0;
        return percent < 0 ? 0 : percent;
    }
}

/// <summary>
/// Samples this process at two rates: processor time at the rate the user chose, and memory, handles
/// and threads once a second whatever that rate is.
/// </summary>
/// <remarks>
/// <para>The two rates are not a compromise. Reading processor time goes to the process handle and
/// costs microseconds; the other four force a snapshot of every process on the machine, which costs
/// milliseconds. Sampling all five at 10 Hz would spend a measurable fraction of a core on the act
/// of measuring, which is the one thing a self-measurement graph must not do.</para>
/// <para>Switched off, this schedules NOTHING: no timer exists, so no callback runs and no reading
/// is allocated. A timer that fires and returns early would still cost a wake per tick and is not
/// what off means here. <c>PerformanceSamplerTests</c> holds that promise.</para>
/// </remarks>
internal sealed class PerformanceSampler : IDisposable
{
    /// <summary>The fixed rate for the snapshot-backed readings, independent of the chosen rate.
    /// Also the flush cadence, so the file write rides on a tick that happens anyway.</summary>
    public static readonly TimeSpan ResourcePeriod = TimeSpan.FromSeconds(1);

    private readonly IPerformanceProbe  _probe;
    private readonly IPeriodicScheduler _scheduler;
    private readonly IPerformanceSink   _sink;
    private readonly Func<DateTime>     _nowUtc;
    private readonly int                _processorCount;

    private readonly Lock _gate = new();

    // Null means not scheduled, which is also how a callback already in flight when the feature was
    // switched off knows to do nothing.
    private IDisposable? _processorTick;
    private IDisposable? _resourceTick;

    // Processor time is cumulative, so the first tick after a start only establishes the baseline.
    private bool     _haveBaseline;
    private DateTime _baselineAtUtc;
    private TimeSpan _baselineProcessorTime;

    public PerformanceSampler(
        IPerformanceProbe probe,
        IPerformanceSink sink,
        IPeriodicScheduler? scheduler = null,
        Func<DateTime>? nowUtc = null,
        int? processorCount = null)
    {
        _probe          = probe ?? throw new ArgumentNullException(nameof(probe));
        _sink           = sink  ?? throw new ArgumentNullException(nameof(sink));
        _scheduler      = scheduler ?? new TimerScheduler();
        _nowUtc         = nowUtc    ?? (() => DateTime.UtcNow);
        _processorCount = processorCount ?? Environment.ProcessorCount;
    }

    /// <summary>True while anything is scheduled.</summary>
    public bool IsSampling { get { lock (_gate) return _processorTick is not null; } }

    /// <summary>The rate as last applied, or null while off.</summary>
    public PerformanceSampleRate? ActiveRate { get; private set; }

    /// <summary>
    /// Brings the sampler in line with the settings. Off tears both timers down and schedules
    /// nothing; on replaces them, so a rate change takes effect without a restart.
    /// </summary>
    public void Apply(bool enabled, PerformanceSampleRate rate)
    {
        var normalised = PerformanceSampleRates.Normalise(rate);

        lock (_gate)
        {
            // Settings changes arrive for every setting in the app, not just these two. Re-applying
            // the same state would tear down both timers and lose the processor baseline, costing a
            // sample every time an unrelated setting was saved.
            if (enabled == (_processorTick is not null) && (!enabled || ActiveRate == normalised)) return;

            StopLocked();
            if (!enabled) return;   // nothing scheduled, nothing allocated

            ActiveRate     = normalised;
            _haveBaseline  = false;
            _processorTick = _scheduler.Schedule(normalised.Period(), SampleProcessor);
            _resourceTick  = _scheduler.Schedule(ResourcePeriod,      SampleResources);
        }
    }

    public void Dispose() { lock (_gate) StopLocked(); }

    private void StopLocked()
    {
        _processorTick?.Dispose();
        _resourceTick?.Dispose();
        _processorTick = null;
        _resourceTick  = null;
        ActiveRate     = null;
    }

    private void SampleProcessor()
    {
        try
        {
            lock (_gate)
            {
                if (_processorTick is null) return;   // switched off while this tick was in flight

                var now   = _nowUtc();
                var total = _probe.ProcessorTime;

                if (!_haveBaseline)
                {
                    _haveBaseline          = true;
                    _baselineAtUtc         = now;
                    _baselineProcessorTime = total;
                    return;                            // a cumulative counter needs two reads
                }

                double percent = ProcessorLoad.Percent(
                    total - _baselineProcessorTime, now - _baselineAtUtc, _processorCount);
                _baselineAtUtc         = now;
                _baselineProcessorTime = total;

                _sink.Add(new ProcessorReading(now, percent));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("PerformanceSampler.SampleProcessor", ex);
        }
    }

    private void SampleResources()
    {
        try
        {
            lock (_gate)
            {
                if (_resourceTick is null) return;   // switched off while this tick was in flight
                _sink.Add(_probe.ReadResources(_nowUtc()));
                _sink.Flush();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("PerformanceSampler.SampleResources", ex);
        }
    }
}
