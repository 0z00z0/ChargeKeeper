using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroZero.Config.Watch;

namespace ChargeKeeper.Services;

/// <summary>A committed settings change, carrying whether it moved anything a subscriber mirrors
/// outside this process.</summary>
/// <param name="IsMaterial">False only when every property that moved is named in
/// <see cref="UnpublishedSettings.UnpublishedProperties"/>.</param>
internal readonly record struct SettingsChange(bool IsMaterial);

/// <summary>
/// Answers "does this change matter?" for a pair of settings states, so a subscriber that redoes a
/// whole outward surface can skip a change that reaches no surface at all. The comparison is the
/// shared change classifier's; the list of what reaches no outward surface is this application's.
/// </summary>
/// <remarks>
/// Works on the serialised form of <see cref="AppSettings"/> alone, not on the settings file: the
/// file's grouped shape is a storage concern, and a classifier tied to it would have to be
/// rewritten when storage moves.
/// </remarks>
internal static class UnpublishedSettings
{
    /// <summary>
    /// The properties whose movement reaches no MQTT entity. The tray icon is not on this list's
    /// account: every committed change is offered to it, and <c>TrayIconLatch</c> drops the ones
    /// that draw the same icon. An exclusion list by design: a property added later lands in the comparison on
    /// its own and is treated as mattering, so the cost of forgetting is a redundant republish
    /// rather than a setting that silently stops being announced. A skip is earned by name here.
    /// </summary>
    internal static readonly IReadOnlyList<string> UnpublishedProperties =
    [
        // Restore bookkeeping for the one-shot override; the override itself is published.
        nameof(AppSettings.TravelOverrideRevertStart),
        nameof(AppSettings.TravelOverrideRevertStop),

        // How one window draws its history graph. Deliberately absent from the MQTT surface.
        nameof(AppSettings.GraphTimeScale),
        nameof(AppSettings.GraphLineColouring),
        nameof(AppSettings.GraphShadingEnabled),
        nameof(AppSettings.GraphDisplay),

        // Whether an off badge collapses to one dense row on the dashboard popup. Decides how one
        // window draws, like the graph settings above — deliberately absent from the MQTT surface.
        nameof(AppSettings.OneLineUntilItMatters),

        // Whether the dashboard popup draws its history graph at all. Decides how one window draws,
        // like the graph settings above — deliberately absent from the MQTT surface.
        nameof(AppSettings.HideGraphInDashboard),

        // How the tray draws the percentage digits. It moves an icon on this machine and nothing
        // outside the process: the tray repaint is driven by the icon request rather than by this
        // classifier, so the style reaches the icon without a republish behind it.
        nameof(AppSettings.PercentageDigitStyle),

        // The lid actions and the battery sleep timeout captured for crash recovery, and the schemes
        // they belong to.
        nameof(AppSettings.LidDelaySavedAcAction),
        nameof(AppSettings.LidDelaySavedDcAction),
        nameof(AppSettings.LidDelaySavedScheme),
        nameof(AppSettings.LidDelaySavedBatterySleepSeconds),
        nameof(AppSettings.LidDelaySavedBatterySleepScheme),

        // The display brightness displaced by a dim. A record of what to put back, not a choice: the
        // level itself is read off the display, so this moving publishes nothing.
        nameof(AppSettings.ScreenSavedBrightness),

        // The firewall state a focus session's network block displaced. A record of what to put
        // back, not a choice, and nothing published reads it. The session's own end time and the
        // three levers it owns are NOT here: they move the state reading, the countdown and all
        // three lever switches.
        nameof(AppSettings.FocusSavedFirewall),

        // Whether the dashboard offers a control that starts a session. Decides how one window
        // draws, like the graph settings above — deliberately absent from the MQTT surface, which
        // is the surface that arms a session in the first place.
        nameof(AppSettings.FocusStartFromDashboard),

        // The once-only network rule migration marker.
        nameof(AppSettings.NetworkRulesKeyedOnPhysicalAdapter),

        // The named scripts. They run on this machine's own state changes and are deliberately
        // absent from the MQTT surface, so editing one reaches nothing outside this process.
        nameof(AppSettings.Scripts),

        // The notification sound and the six switches that have no entity. They decide what this
        // machine shows and plays; the three warnings with entities keep theirs and are not here.
        nameof(AppSettings.NotificationSound),
        nameof(AppSettings.ChargeCompleteNoticeEnabled),
        nameof(AppSettings.ChargingStartedNoticeEnabled),
        nameof(AppSettings.SleptWhileHotWarningEnabled),
        nameof(AppSettings.SettingsNotSavedWarningEnabled),
        nameof(AppSettings.ScriptFailedWarningEnabled),
        nameof(AppSettings.AwakeHoldWarningEnabled),
        nameof(AppSettings.AwakeHoldWarningHours),

        // How often the update check runs, and whether a release installs itself. They govern this
        // machine's own update routine and were never asked to reach Home Assistant.
        nameof(AppSettings.UpdateCheckCadence),
        nameof(AppSettings.InstallUpdatesAutomatically),

        // What the shell held for each tray icon before it was promoted. Restore bookkeeping for
        // PromoteTrayIcons, which is itself published; this is not.
        nameof(AppSettings.TrayPromotionRestore),

        // The early sleep waiting to be said at the next wake. Recorded and then cleared as it is
        // reported, so it moves twice per event and reaches no outward surface either time.
        nameof(AppSettings.LidThermalSleptAtCelsius),
        nameof(AppSettings.LidThermalSleptAtUtc),

        // Where the broker answered last. State rather than a setting, and written on every
        // successful connect — the single largest source of changes that move nothing.
        nameof(AppSettings.MqttLastGoodEndpoint),

        // The Settings window's saved placement.
        nameof(AppSettings.SettingsWindowX),
        nameof(AppSettings.SettingsWindowY),
        nameof(AppSettings.SettingsWindowWidth),
        nameof(AppSettings.SettingsWindowHeight),
    ];

    /// <summary>Every member written, null or not, so a nullable switch moving between unset and a
    /// value is a change the comparison sees.</summary>
    internal static JsonSerializerOptions Serialiser { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The shared classifier over <see cref="UnpublishedProperties"/>. Its fingerprint is
    /// taken before and after a mutation, because <see cref="SettingsService.Update"/> mutates the
    /// live object in place and leaves no earlier instance to compare against.</summary>
    internal static SettingsChangeClassifier<AppSettings> Classifier { get; } =
        new("Does this change reach an MQTT entity?", UnpublishedProperties, Serialiser);
}
