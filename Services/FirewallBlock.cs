using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>A Windows Firewall profile. The three Windows itself declares; a machine carries all of
/// them whether or not it is on a network each describes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum FirewallProfile
{
    Domain,
    Private,
    Public,
}

/// <summary>One profile's two settings: what happens to outbound traffic no rule names, and whether
/// unsolicited inbound is refused outright.</summary>
internal readonly record struct FirewallProfileSetting(
    FirewallProfile Profile, bool BlockOutbound, bool BlockAllInbound);

/// <summary>Which way a rule points.</summary>
internal enum FirewallDirection { Inbound, Outbound }

/// <summary>An allow rule narrow enough to be one exception: one protocol, one set of remote
/// addresses, one set of remote ports.</summary>
/// <param name="RemoteAddresses">Comma-separated, as Windows Firewall spells an address list.</param>
internal sealed record FirewallAllowRule(
    string Name, string Group, string Description, FirewallDirection Direction,
    int Protocol, string RemoteAddresses, string RemotePorts);

/// <summary>The Windows Firewall settings a focus session displaces, behind an interface so the
/// lever can be exercised without touching a machine's firewall.</summary>
internal interface IFirewallPolicy
{
    /// <summary>Every profile's current settings, or null when any of them cannot be read. All or
    /// nothing: a half-read set cannot be put back.</summary>
    IReadOnlyList<FirewallProfileSetting>? ReadAll();

    /// <summary>Writes one profile's two settings. False when either did not land.</summary>
    bool Write(FirewallProfileSetting setting);

    /// <summary>Adds one allow rule. False when nothing was added.</summary>
    bool AddAllowRule(FirewallAllowRule rule);

    /// <summary>Removes every rule this feature owns, by name. True when none is left, including
    /// when there was none to begin with.</summary>
    bool RemoveOwnRules();
}

/// <summary>Where the profile settings displaced by a block are kept so a crash cannot lose
/// them.</summary>
internal interface IFirewallBlockRecord
{
    IReadOnlyList<FirewallProfileSetting>? Read();

    /// <summary>False when the record did not reach disk.</summary>
    bool Save(IReadOnlyList<FirewallProfileSetting> settings);

    void Clear();
}

/// <summary>The two named exceptions a blocked session keeps open, and the names they carry in the
/// Windows Firewall console.</summary>
/// <remarks>The names are the published way out: somebody stuck removes these by hand, so they say
/// what they are in plain words rather than needing to be worked out under pressure. They are also
/// what <see cref="IFirewallPolicy.RemoveOwnRules"/> matches on, so changing one strands the rules
/// an earlier run left behind.</remarks>
internal static class FocusFirewallRules
{
    public const string Group = "ChargeKeeper focus session";
    public const string BrokerRuleName = "ChargeKeeper focus session: broker";
    public const string ResolverRuleName = "ChargeKeeper focus session: name resolution";

    /// <summary>Every rule name this feature owns, for removal.</summary>
    public static IReadOnlyList<string> Names { get; } = [BrokerRuleName, ResolverRuleName];

    public const int ProtocolTcp = 6;
    public const int ProtocolUdp = 17;

    /// <summary>The exceptions for one session: the broker, and the name resolution the broker
    /// address was found through. The resolver rule is left out where no resolver address is known,
    /// which is a machine whose broker is named by address and needs no lookup at all.</summary>
    /// <param name="brokerAddresses">The broker's resolved addresses, comma-separated.</param>
    /// <param name="resolverAddresses">The configured resolvers, comma-separated, or empty.</param>
    public static IReadOnlyList<FirewallAllowRule> For(
        string brokerAddresses, int brokerPort, string resolverAddresses)
    {
        var rules = new List<FirewallAllowRule>
        {
            new(BrokerRuleName, Group,
                "Lets ChargeKeeper reach its MQTT broker while a focus session blocks everything else. "
              + "Removing this rule ends that exception.",
                FirewallDirection.Outbound, ProtocolTcp, brokerAddresses,
                brokerPort.ToString(CultureInfo.InvariantCulture)),
        };

        // UDP alone: a broker host name answers in one small record, which is never truncated, so the
        // TCP fallback a resolver keeps for large answers is never reached here.
        if (resolverAddresses.Length > 0)
            rules.Add(new(ResolverRuleName, Group,
                "Lets ChargeKeeper look up its MQTT broker's address while a focus session blocks "
              + "everything else. Removing this rule ends that exception.",
                FirewallDirection.Outbound, ProtocolUdp, resolverAddresses, "53"));

        return rules;
    }
}

