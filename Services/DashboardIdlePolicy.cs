namespace ChargeKeeper.Services;

/// <summary>
/// Pure decision for closing the hidden dashboard popup (issue #76): given whether the window is on
/// screen and how long it has been hidden, decide whether it should now be destroyed. Extracted from
/// <c>DashboardWindow</c> so the rule is unit-testable without a live WinUI window.
/// </summary>
internal static class DashboardIdlePolicy
{
    /// <summary>
    /// How long the popup may sit hidden before it is destroyed rather than retained. Hidden, it
    /// still holds a full XAML tree, the history graph control and a composition surface (~5-15 MB
    /// private) to buy a cheaper re-show — a bad trade for a tray app that is idle ~all day. The
    /// re-show path this falls back on is not a new risk: a GPU/compositor reset already destroys
    /// this window from below and the next tray click rebuilds it, so closing it deliberately only
    /// exercises a path that must already work. Its 5s refresh timer is stopped while hidden, so
    /// this reclaims memory only — there is no CPU to save and no reason to be aggressive.
    /// </summary>
    internal static readonly TimeSpan IdleCloseAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// True when a hidden popup has been idle long enough to close.
    /// <para><paramref name="isVisible"/> is load-bearing, not defensive: the close is driven by a
    /// DispatcherTimer, and a tick already queued on the dispatcher is still delivered after the
    /// user's tray click re-shows the window. Without this check that stale tick would close the
    /// popup out from under the user the instant they opened it.</para>
    /// </summary>
    public static bool ShouldClose(bool isVisible, TimeSpan sinceHidden) =>
        !isVisible && sinceHidden >= IdleCloseAfter;
}
