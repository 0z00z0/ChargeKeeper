namespace ChargeKeeper.Services;

/// <summary>Pure decision for closing the hidden dashboard popup, extracted from <c>DashboardWindow</c>
/// so the rule is unit-testable without a live WinUI window.</summary>
internal static class DashboardIdlePolicy
{
    /// <summary>Hidden, the popup still holds a full XAML tree and composition surface (~5-15 MB private)
    /// to buy a cheaper re-show — a bad trade for a tray app that is idle most of the day.</summary>
    internal static readonly TimeSpan IdleCloseAfter = TimeSpan.FromMinutes(15);

    /// <summary><paramref name="isVisible"/> is load-bearing, not defensive: a DispatcherTimer tick queued
    /// before a tray click is still delivered afterwards, and would close the popup as the user opened it.</summary>
    public static bool ShouldClose(bool isVisible, TimeSpan sinceHidden) =>
        !isVisible && sinceHidden >= IdleCloseAfter;
}
