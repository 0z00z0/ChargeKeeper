namespace ChargeKeeper.Helpers;

/// <summary>
/// The tray icon's identity to Windows. The shell stores each icon's settings — above all whether
/// it sits in the visible area or behind the overflow chevron — in a record keyed on this value,
/// so an installation keeps the position its owner chose only for as long as the value does not
/// move.
/// </summary>
internal static class TrayIconIdentity
{
    /// <summary>
    /// Generated once and fixed for the life of the product. NEVER regenerate it, and never derive
    /// it from the executable path, the version, the machine or anything else that can change: the
    /// shell treats a different value as a different icon, and every installation silently loses
    /// its chosen tray position with no way to recover it. A test pins this exact literal so a
    /// regenerated value fails the build rather than shipping.
    /// <para>Left unset, H.NotifyIcon hashes the executable's full path into a GUID, which is why
    /// moving the install folder used to cost every installation its position.</para>
    /// </summary>
    internal static readonly Guid Value = new("05290CC3-5F1D-4AD4-8F5D-722D2D0772A1");

    /// <summary>
    /// The second, display-only icon carrying the charge level as a number. Its own value, so the
    /// shell tracks the two icons separately and each keeps the position its owner chose for it.
    /// Fixed on the same terms as <see cref="Value"/>: never regenerated, never derived from
    /// anything that can move, and pinned as a literal by a test.
    /// </summary>
    internal static readonly Guid PercentageValue = new("3C0B6A57-9E44-4E1B-B0A2-6D8F4C21B7E9");
}
