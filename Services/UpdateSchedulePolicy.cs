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

/// <summary>
/// The options the shared unattended-update policy runs on, and the application's own reasons for
/// refusing an install at the moment it would start. The policy is the whole background update
/// mechanism: it checks on the chosen cadence, reports every tick for the tray line, and installs
/// once the machine is free and nothing here refuses.
/// </summary>
/// <remarks>Pure, so every cadence and every refusal is testable without a policy running.</remarks>
internal static class UpdateSchedulePolicy
{
    /// <summary>Delayed so the first check does not slow the cold-start path.</summary>
    internal static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);

    /// <summary>The refusal while the Settings switch is off. The policy always runs, because it is
    /// also what keeps the tray line current, so the switch is a standing refusal.</summary>
    internal const string SwitchedOff = "installing automatically is switched off";

    internal const string FocusSessionRunning = "a focus session is running";

    /// <summary>The lid is shut, so the machine reads as free exactly when it is on its way to
    /// sleep.</summary>
    internal const string LidCloseWaitRunning = "a lid-close wait is running";

    /// <summary>The policy's options for the chosen cadence. Enabled whatever the Settings switch
    /// says: with it off the policy still checks and still reports, and <see cref="MayInstallNow"/>
    /// refuses every install.</summary>
    internal static UnattendedUpdateOptions Options(
        UpdateCheckCadence cadence, Action shutdown, Func<ReleaseInfo, InstallMoment> mayInstallNow,
        Action<UnattendedTick> tickReported, ILogSink log) => new()
    {
        Enabled       = true,
        InitialDelay  = FirstCheckDelay,
        Cadence       = cadence == UpdateCheckCadence.AtStartupOnly ? CheckCadence.Once : CheckCadence.Periodic,
        CheckInterval = cadence == UpdateCheckCadence.EveryHour ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24),
        Shutdown      = shutdown,
        MayInstallNow = mayInstallNow,
        TickReported  = tickReported,
        Log           = log,
    };

    /// <summary>Whether an installer may start now, asked by the policy once the installer is
    /// verified and the machine is free. Each reason is one fixed wording, because the policy logs a
    /// refusal once per reason.</summary>
    internal static InstallMoment MayInstallNow(bool installAutomatically, bool focusRunning, bool lidWaitRunning)
    {
        if (!installAutomatically) return InstallMoment.NotNow(SwitchedOff);
        if (focusRunning)          return InstallMoment.NotNow(FocusSessionRunning);
        if (lidWaitRunning)        return InstallMoment.NotNow(LidCloseWaitRunning);

        return InstallMoment.Now;
    }
}
