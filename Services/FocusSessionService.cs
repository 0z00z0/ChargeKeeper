using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ChargeKeeper.Services;

/// <summary>
/// The focus session as one mechanism: the levers, the record on disk and the clock that ends it.
/// The Settings page, the tray and an MQTT command all resolve through here.
/// </summary>
/// <remarks>Nothing on the machine can end a session, by design. The only ways out are Home
/// Assistant's staged cancel, the session's own duration, and removing the firewall rules by hand —
/// which USAGE.md says how to do.</remarks>
internal static class FocusSessionService
{
    /// <summary>Often enough that the ten-second confirm window is reported while it stands, and
    /// cheap enough to leave running: each tick is a comparison against the clock.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private static readonly FirewallBlockPark _firewall =
        new(new WindowsFirewallPolicy(), new SettingsFirewallBlockRecord(),
            (what, cause) => PowerLog.Event(what, cause));

    // Set before Start, so the lever can be pointed at the broker the publisher actually uses.
    private static Func<(string? Host, int? Port)?> _broker = () => null;

    private static readonly FocusSessionEngine _engine = new(
        new FocusNetworkLever(_firewall, new LiveFocusNetworkTargets(() => _broker()),
                              (what, cause) => PowerLog.Event(what, cause),
                              () => SettingsService.Read(s => s.FocusAllowedPrograms.ToList())),
        new FocusScreenLever(() => ScreenBrightnessService.IsSupported,
                             ScreenBrightnessService.Set,
                             ScreenBrightnessService.Restore),
        new FocusCoverLever(() => ScreenCoverService.HasDisplay,
                            ScreenCoverService.Show,
                            ScreenCoverService.Hide),
        new FocusInputLever(InputBlock.Refusal, InputBlock.Take, InputBlock.Release),
        new SettingsFocusSessionRecord(),
        () => DateTimeOffset.Now,
        (what, cause) => PowerLog.Event(what, cause),
        FocusHistoryService.Record);

    private static Timer? _timer;

    /// <summary>Raised after the session or its stage moves.</summary>
    public static event Action? Changed
    {
        add    => _engine.Changed += value;
        remove => _engine.Changed -= value;
    }

    /// <summary>The session as every surface reads it.</summary>
    public static FocusSnapshot Current => _engine.Snapshot();

    public static bool IsRunning => _engine.Snapshot().IsRunning;

    /// <summary>
    /// Puts back whatever a previous run left displaced, resumes or ends the session it left, and
    /// starts the clock.
    /// </summary>
    /// <param name="broker">The broker host and port the network lever writes its exception against.
    /// A null port is Automatic, which the lever refuses to arm on.</param>
    /// <remarks>Runs after <see cref="ScreenBrightnessService.Start"/>, which puts a dimmed display
    /// back first: a session that is resuming then dims it again and parks the level it found, which
    /// is the level the session is owed to put back.</remarks>
    public static void Start(Func<(string? Host, int? Port)?> broker)
    {
        _broker = broker;

        // settings.json roams, so it can arrive from another machine carrying neither record while
        // this machine is still blocked and dimmed.
        SettingsService.Reloaded += KeepRecords;

        _engine.Start();
        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    /// <summary>Starts a session from the defaults the settings hold. The four lever choices are
    /// whatever was last left in them; the duration is <paramref name="minutes"/> where a surface
    /// asked for one, and the stored default otherwise.</summary>
    /// <param name="minutes">The duration chosen in the dashboard's start box. Null from Home
    /// Assistant, which sets the duration through its own number instead.</param>
    public static FocusArmOutcome Arm(string cause, int? minutes = null)
    {
        var (stored, network, screen, cover, input) = SettingsService.Read(
            s => (s.FocusSessionMinutes, s.FocusBlocksNetwork, s.FocusDimsScreen, s.FocusCoversScreen,
                  s.FocusBlocksInput));
        return _engine.Arm(FocusStartRequest.Minutes(minutes, stored), network, screen, cover, input,
                           cause);
    }

    public static void RequestCancel(string cause) => _engine.RequestCancel(cause);

    /// <summary>Whether a lever switch may be changed. Refused while a session runs: a lever turned
    /// off part-way through would leave its record parked with nothing owning it.</summary>
    public static bool LeversAreLocked => IsRunning;

    public static void Stop()
    {
        SettingsService.Reloaded -= KeepRecords;
        _timer?.Dispose();
        _timer = null;
        // The cover is a window and dies with the process anyway; taking it down here keeps the
        // shutdown ordered rather than relying on that. The session itself is untouched — its record
        // stays on disk and the next start resumes or ends it.
        ScreenCoverService.Hide("the application is closing");

        // The input block is released here rather than left to the process ending, so a machine that
        // answers is not waiting on what a kill does to a block nobody can measure from inside it.
        InputBlock.Release("the application is closing");
    }

    private static void Tick()
    {
        try
        {
            _engine.Tick();

            // The input block lapses unless this pushes its deadline forward, so the application
            // hanging lifts it within seconds instead of leaving a machine nobody can type on.
            var session = _engine.Snapshot();
            if (session.IsRunning && session.BlocksInput && session.EndsAt is { } ends)
                InputBlock.Renew(ends);
        }
        catch (Exception ex) { AppLog.Error("FocusSessionService.Tick", ex); }
    }

    private static void KeepRecords()
    {
        _engine.KeepRecord();
        _firewall.KeepRecord();
    }
}

/// <summary>What duration a start request runs for. Its own type because two surfaces ask for a
/// session and only one of them names a duration.</summary>
internal static class FocusStartRequest
{
    /// <summary>The duration to use: the one chosen in the dashboard's start box where there is one,
    /// the stored default otherwise, and never outside the range a session accepts.</summary>
    internal static int Minutes(int? chosen, int storedDefault) =>
        Math.Clamp(chosen ?? storedDefault,
                   FocusSessionEngine.MinMinutes, FocusSessionEngine.MaxMinutes);
}

/// <summary>The live broker and resolver addresses the network lever writes its exceptions
/// against.</summary>
internal sealed class LiveFocusNetworkTargets(Func<(string? Host, int? Port)?> broker)
    : IFocusNetworkTargets
{
    public string? BrokerHost() => broker()?.Host;

    public int? BrokerPort() => broker()?.Port;

    public string Resolve(string host)
    {
        // An address needs no lookup, and a machine whose broker is named by address needs no
        // resolver exception either.
        if (IPAddress.TryParse(host, out var literal)) return literal.ToString();

        try
        {
            return string.Join(',', Dns.GetHostAddresses(host)
                                       .Where(Routable)
                                       .Select(a => a.ToString())
                                       .Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            AppLog.Error($"LiveFocusNetworkTargets.Resolve({host})", ex);
            return "";
        }
    }

    public string Resolvers()
    {
        try
        {
            return string.Join(',', NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().DnsAddresses)
                .Where(Routable)
                .Select(a => a.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            AppLog.Error("LiveFocusNetworkTargets.Resolvers", ex);
            return "";
        }
    }

    /// <summary>Whether an address needs a rule at all. Loopback is never filtered by Windows
    /// Firewall on any of these settings, so a rule naming it would be dead weight.</summary>
    private static bool Routable(IPAddress address) =>
        !IPAddress.IsLoopback(address)
        && address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
}
