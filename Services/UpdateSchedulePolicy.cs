using System.Text.Json.Serialization;
using ZeroZero.Primitives;
using ZeroZero.Update;
using ZeroZero.Update.Win32;

namespace ChargeKeeper.Services;

/// <summary>How often the background check asks whether a newer version has been released. The
/// document stores the member name, so renaming a member resets every installation's
/// choice.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum UpdateCheckCadence
{
    /// <summary>Once an hour, for as long as the application runs.</summary>
    EveryHour,

    /// <summary>Once a day. What every installation does before the choice exists.</summary>
    EveryDay,

    /// <summary>The run shortly after start, and nothing after it.</summary>
    AtStartupOnly,
}

/// <summary>The shared unattended-update policy's options, and one fixed wording per reason not to
/// install: the component logs a refusal once per reason and ends "did not start because …" with it.</summary>
internal static class UpdateSchedulePolicy
{
    /// <summary>Delayed so the first check does not slow the cold-start path.</summary>
    internal static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);

    internal const string SwitchedOff = "installing automatically is switched off";

    internal const string LidCloseWaitRunning = "a lid-close wait is running";

    internal const string KeepAwakeRunning = "a Keep Awake session is running";

    /// <summary>Enabled whatever the Settings switch says: the check also keeps the tray line current.</summary>
    internal static UnattendedUpdateOptions Options(
        UpdateCheckCadence cadence, Action shutdown, Func<bool> installAutomatically, InstallHolds holds,
        Action<UnattendedTick> tickReported, ILogSink log) => new()
    {
        Enabled        = true,
        InitialDelay   = FirstCheckDelay,
        Cadence        = cadence == UpdateCheckCadence.AtStartupOnly ? CheckCadence.Once : CheckCadence.Periodic,
        CheckInterval  = cadence == UpdateCheckCadence.EveryHour ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24),
        Shutdown       = shutdown,
        // Not an install hold: the holds also gate an install a person asked for.
        MayDownloadNow = _ => installAutomatically() ? InstallMoment.Now : InstallMoment.NotNow(SwitchedOff),
        Holds          = holds,
        TickReported   = tickReported,
        Log            = log,
    };
}
