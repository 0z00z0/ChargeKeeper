using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// What the self-measurement sampler promises. The load-bearing one is the first group: switched
/// off, the sampler must schedule NOTHING — no timer, no callback, no reading. A timer that fires
/// and returns early still costs a wake per tick, so "off" is asserted as "nothing was ever handed
/// to the scheduler", not as "no sample came out".
/// </summary>
public class PerformanceSamplerTests
{
    /// <summary>A scheduler that records what it was asked to run and never runs any of it, so a
    /// test can inspect exactly what would have been ticking and fire a callback deliberately.</summary>
    private sealed class RecordingScheduler : IPeriodicScheduler
    {
        internal sealed class Job(TimeSpan period, Action callback)
        {
            public TimeSpan Period   { get; } = period;
            public Action   Callback { get; } = callback;
            public bool     Disposed { get; set; }
        }

        public List<Job> Jobs { get; } = [];

        public IEnumerable<Job> Live => Jobs.Where(j => !j.Disposed);

        public IDisposable Schedule(TimeSpan period, Action callback)
        {
            var job = new Job(period, callback);
            Jobs.Add(job);
            return new Handle(job);
        }

        private sealed class Handle(Job job) : IDisposable
        {
            public void Dispose() => job.Disposed = true;
        }
    }

    private sealed class ScriptedProbe : IPerformanceProbe
    {
        public TimeSpan Processor      { get; set; }
        public int      ProcessorReads { get; private set; }
        public int      ResourceReads  { get; private set; }

        public TimeSpan ProcessorTime
        {
            get { ProcessorReads++; return Processor; }
        }

        public ResourceReading ReadResources(DateTime atUtc)
        {
            ResourceReads++;
            return new ResourceReading(atUtc, 51_200, 61_440, 412, 37);
        }
    }

    private sealed class CollectingSink : IPerformanceSink
    {
        public List<ProcessorReading> Processor { get; } = [];
        public List<ResourceReading>  Resources { get; } = [];
        public int                    Flushes   { get; private set; }

        public void Add(ProcessorReading reading) => Processor.Add(reading);
        public void Add(ResourceReading reading)  => Resources.Add(reading);
        public void Flush()                       => Flushes++;
    }

    private sealed class Harness
    {
        public RecordingScheduler Scheduler { get; } = new();
        public ScriptedProbe      Probe     { get; } = new();
        public CollectingSink     Sink      { get; } = new();
        public DateTime           Now       { get; set; } = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);
        public PerformanceSampler Sampler   { get; }

        public Harness(int processorCount = 8)
        {
            Sampler = new PerformanceSampler(
                Probe, Sink, Scheduler, () => Now, processorCount);
        }

