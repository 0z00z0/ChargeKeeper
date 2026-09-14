using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The About window and the Settings window each check for updates as they open. Open together they
/// must share one request, and opening either again must ask GitHub again rather than repeat an old
/// answer.
/// </summary>
public class UpdateCheckCoordinatorTests
{
    private static UpdateCheckService.CheckOutcome UpToDate() =>
        UpdateCheckService.CheckOutcome.Release(false, "1.0.0", "1.0.0", "https://example.invalid", null, null);

    [Fact]
    public async Task TwoRequestsWhileACheckRuns_ShareOneCheck()
    {
        int checks = 0;
        var answer = new TaskCompletionSource<UpdateCheckService.CheckOutcome>();
        var coordinator = new UpdateCheckCoordinator(() => { checks++; return answer.Task; });

        var first  = coordinator.Run();
        var second = coordinator.Run();

        Assert.Same(first, second);
        Assert.Equal(1, checks);

        answer.SetResult(UpToDate());
        Assert.Equal(UpdateStatus.UpToDate, (await first).Status);
        Assert.Equal(UpdateStatus.UpToDate, (await second).Status);
    }

    [Fact]
    public async Task ARequestAfterACheckEnded_StartsAFreshCheck()
    {
        int checks = 0;
        var coordinator = new UpdateCheckCoordinator(() => { checks++; return Task.FromResult(UpToDate()); });

        await coordinator.Run();
        await coordinator.Run();

        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task EveryCheckAnnouncesItselfOnce()
    {
        var started = new List<Task<UpdateCheckService.CheckOutcome>>();
        var answer  = new TaskCompletionSource<UpdateCheckService.CheckOutcome>();
        var coordinator = new UpdateCheckCoordinator(() => answer.Task);
        coordinator.CheckStarted += started.Add;

        var check = coordinator.Run();
        coordinator.Run();

        Assert.Same(check, Assert.Single(started));

        answer.SetResult(UpToDate());
        await check;
    }
}
