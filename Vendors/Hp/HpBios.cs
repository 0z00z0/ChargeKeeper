using System.Management;

namespace ChargeKeeper.Vendors.Hp;

/// <summary>One HP BIOS enumeration setting, as reported by <c>HP_BIOSEnumeration</c>.</summary>
/// <param name="Name">The setting's BIOS name, e.g. "Battery Health Manager".</param>
/// <param name="CurrentValue">The currently selected option.</param>
/// <param name="PossibleValues">Every option the firmware accepts for this setting.</param>
/// <param name="IsReadOnly">
/// True when the firmware refuses writes. HP marks several battery settings read-only even
/// though they are visible — notably "Adaptive Battery Optimizer" on the EliteBook 840 G8.
/// </param>
internal sealed record HpEnumSetting(
    string Name,
    string CurrentValue,
    IReadOnlyList<string> PossibleValues,
    bool IsReadOnly);

/// <summary>
/// Thin wrapper over HP's BIOS management surface, the WMI namespace
/// <c>root\HP\InstrumentedBIOS</c>.
///
/// Availability: HP ships this namespace on its COMMERCIAL lines (EliteBook, ProBook, ZBook)
/// and not on consumer SKUs (Pavilion, Envy), so its absence is an ordinary, expected outcome
/// rather than an error — every method here degrades to null/false instead of throwing, which
/// is what <see cref="IChargeThresholdProvider.Read"/>'s "null means unavailable" contract
/// requires of its callers.
///
/// Privileges: reads succeed from a NON-elevated token. Writes through
/// <see cref="SetSetting"/> require elevation, and additionally require the BIOS Setup
/// password when one is set (passed as <c>Password</c>; empty string when unset).
///
/// Unlike Lenovo — whose charge threshold is NOT reachable over WMI and needs the native
/// <c>LenPower.dll</c> RPC bridge — everything HP exposes here is plain managed WMI, so this
/// module ships no native component.
/// </summary>
internal static class HpBios
{
    /// <summary>HP's BIOS management namespace. Absent on consumer SKUs.</summary>
    private const string Namespace = @"root\HP\InstrumentedBIOS";

    /// <summary>Returned by <c>SetBIOSSetting</c> on success. Non-zero values are failures.</summary>
    private const uint Success = 0;

    /// <summary>
    /// Reads one enumeration setting, or <c>null</c> when the namespace, the class or the
    /// setting is absent — i.e. "this is not an HP commercial machine, or this firmware does
    /// not have that setting". Never throws.
    /// </summary>
    internal static HpEnumSetting? ReadEnumSetting(string name)
    {
        try
        {
            var scope = new ManagementScope(Namespace);
            scope.Connect();

            // Name is caller-supplied; escape it rather than trusting it, so a quote can never
            // change the shape of the query.
            var query = new ObjectQuery(
                $"SELECT * FROM HP_BIOSEnumeration WHERE Name='{EscapeWql(name)}'");

            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();

            foreach (var o in results)
            {
                using var mo = (ManagementObject)o;
                return new HpEnumSetting(
                    Name: name,
                    CurrentValue: AsString(mo, "CurrentValue") ?? AsString(mo, "Value") ?? string.Empty,
                    PossibleValues: AsStringArray(mo, "PossibleValues"),
                    IsReadOnly: AsBool(mo, "IsReadOnly"));
            }

            return null;
        }
        catch
        {
            // ManagementException (namespace/class absent), UnauthorizedAccessException,
            // COMException on a machine with a broken WMI repository. All mean "unavailable".
            return null;
        }
    }

    /// <summary>
    /// Writes a BIOS setting through <c>HP_BIOSSettingInterface.SetBIOSSetting</c>. Returns
    /// false on any failure rather than throwing.
    ///
    /// CAUTION: this changes firmware configuration. HP applies most battery settings only
    /// after a reboot, so a successful return does NOT mean the new value is in effect yet.
    /// </summary>
    /// <param name="name">The BIOS setting name, exactly as the firmware spells it.</param>
    /// <param name="value">One of the setting's <see cref="HpEnumSetting.PossibleValues"/>.</param>
    /// <param name="password">
    /// The BIOS Setup password, or empty when none is set. HP expects the bare string when no
    /// password exists; when one is set it must be given in the <c>&lt;utf-16/&gt;</c> prefixed
    /// form, which this method does not currently construct.
    /// </param>
    internal static bool SetSetting(string name, string value, string password = "")
    {
        try
        {
            var scope = new ManagementScope(Namespace);
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(
                scope, new ObjectQuery("SELECT * FROM HP_BIOSSettingInterface"));
            using var results = searcher.Get();

            foreach (var o in results)
            {
                using var mo = (ManagementObject)o;

                using var inParams = mo.GetMethodParameters("SetBIOSSetting");
                inParams["Name"] = name;
                inParams["Password"] = password;
                inParams["Value"] = value;

                using var outParams = mo.InvokeMethod("SetBIOSSetting", inParams, null);
                return outParams?["Return"] is uint ret && ret == Success;
            }

            return false;
        }
        catch
        {
            // Most commonly UnauthorizedAccessException when not elevated, or a
            // ManagementException when the namespace is absent.
            return false;
        }
    }

    /// <summary>Doubles single quotes, the only WQL string-literal escape.</summary>
    private static string EscapeWql(string value) => value.Replace("'", "''");

    private static string? AsString(ManagementObject mo, string property)
    {
        try { return mo[property] as string; }
        catch (ManagementException) { return null; }   // property absent on this firmware
    }

    private static IReadOnlyList<string> AsStringArray(ManagementObject mo, string property)
    {
        try
        {
            return mo[property] is string[] values ? values : [];
        }
        catch (ManagementException) { return []; }
    }

    /// <summary>
    /// HP reports IsReadOnly inconsistently across firmware revisions — sometimes a numeric
    /// 0/1, sometimes the string "0"/"1". Treat anything unrecognised as read-only, so an
    /// unexpected shape fails closed rather than letting a write be attempted blind.
    /// </summary>
    private static bool AsBool(ManagementObject mo, string property)
    {
        try
        {
            return mo[property] switch
            {
                bool b       => b,
                string s     => s is not ("0" or "false" or "False"),
                null         => true,
                var v        => Convert.ToInt64(v) != 0,
            };
        }
        catch { return true; }
    }
}
