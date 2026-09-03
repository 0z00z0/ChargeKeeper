using ChargeKeeper.Services;
using Microsoft.Win32;

namespace ChargeKeeper.Helpers;

/// <summary>What the shell recorded for one tray icon before the application touched it. Null
/// <see cref="Previous"/> means the value was not there at all, which is how "not promoted" is
/// spelled — restoring it means deleting the value rather than writing a zero.</summary>
internal sealed class TrayPromotionMemory
{
    public string Icon     { get; set; } = "";
    public int?   Previous { get; set; }
}

/// <summary>Where an icon's promotion flag is kept. An interface because the only production store
/// is an undocumented registry key that a future Windows may move, and because the policy above it
/// is worth testing without a machine's live tray in the way.</summary>
internal interface ITrayPromotionStore
{
    /// <summary>1 promoted, 0 explicitly not, null for an icon the store has no record of.</summary>
    int? Read(Guid icon);

    /// <summary>Writes the flag, or removes it when <paramref name="value"/> is null. False when
    /// there was nothing to write to, which is not an error.</summary>
    bool Write(Guid icon, int? value);
}

/// <summary>
/// Moves a tray icon out of the overflow flyout into the visible tray. There is no supported
/// interface for this, which is what the setting's "(experimental)" says out loud, so every path
/// here degrades to doing nothing: an absent key, a shape that has changed, or a refusal leaves the
/// icons wherever Windows put them.
/// </summary>
internal static class TrayIconPromotion
{
    /// <summary>
    /// Applies <paramref name="promote"/> to every icon in <paramref name="icons"/>, recording what
    /// the store held first so switching the setting off can put it back. True when at least one
    /// icon's flag actually moved, which is what makes re-registering the icon worth doing.
    /// </summary>
    /// <param name="memory">The application's own record of what each icon carried before it was
    /// first promoted. Grown on the way in and emptied on the way out, so a restore survives a
    /// restart between the two.</param>
    internal static bool Apply(bool promote, IEnumerable<Guid> icons, IList<TrayPromotionMemory> memory,
                               ITrayPromotionStore store)
    {
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            return promote ? Promote(icons, memory, store) : Restore(memory, store);
        }
        catch (Exception ex)
        {
            // Never escapes: the icons staying where Windows put them is the whole failure mode.
            AppLog.Error("TrayIconPromotion.Apply", ex);
            return false;
        }
    }

    private static bool Promote(IEnumerable<Guid> icons, IList<TrayPromotionMemory> memory,
                                ITrayPromotionStore store)
    {
        bool moved = false;

        foreach (var icon in icons)
        {
            string key = Braced(icon);
            if (store.Read(icon) is 1) continue;           // already where the setting wants it

            int? before = store.Read(icon);
            if (!store.Write(icon, 1)) continue;           // nothing to write to; try again later

            moved = true;

            // Remembered once, after the first successful write and never again: a second pass
            // would record the value this class itself wrote and lose the original.
            if (!memory.Any(m => string.Equals(m.Icon, key, StringComparison.OrdinalIgnoreCase)))
                memory.Add(new TrayPromotionMemory { Icon = key, Previous = before });
        }

        return moved;
    }

    private static bool Restore(IList<TrayPromotionMemory> memory, ITrayPromotionStore store)
    {
        bool moved = false;

        foreach (var entry in memory.ToList())
        {
            if (!Guid.TryParse(entry.Icon, out var icon)) continue;
            if (store.Write(icon, entry.Previous)) moved = true;
        }

        // Cleared whatever the writes did: a memory kept after a refused restore would be replayed
        // on the next start against a machine that has since moved on.
        memory.Clear();
        return moved;
    }

    /// <summary>The spelling a store records, braced and upper case.</summary>
    internal static string Braced(Guid icon) => icon.ToString("B").ToUpperInvariant();
}

/// <summary>
/// The Windows 11 shell's own record. It keeps one subkey per icon under
/// <c>HKCU\Control Panel\NotifyIconSettings</c>, named by a hash it computes itself, carrying
/// <c>IconGuid</c>, <c>ExecutablePath</c>, <c>IconSnapshot</c> and <c>IsPromoted</c>.
/// </summary>
/// <remarks>
/// The subkey is found by matching <c>IconGuid</c> rather than by reproducing the hash: a hash the
/// application computed itself would be a second implementation of an undocumented one, and would
/// break silently the day it changed. An icon registered without a fixed identity records a
/// path-derived GUID there instead, which is the fragility issue #135 removed.
/// <para>Windows 10 keeps the equivalent as an obfuscated blob in the <c>TrayNotify</c> key and is
/// not supported: nothing is written there, and the setting simply does nothing.</para>
/// </remarks>
internal sealed class RegistryTrayPromotionStore : ITrayPromotionStore
{
    private const string RootPath  = @"Control Panel\NotifyIconSettings";
    private const string GuidValue = "IconGuid";
    private const string FlagValue = "IsPromoted";

    /// <summary>Whether this machine stores promotion where this class writes it. False on
    /// Windows 10 and on anything that has moved the key.</summary>
    internal static bool IsSupported
    {
        get
        {
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(RootPath);
                return root is not null;
            }
            catch (Exception ex)
            {
                AppLog.Error("TrayIconPromotion.IsSupported", ex);
                return false;
            }
        }
    }

    public int? Read(Guid icon)
    {
        try
        {
            using var entry = OpenEntry(icon, writable: false);
            return entry?.GetValue(FlagValue) as int?;
        }
        catch (Exception ex)
        {
            AppLog.Error("TrayIconPromotion.Read", ex);
            return null;
        }
    }

    public bool Write(Guid icon, int? value)
    {
        try
        {
            using var entry = OpenEntry(icon, writable: true);
            if (entry is null) return false;

            if (value is { } flag) entry.SetValue(FlagValue, flag, RegistryValueKind.DWord);
            else                   entry.DeleteValue(FlagValue, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("TrayIconPromotion.Write", ex);
            return false;
        }
    }

    /// <summary>The shell's own subkey for <paramref name="icon"/>. Null when the shell has never
    /// seen the icon, which is the ordinary state before its first registration.</summary>
    private static RegistryKey? OpenEntry(Guid icon, bool writable)
    {
        using var root = Registry.CurrentUser.OpenSubKey(RootPath);
        if (root is null) return null;

        string wanted = TrayIconPromotion.Braced(icon);
        foreach (string name in root.GetSubKeyNames())
        {
            string? recorded;
            using (var probe = root.OpenSubKey(name))
                recorded = probe?.GetValue(GuidValue) as string;

            if (recorded is null || !string.Equals(recorded, wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            return root.OpenSubKey(name, writable);
        }

        return null;
    }
}
