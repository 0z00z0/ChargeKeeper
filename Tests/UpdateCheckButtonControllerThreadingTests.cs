using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// <see cref="UI.UpdateCheckButtonController"/>.Follow runs on whichever thread raised the shared
/// update check — the component's own background scheduler included, not only a button click — so
/// every touch of the button it drives has to cross back onto the UI thread. A dependency-property
/// write from the wrong thread throws a COM exception (0x8001010E) that crashed in the field. The
/// control cannot be constructed without a display, so the invariant is pinned against the shipped
/// source rather than driven live.
/// </summary>
public class UpdateCheckButtonControllerThreadingTests
{
    private static readonly string ControllerPath =
        System.IO.Path.Combine("UI", "UpdateCheckButtonController.cs");

    [Fact]
    public void FollowTouchesTheButtonOnlyThroughRunOnUi()
    {
        string body = RepoFiles.MethodBody(ControllerPath, "private async void Follow(Task<UpdateFlowRun> check)");

        // Every dispatched call is removed whole, arguments included; what remains is whatever Follow
        // still does directly on its own calling thread. A Show(...) surviving that means a completion
        // path paints the button without crossing onto the UI thread first.
        string withDispatchedCallsRemoved = StripBalancedCalls(body, "RunOnUi(");

        Assert.DoesNotContain("Show(", withDispatchedCallsRemoved, StringComparison.Ordinal);
    }

    [Fact]
    public void RunOnUiRechecksLivenessInsideTheCallbackAndGuardsItsOwnEnqueue()
    {
        // The window can close between Follow deciding to dispatch and the callback actually running,
        // so the liveness flag has to be read again inside the queued action, not only before it is
        // queued — and the enqueue call itself is guarded, matching every other RunOnUi in the app.
        string body = RepoFiles.MethodBody(ControllerPath, "private void RunOnUi(Action action)");

        Assert.Contains("DispatcherQueue", body, StringComparison.Ordinal);
        Assert.Contains("_detached", body, StringComparison.Ordinal);
        Assert.Contains("catch", body, StringComparison.Ordinal);
    }

    /// <summary>Removes every call to <paramref name="calleeWithOpenParen"/>, its arguments included,
    /// so a callee found only inside another call cannot be mistaken for one made directly.</summary>
    private static string StripBalancedCalls(string source, string calleeWithOpenParen)
    {
        while (true)
        {
            int start = source.IndexOf(calleeWithOpenParen, StringComparison.Ordinal);
            if (start < 0) return source;

            int open = start + calleeWithOpenParen.Length - 1;
            int depth = 0;
            int end = -1;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '(') depth++;
                else if (source[i] == ')' && --depth == 0) { end = i; break; }
            }

            if (end < 0)
                throw new InvalidOperationException($"Unbalanced call to {calleeWithOpenParen} in source.");
            source = source.Remove(start, end - start + 1);
        }
    }
}