/// <summary>
/// Blocks every network connection but the two named exceptions, and puts the firewall back exactly
/// as it was when the block lifts.
/// </summary>
/// <remarks>
/// The same shape as <see cref="BatterySleepPark"/> and <see cref="ScreenBrightnessPark"/>: what is
/// displaced reaches the record before the firewall changes, and is never re-captured while the
/// record stands, so a crash between the two cannot turn "blocked" into the state restored later. A
/// record left behind by a run that died is put back at the next start — the firewall keeps a block
/// across a restart on its own, so nothing else would.
/// </remarks>
internal sealed class FirewallBlockPark(
    IFirewallPolicy policy,
    IFirewallBlockRecord record,
    Action<string, string> log)
{
    private readonly Lock _gate = new();

    // What this process displaced, kept so a settings document replaced underneath the process
    // cannot lose the original — the same rule the battery sleep timeout follows.
    private IReadOnlyList<FirewallProfileSetting>? _parked;

    /// <summary>Whether a firewall state is waiting to be put back.</summary>
    public bool Holding
    {
        get { lock (_gate) return record.Read() is not null; }
    }

    /// <summary>
    /// Sets every profile to block, with <paramref name="exceptions"/> allowed through. False when
    /// nothing was displaced, which leaves the firewall exactly as it was found.
    /// </summary>
    public bool Engage(IReadOnlyList<FirewallAllowRule> exceptions, string cause)
    {
        ArgumentNullException.ThrowIfNull(exceptions);

        lock (_gate)
        {
            // Already ours: the record describes what to put back and is never re-captured, so a
            // second engage must not overwrite it with the blocked state it is looking at. What it
            // does write again is the block, covering a run that died between saving the record and
            // applying it. This is not the restart path — a session resuming leaves the firewall
            // exactly as it finds it, because removing the rules by hand is the published way out.
            if (record.Read() is { Count: > 0 } held)
            {
                if (!Apply(held, exceptions, cause)) return false;
                _parked = held;
                log("Network block put back on every firewall profile, with the broker and its name "
                  + "resolution left open", cause);
                return true;
            }

            if (policy.ReadAll() is not { Count: > 0 } original)
            {
                log("The network is left open: the firewall's own settings could not be read", cause);
                return false;
            }

            if (!record.Save(original))
            {
                log("The network is left open: the firewall's own settings could not be saved first, "
                  + "so they could not be put back after a crash", cause);
                return false;
            }

            if (!Apply(original, exceptions, cause))
            {
                Undo(original, cause);
                return false;
            }

            _parked = original;
            log("Network blocked on every firewall profile, with the broker and its name resolution "
              + "left open", cause);
            return true;
        }
    }

    /// <summary>Adds the exceptions and writes the block onto every recorded profile. Called with the
    /// lock held. The exceptions go first: the reverse order leaves a window in which the machine is
    /// cut off with no way back, and that window is the whole of what a failure here costs.</summary>
    private bool Apply(
        IReadOnlyList<FirewallProfileSetting> profiles, IReadOnlyList<FirewallAllowRule> exceptions,
        string cause)
    {
        // Rules left by a run that died part-way through, so a re-apply cannot end with two of each.
        policy.RemoveOwnRules();

        foreach (var rule in exceptions)
            if (!policy.AddAllowRule(rule))
            {
                log($"The network is left open: the '{rule.Name}' exception could not be added", cause);
                return false;
            }

        foreach (var profile in profiles)
            if (!policy.Write(profile with { BlockOutbound = true, BlockAllInbound = true }))
            {
                log($"The network is left open: the {profile.Profile} profile could not be set to block", cause);
                return false;
            }

        return true;
    }

    /// <summary>Puts the recorded settings back and removes the exceptions. True when nothing is
    /// owed. False only when a write failed, which leaves the record for the next start.</summary>
    public bool Lift(string cause)
    {
        lock (_gate)
        {
            if (record.Read() is not { Count: > 0 } saved)
            {
                _parked = null;
                // A rule left behind by a run that died before its record was written owns nothing,
                // and would otherwise stand in the console for good.
                policy.RemoveOwnRules();
                return true;
            }

            bool landed = true;
            foreach (var profile in saved)
                if (!policy.Write(profile)) landed = false;

            if (!policy.RemoveOwnRules()) landed = false;

            if (!landed)
            {
                log("The firewall could not be put back as it was — retrying at next start", cause);
                return false;
            }

            record.Clear();
            _parked = null;
            log("The firewall is back as it was and the focus session's own rules are gone", cause);
            return true;
        }
    }

    /// <summary>Re-saves what this process displaced when the record has gone missing — settings.json
    /// can be replaced underneath the process while the firewall still carries the block.</summary>
    public void KeepRecord()
    {
        lock (_gate)
        {
            if (_parked is { Count: > 0 } parked && record.Read() is null && record.Save(parked))
                AppLog.Info("Focus: reloaded settings carried no saved firewall state while one is still "
                          + "displaced — restoring the record from this session.");
        }
    }

    /// <summary>Rolls a half-applied engage back to what was found. Called with the lock held.</summary>
    private void Undo(IReadOnlyList<FirewallProfileSetting> original, string cause)
    {
        foreach (var profile in original) policy.Write(profile);
        policy.RemoveOwnRules();
        record.Clear();
        _parked = null;
        log("The firewall is back as it was after the block could not be completed", cause);
    }
}

