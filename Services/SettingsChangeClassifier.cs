using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ChargeKeeper.Services;

/// <summary>A committed settings change, carrying whether it moved anything a subscriber mirrors
/// outside this process.</summary>
/// <param name="IsMaterial">False only when every property that moved is named in
/// <see cref="SettingsChangeClassifier.UnpublishedProperties"/>.</param>
internal readonly record struct SettingsChange(bool IsMaterial);

/// <summary>
/// Answers "does this change matter?" for a pair of settings states, so a subscriber that redoes a
/// whole outward surface can skip a change that reaches no surface at all.
/// </summary>
/// <remarks>
/// Works on the serialised form of <see cref="AppSettings"/> alone, not on the settings file: the
/// file's grouped shape is a storage concern, and a classifier tied to it would have to be
/// rewritten when storage moves.
/// </remarks>
internal static class SettingsChangeClassifier
{
    /// <summary>
    /// The properties whose movement reaches no outward surface — neither an MQTT entity nor the
    /// tray icon. An exclusion list by design: a property added later lands in the comparison on
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

        // The lid actions captured for crash recovery, and the scheme they belong to.
        nameof(AppSettings.LidDelaySavedAcAction),
        nameof(AppSettings.LidDelaySavedDcAction),
        nameof(AppSettings.LidDelaySavedScheme),

        // The once-only network rule migration marker.
        nameof(AppSettings.NetworkRulesKeyedOnPhysicalAdapter),

        // What the shell held for each tray icon before it was promoted. Restore bookkeeping for
        // PromoteTrayIcons, which is itself published; this is not.
        nameof(AppSettings.TrayPromotionRestore),

        // Where the broker answered last. State rather than a setting, and written on every
        // successful connect — the single largest source of changes that move nothing.
        nameof(AppSettings.MqttLastGoodEndpoint),

        // The Settings window's saved placement.
        nameof(AppSettings.SettingsWindowX),
        nameof(AppSettings.SettingsWindowY),
        nameof(AppSettings.SettingsWindowWidth),
        nameof(AppSettings.SettingsWindowHeight),
    ];

    private static readonly JsonSerializerOptions _opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The settings state as one comparable string. Taken before and after a mutation,
    /// because <see cref="SettingsService.Update"/> mutates the live object in place and leaves no
    /// earlier instance to compare against.</summary>
    internal static string Snapshot(AppSettings settings) => JsonSerializer.Serialize(settings, _opts);

    /// <summary>Whether anything outside <see cref="UnpublishedProperties"/> differs between two
    /// snapshots. Unparseable input reads as mattering: a subscriber doing redundant work is a
    /// smaller fault than one that stops announcing a setting.</summary>
    internal static bool IsMaterial(string before, string after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal)) return false;

        try
        {
            return WithoutUnpublished(before) != WithoutUnpublished(after);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    public static bool IsMaterial(AppSettings before, AppSettings after) =>
        IsMaterial(Snapshot(before), Snapshot(after));

    /// <summary>One snapshot with the excluded properties removed. <see cref="AppSettings"/> is a
    /// flat object, so a top-level removal reaches every excluded name.</summary>
    private static string WithoutUnpublished(string snapshot)
    {
        if (JsonNode.Parse(snapshot) is not JsonObject root)
            throw new JsonException("settings snapshot is not a JSON object");

        foreach (var name in UnpublishedProperties) root.Remove(name);
        return root.ToJsonString();
    }
}
