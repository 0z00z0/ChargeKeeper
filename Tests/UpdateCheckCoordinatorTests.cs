using ChargeKeeper.Services;
using Xunit;
using ZeroZero.Update.Win32;

namespace ChargeKeeper.Tests;

/// <summary>
/// The About window and the Settings window each check for updates as they open. Open together they
/// must share one request, and opening either again must ask GitHub again rather than repeat an old
/// answer.
/// </summary>
public class UpdateCheckCoordinatorTests
{
    private static UpdateFlowRun UpToDate() => new(UpdateFlowResult.UpToDate);

    [Fact]
    public async Task TwoRequestsWhileACheckRuns_ShareOneCheck()
    {
        int checks = 0;
        var answer = new TaskCompletionSource<UpdateFlowRun>();
        var coordinator = new UpdateCheckCoordinator(() => { checks++; return answer.Task; });

        var first  = coordinator.Run();
        var second = coordinator.Run();

        Assert.Same(first, second);
        Assert.Equal(1, checks);

        answer.SetResult(UpToDate());
        Assert.Equal(UpdateFlowResult.UpToDate, (await first).Result);
        Assert.Equal(UpdateFlowResult.UpToDate, (await second).Result);
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
}