/// <summary>
/// The live Windows Firewall, over the COM interface behind Windows Defender Firewall with Advanced
/// Security.
/// </summary>
/// <remarks>
/// Late-bound rather than through an interop assembly: the application already runs elevated, the
/// interface is stable, and binding by name avoids carrying a generated wrapper for six members.
/// Spawning <c>netsh advfirewall</c> is the alternative and is worse — a process per call, and
/// output whose wording follows the machine's display language.
/// </remarks>
internal sealed class WindowsFirewallPolicy : IFirewallPolicy
{
    private const string PolicyProgId = "HNetCfg.FwPolicy2";
    private const string RuleProgId = "HNetCfg.FWRule";

    // NET_FW_ACTION
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;

    // NET_FW_RULE_DIRECTION
    private const int DirectionIn = 1;
    private const int DirectionOut = 2;

    // NET_FW_PROFILE_TYPE2
    private const int ProfileDomain = 1;
    private const int ProfilePrivate = 2;
    private const int ProfilePublic = 4;
    private const int ProfileAll = 0x7FFFFFFF;

    private static int TypeOf(FirewallProfile profile) => profile switch
    {
        FirewallProfile.Domain  => ProfileDomain,
        FirewallProfile.Private => ProfilePrivate,
        _                       => ProfilePublic,
    };

    public IReadOnlyList<FirewallProfileSetting>? ReadAll()
    {
        try
        {
            object policy = Policy();
            var read = new List<FirewallProfileSetting>();
            foreach (var profile in Enum.GetValues<FirewallProfile>())
            {
                int type = TypeOf(profile);
                int outbound = Convert.ToInt32(
                    Get(policy, "DefaultOutboundAction", type), CultureInfo.InvariantCulture);
                bool inbound = Convert.ToBoolean(
                    Get(policy, "BlockAllInboundTraffic", type), CultureInfo.InvariantCulture);
                read.Add(new FirewallProfileSetting(profile, outbound == ActionBlock, inbound));
            }
            return read;
        }
        catch (Exception ex)
        {
            AppLog.Error("WindowsFirewallPolicy.ReadAll", ex);
            return null;
        }
    }

