using System;
using System.Threading;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The two promises about a run that cannot be tried by hand: that one script never has two runs
/// going at once, and that a run reaching the time limit is ended rather than left holding its own
/// gate shut. Everything else about running a script — the hidden window, the captured output, the
/// exit code — is proved by running the application.
/// </summary>
public class ScriptRunnerTests
{
    private static ScriptDefinition AScript(string id = "one") =>
        new(id, "Pause file sync", ScriptTrigger.MainsDisconnected, "Write-Output 'hello'");

    /// <summary>A started script that ends when it is told to, and records having been told.</summary>
    private sealed class FakeProcess : IScriptProcess
    {
        private readonly ManualResetEventSlim _finish;
        private readonly bool                 _everFinishes;

        public FakeProcess(ManualResetEventSlim finish, bool everFinishes, int exitCode = 0)
        {
            _finish = finish; _everFinishes = everFinishes; ExitCode = exitCode;
        }

        public int  Started  { get; private set; }
        public bool WasKilled { get; private set; }
        public bool WasDisposed { get; private set; }
        public int  ExitCode { get; }
        public string Output => "";

        public bool WaitForExit(TimeSpan limit) =>
            _everFinishes && _finish.Wait(limit);

        public void Kill() => WasKilled = true;
        public void Dispose() => WasDisposed = true;
    }

    [Fact]
    public void ASecondFiringWhileARunIsGoingIsSkipped()
    {
        using var finish = new ManualResetEventSlim(false);
        int starts = 0;
        var runner = new ScriptRunner(_ => { starts++; return new FakeProcess(finish, everFinishes: true); });

        var script = AScript();

        Assert.True(runner.Start(script, "the first event", TimeSpan.FromSeconds(30)));
        // The gate is taken before the run is dispatched, so the answer here does not depend on how
        // far the first run has got.
        Assert.False(runner.Start(script, "the second event", TimeSpan.FromSeconds(30)));
        Assert.True(runner.IsRunning(script.Id));

        finish.Set();
        Assert.True(SpinUntil(() => !runner.IsRunning(script.Id)),
                    "the run never released its gate.");
        Assert.Equal(1, starts);

        // And once it has finished, the same script starts again rather than being locked out.
        Assert.True(runner.Start(script, "a later event", TimeSpan.FromSeconds(30)));
        Assert.True(SpinUntil(() => !runner.IsRunning(script.Id)));
        Assert.Equal(2, starts);
    }

    /// <summary>Two scripts are two gates. Keyed on the identifier rather than the name, so two
    /// scripts sharing a name are still two.</summary>
    [Fact]
    public void OneScriptRunningDoesNotHoldAnotherBack()
    {
        using var finish = new ManualResetEventSlim(false);
        var runner = new ScriptRunner(_ => new FakeProcess(finish, everFinishes: true));

        Assert.True(runner.Start(AScript("one"), "an event", TimeSpan.FromSeconds(30)));
        Assert.True(runner.Start(AScript("two"), "an event", TimeSpan.FromSeconds(30)));

        finish.Set();
        Assert.True(SpinUntil(() => !runner.IsRunning("one") && !runner.IsRunning("two")));
    }

    [Fact]
    public void ARunStillGoingAtTheTimeLimitIsEnded()
    {
        using var never = new ManualResetEventSlim(false);
        FakeProcess? started = null;
        var runner = new ScriptRunner(_ => started = new FakeProcess(never, everFinishes: false));

        var outcome = runner.Run(AScript(), TimeSpan.FromMilliseconds(50));

        Assert.Equal(ScriptOutcomeKind.TimedOut, outcome.Kind);
        Assert.True(started!.WasKilled, "the run reached the limit and was not ended.");
        Assert.True(started.WasDisposed, "the ended run was not cleaned up.");
    }

    [Fact]
    public void ARunThatFinishesInsideTheLimitIsNotEnded()
    {
        using var finish = new ManualResetEventSlim(true);
        FakeProcess? started = null;
        var runner = new ScriptRunner(_ => started = new FakeProcess(finish, everFinishes: true));

        var outcome = runner.Run(AScript(), TimeSpan.FromSeconds(30));

        Assert.Equal(ScriptOutcomeKind.Succeeded, outcome.Kind);
        Assert.False(started!.WasKilled, "a run that finished in time was ended anyway.");
    }

    /// <summary>A non-zero exit code is a failure; the time limit is a different failure. Both are
    /// told apart from success, because the notification latch turns on the distinction.</summary>
    [Fact]
    public void ANonZeroExitCodeCountsAsAFailure()
    {
        using var finish = new ManualResetEventSlim(true);
        var runner = new ScriptRunner(_ => new FakeProcess(finish, everFinishes: true, exitCode: 3));

        var outcome = runner.Run(AScript(), TimeSpan.FromSeconds(30));

        Assert.Equal(ScriptOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(3, outcome.ExitCode);
        Assert.False(outcome.Ok);
    }

    /// <summary>A script that cannot be started at all is a failure rather than a fault escaping into
    /// a battery-report handler or an OS lid callback.</summary>
    [Fact]
    public void AScriptThatCannotBeStartedIsAFailureRatherThanAFault()
    {
        var runner = new ScriptRunner(_ => throw new InvalidOperationException("powershell.exe is missing"));

        var outcome = runner.Run(AScript(), TimeSpan.FromSeconds(30));

        Assert.Equal(ScriptOutcomeKind.DidNotStart, outcome.Kind);
        Assert.Contains("powershell.exe", outcome.Reason, StringComparison.Ordinal);
    }

    private static bool SpinUntil(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10));
}