        public RecordingScheduler.Job ProcessorJob => Scheduler.Live.First();
        public RecordingScheduler.Job ResourceJob  => Scheduler.Live.Last();
    }

    // ── The off state ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SwitchedOff_NothingIsScheduledAtAll()
    {
        var h = new Harness();

        h.Sampler.Apply(enabled: false, PerformanceSampleRate.TenHz);

        Assert.Empty(h.Scheduler.Jobs);          // not "scheduled then disposed" — never scheduled
        Assert.False(h.Sampler.IsSampling);
        Assert.Null(h.Sampler.ActiveRate);
        Assert.Equal(0, h.Probe.ProcessorReads);
        Assert.Equal(0, h.Probe.ResourceReads);
        Assert.Empty(h.Sink.Processor);
        Assert.Empty(h.Sink.Resources);
        Assert.Equal(0, h.Sink.Flushes);
    }

    [Fact]
    public void SwitchedOffAfterRunning_EveryTimerIsDisposedAndNothingIsLeftLive()
    {
        var h = new Harness();
        h.Sampler.Apply(enabled: true, PerformanceSampleRate.TenHz);
        Assert.Equal(2, h.Scheduler.Live.Count());

        h.Sampler.Apply(enabled: false, PerformanceSampleRate.TenHz);

        Assert.Empty(h.Scheduler.Live);
        Assert.All(h.Scheduler.Jobs, job => Assert.True(job.Disposed));
        Assert.False(h.Sampler.IsSampling);
        Assert.Null(h.Sampler.ActiveRate);
    }

    /// <summary>The race the null checks in the callbacks exist for: a tick already on its way when
    /// the feature was switched off must record nothing.</summary>
    [Fact]
    public void ATickStillInFlightWhenSwitchedOff_RecordsNothing()
    {
        var h = new Harness();
        h.Sampler.Apply(enabled: true, PerformanceSampleRate.TenHz);
        var processor = h.ProcessorJob.Callback;
        var resource  = h.ResourceJob.Callback;

        // The baseline is taken FIRST, or the late processor tick would be swallowed by the
        // two-reads rule rather than by the guard this test is about.
        processor();
        h.Now = h.Now.AddMilliseconds(100);
        h.Probe.Processor = TimeSpan.FromMilliseconds(20);

        h.Sampler.Apply(enabled: false, PerformanceSampleRate.TenHz);
        processor();
        resource();

        Assert.Empty(h.Sink.Processor);
        Assert.Empty(h.Sink.Resources);
        Assert.Equal(0, h.Sink.Flushes);
    }

    [Fact]
    public void DisposingTheSampler_LeavesNothingScheduled()
    {
        var h = new Harness();
        h.Sampler.Apply(enabled: true, PerformanceSampleRate.OneHz);

        h.Sampler.Dispose();

        Assert.Empty(h.Scheduler.Live);
        Assert.False(h.Sampler.IsSampling);
    }

    // ── Two rates ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SwitchedOn_SchedulesExactlyTwoSeries()
    {
        var h = new Harness();

        h.Sampler.Apply(enabled: true, PerformanceSampleRate.TenHz);

        Assert.Equal(2, h.Scheduler.Live.Count());
        Assert.True(h.Sampler.IsSampling);
        Assert.Equal(PerformanceSampleRate.TenHz, h.Sampler.ActiveRate);
    }

    // The rate is passed as its ordinal: an internal enum cannot appear in a public signature, and
    // xUnit requires the test method to be public.
    [Theory]
    [InlineData((int)PerformanceSampleRate.TenHz,   100)]
    [InlineData((int)PerformanceSampleRate.FiveHz,  200)]
    [InlineData((int)PerformanceSampleRate.TwoHz,   500)]
    [InlineData((int)PerformanceSampleRate.OneHz,   1000)]
    [InlineData((int)PerformanceSampleRate.HalfHz,  2000)]
    [InlineData((int)PerformanceSampleRate.FifthHz, 5000)]
    [InlineData((int)PerformanceSampleRate.TenthHz, 10000)]
    public void TheProcessorSeriesTakesTheChosenRate(int rateOrdinal, int expectedMs)
    {
        var h = new Harness();

        h.Sampler.Apply(enabled: true, (PerformanceSampleRate)rateOrdinal);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), h.ProcessorJob.Period);
    }

    /// <summary>The whole point of decision one: the snapshot-backed readings never follow the
    /// chosen rate, because one of them costs a machine-wide process snapshot.</summary>
    [Theory]
    [InlineData((int)PerformanceSampleRate.TenHz)]
    [InlineData((int)PerformanceSampleRate.OneHz)]
    [InlineData((int)PerformanceSampleRate.TenthHz)]
    public void TheResourceSeriesIsAlwaysOncePerSecond(int rateOrdinal)
    {
        var h = new Harness();

        h.Sampler.Apply(enabled: true, (PerformanceSampleRate)rateOrdinal);

        Assert.Equal(TimeSpan.FromSeconds(1), h.ResourceJob.Period);
        Assert.Equal(TimeSpan.FromSeconds(1), PerformanceSampler.ResourcePeriod);
    }

    /// <summary>At the slow end the once-a-second series is the DENSER of the two. Stated as a test
    /// because on screen it looks like a defect, and the interface has to say which is which.</summary>
    [Fact]
    public void AtTheSlowestRate_TheResourceSeriesIsTheDenserOfTheTwo()
    {
        var h = new Harness();

        h.Sampler.Apply(enabled: true, PerformanceSampleRate.TenthHz);

        Assert.True(h.ResourceJob.Period < h.ProcessorJob.Period,
            "at 0.1 Hz the once-a-second resource series must be the denser line");
    }

    [Fact]
    public void AtTheFastestRate_TheProcessorSeriesIsTheDenserOfTheTwo()
    {
        var h = new Harness();

        h.Sampler.Apply(enabled: true, PerformanceSampleRate.TenHz);

        Assert.True(h.ProcessorJob.Period < h.ResourceJob.Period);
    }

    [Fact]
    public void ChangingTheRate_ReplacesTheTimersRatherThanAddingToThem()
    {
        var h = new Harness();
        h.Sampler.Apply(enabled: true, PerformanceSampleRate.TenHz);

        h.Sampler.Apply(enabled: true, PerformanceSampleRate.TenthHz);

        Assert.Equal(2, h.Scheduler.Live.Count());
        Assert.Equal(4, h.Scheduler.Jobs.Count);          // two replaced, two live
        Assert.Equal(TimeSpan.FromSeconds(10), h.ProcessorJob.Period);
        Assert.Equal(TimeSpan.FromSeconds(1),  h.ResourceJob.Period);
    }

    /// <summary>Every settings write raises the change event, not only these two settings. Applying
    /// the same state again must leave the timers alone: a restart would lose the processor baseline
    /// and cost a sample each time an unrelated setting was saved.</summary>
    [Fact]
    public void ApplyingTheSameSettingsAgainDoesNotRestartTheTimers()
    {
        var h = new Harness();
        h.Sampler.Apply(enabled: true, PerformanceSampleRate.TwoHz);

        h.Sampler.Apply(enabled: true, PerformanceSampleRate.TwoHz);

        Assert.Equal(2, h.Scheduler.Jobs.Count);   // still the original pair, nothing replaced
        Assert.Equal(2, h.Scheduler.Live.Count());
    }

    [Fact]
    public void ApplyingOffTwiceStillSchedulesNothing()
    {
        var h = new Harness();

        h.Sampler.Apply(enabled: false, PerformanceSampleRate.TwoHz);
        h.Sampler.Apply(enabled: false, PerformanceSampleRate.TenHz);

        Assert.Empty(h.Scheduler.Jobs);
    }

    /// <summary>The baseline survives a no-op apply, so the very next tick still produces a reading
    /// instead of starting the two-read cycle again.</summary>
    [Fact]
    public void ANoOpApplyKeepsTheProcessorBaseline()
    {
        var h = new Harness();
        h.Sampler.Apply(enabled: true, PerformanceSampleRate.OneHz);
        h.ProcessorJob.Callback();                    // baseline taken

        h.Sampler.Apply(enabled: true, PerformanceSampleRate.OneHz);
        h.Now = h.Now.AddSeconds(1);
        h.ProcessorJob.Callback();

        Assert.Single(h.Sink.Processor);
    }

    /// <summary>A stored value naming no member must not schedule a zero-period timer.</summary>
    [Fact]
    public void ARateOutsideTheEnum_FallsBackToTheDefaultStep()
    {
        var h = new Harness();

        h.Sampler.Apply(enabled: true, (PerformanceSampleRate)99);

        Assert.Equal(PerformanceSampleRates.Default, h.Sampler.ActiveRate);
        Assert.Equal(PerformanceSampleRates.Default.Period(), h.ProcessorJob.Period);
    }

    // ── What a tick records ─────────────────────────────────────────────────────────────────────

    /// <summary>Processor time is a cumulative counter, so one read is not a rate.</summary>
    [Fact]
    public void TheFirstProcessorTickOnlyTakesTheBaseline()
    {
        var h = new Harness();
        h.Sampler.Apply(enabled: true, PerformanceSampleRate.OneHz);

        h.ProcessorJob.Callback();

        Assert.Empty(h.Sink.Processor);
        Assert.Equal(1, h.Probe.ProcessorReads);
    }

    [Fact]
    public void TheSecondProcessorTickRecordsTheShareOfTheWholeMachine()
    {
        var h = new Harness(processorCount: 8);
        h.Sampler.Apply(enabled: true, PerformanceSampleRate.OneHz);
        h.ProcessorJob.Callback();                              // baseline

        h.Now = h.Now.AddSeconds(1);
        h.Probe.Processor = TimeSpan.FromMilliseconds(80);      // 80 ms of 8 cores x 1000 ms
        h.ProcessorJob.Callback();

        var reading = Assert.Single(h.Sink.Processor);
        Assert.Equal(1.0, reading.Percent, 6);
        Assert.Equal(h.Now, reading.AtUtc);
    }

    [Fact]
    public void AResourceTickRecordsOneSnapshotAndFlushes()
    {
        var h = new Harness();
        h.Sampler.Apply(enabled: true, PerformanceSampleRate.TenHz);

        h.ResourceJob.Callback();

        var reading = Assert.Single(h.Sink.Resources);
        Assert.Equal(51_200, reading.WorkingSetKb);
        Assert.Equal(412, reading.Handles);
        Assert.Equal(37, reading.Threads);
        Assert.Equal(1, h.Probe.ResourceReads);
        Assert.Equal(1, h.Sink.Flushes);
    }

    /// <summary>A processor tick must not touch the expensive snapshot — that is the whole reason
    /// the two series exist. Fifty fast ticks, still no snapshot read.</summary>
    [Fact]
    public void ProcessorTicksNeverTakeAProcessSnapshot()
    {
        var h = new Harness();
        h.Sampler.Apply(enabled: true, PerformanceSampleRate.TenHz);

        for (int i = 0; i < 50; i++) { h.Now = h.Now.AddMilliseconds(100); h.ProcessorJob.Callback(); }

        Assert.Equal(0, h.Probe.ResourceReads);
        Assert.Equal(49, h.Sink.Processor.Count);   // the first tick was the baseline
    }

    // ── The percentage itself ───────────────────────────────────────────────────────────────────

    [Fact]
    public void OneCoreFullyBusyOnAnEightCoreMachineIsAnEighthOfIt() =>
        Assert.Equal(12.5, ProcessorLoad.Percent(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), processorCount: 8), 6);

    [Fact]
    public void EveryCoreFullyBusyIsAHundredPercent() =>
        Assert.Equal(100.0, ProcessorLoad.Percent(
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(1), processorCount: 8), 6);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnIntervalThatDidNotAdvanceReportsZeroRatherThanASpike(int elapsedSeconds) =>
        Assert.Equal(0, ProcessorLoad.Percent(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(elapsedSeconds), processorCount: 8));

    [Fact]
    public void AProcessorCounterThatWentBackwardsReportsZeroRatherThanANegative() =>
        Assert.Equal(0, ProcessorLoad.Percent(
            TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1), processorCount: 8));
}