    public bool Write(FirewallProfileSetting setting)
    {
        try
        {
            object policy = Policy();
            int type = TypeOf(setting.Profile);
            Set(policy, "DefaultOutboundAction", type, setting.BlockOutbound ? ActionBlock : ActionAllow);
            Set(policy, "BlockAllInboundTraffic", type, setting.BlockAllInbound);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"WindowsFirewallPolicy.Write({setting.Profile})", ex);
            return false;
        }
    }

    public bool AddAllowRule(FirewallAllowRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        try
        {
            object entry = Create(RuleProgId);
            Set(entry, "Name", rule.Name);
            Set(entry, "Description", rule.Description);
            Set(entry, "Grouping", rule.Group);
            Set(entry, "Direction", rule.Direction == FirewallDirection.Inbound ? DirectionIn : DirectionOut);
            Set(entry, "Action", ActionAllow);
            Set(entry, "Protocol", rule.Protocol);
            Set(entry, "RemoteAddresses", rule.RemoteAddresses);
            Set(entry, "RemotePorts", rule.RemotePorts);
            Set(entry, "Profiles", ProfileAll);
            Set(entry, "Enabled", true);

            object rules = Get(Policy(), "Rules")
                ?? throw new InvalidOperationException("the firewall exposed no rule collection");
            Call(rules, "Add", entry);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"WindowsFirewallPolicy.AddAllowRule({rule.Name})", ex);
            return false;
        }
    }

    public bool RemoveOwnRules()
    {
        object rules;
        try
        {
            rules = Get(Policy(), "Rules")
                ?? throw new InvalidOperationException("the firewall exposed no rule collection");
        }
        catch (Exception ex)
        {
            AppLog.Error("WindowsFirewallPolicy.RemoveOwnRules", ex);
            return false;
        }

        // Remove throws for a name that is not there, which is the ordinary case on a lift that
        // follows a failed engage — so each name is taken on its own and an absent one is not a
        // failure. A name that is there and will not go is, and is reported.
        foreach (string name in FocusFirewallRules.Names)
        {
            try { Call(rules, "Remove", name); }
            catch (Exception ex)
            {
                if (Exists(rules, name))
                {
                    AppLog.Error($"WindowsFirewallPolicy.RemoveOwnRules({name})", ex);
                    return false;
                }
            }
        }
        return true;
    }

    private static bool Exists(object rules, string name)
    {
        try { return Get(rules, "Item", name) is not null; }
        catch { return false; }
    }

    // The RCW is left to the finaliser rather than released by hand: this runs a handful of times per
    // session, and an explicit release throws where built-in COM interop is switched off.
    private static object Policy() => Create(PolicyProgId);

    private static object Create(string progId) =>
        Type.GetTypeFromProgID(progId) is { } type && Activator.CreateInstance(type) is { } instance
            ? instance
            : throw new PlatformNotSupportedException($"{progId} is not registered on this machine.");

    private static object? Get(object target, string name, params object[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, arguments,
                                      CultureInfo.InvariantCulture);

    private static void Set(object target, string name, params object[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, arguments,
                                      CultureInfo.InvariantCulture);

    private static object? Call(object target, string name, params object?[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments,
                                      CultureInfo.InvariantCulture);
}

/// <summary>The record in settings.json, in the Focus section.</summary>
internal sealed class SettingsFirewallBlockRecord : IFirewallBlockRecord
{
    public IReadOnlyList<FirewallProfileSetting>? Read() =>
        SettingsService.Read(s => s.FocusSavedFirewall is { Count: > 0 } saved ? saved.ToList() : null);

    public bool Save(IReadOnlyList<FirewallProfileSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SettingsService.Update(s => s.FocusSavedFirewall = [.. settings]);
    }

    public void Clear() => SettingsService.Update(s => s.FocusSavedFirewall = null);
}
