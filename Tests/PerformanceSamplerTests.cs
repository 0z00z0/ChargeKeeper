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
            return new ResourceReading(atUtc, 51_200, 61_440, 412, 37, 8_192, 4_096);
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
}
