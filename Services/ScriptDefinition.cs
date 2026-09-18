using System.Text.Json.Serialization;

namespace ChargeKeeper.Services;

/// <summary>Which of the application's own state changes runs a script. One event and one
/// direction: two named scripts cover the two directions rather than one script being told what
/// happened, so nothing has to be passed into a script.</summary>
/// <remarks>APPEND new members, never insert: the value is persisted by name, but the Settings
/// page's dropdown maps position to member, so the two orders stay in lockstep.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ScriptTrigger
{
    /// <summary>A charger was connected.</summary>
    MainsConnected,

    /// <summary>A charger was disconnected.</summary>
    MainsDisconnected,

    /// <summary>The lid was shut.</summary>
    LidClosed,

    /// <summary>The lid was opened.</summary>
    LidOpened,

    /// <summary>The machine arrived on the network one named profile matches.</summary>
    NetworkJoined,

    /// <summary>The machine left the network one named profile matches.</summary>
    NetworkLeft,
}

/// <summary>The label each <see cref="ScriptTrigger"/> carries on screen and in the log, in enum
/// order — one table rather than the strings restated in the page, the dropdown and the log.</summary>
internal static class ScriptTriggerLabels
{
    private static readonly string[] _labels =
    [
        "Charger connected",
        "Charger disconnected",
        "Lid closed",
        "Lid opened",
        "Network joined",
        "Network left",
    ];

    public static string For(ScriptTrigger trigger) => _labels[(int)trigger];

    /// <summary>Every label, in enum order, for the Settings page's dropdown.</summary>
    public static IReadOnlyList<string> All => _labels;
}

/// <summary>One named PowerShell script and the state change that runs it.</summary>
/// <remarks>
/// <see cref="Id"/> rather than the name identifies a script to the runner: the name is editable and
/// nothing stops two scripts sharing one, and both the one-run-at-a-time gate and the failure latch
/// would then be shared by two unrelated scripts.
/// </remarks>
internal sealed class ScriptDefinition
{
    /// <summary>Stable for the life of the script, stamped when it is created. Blank on a document
    /// written before the key existed, which the page stamps on the next edit.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public ScriptTrigger Trigger { get; set; }

    /// <summary>The script itself, as PowerShell source.</summary>
    public string Body { get; set; } = "";

    /// <summary>The network profile joining or leaving runs this script, by
    /// <see cref="NetworkLocationRule.Id"/> rather than by name or position: a profile is renamed and
    /// reordered freely, and a script bound to one must not silently follow another. Null on every
    /// trigger that names no profile.</summary>
    public string? NetworkProfileId { get; set; }

    // Parameterless ctor required for JSON deserialisation.
    public ScriptDefinition() { }

    public ScriptDefinition(string id, string name, ScriptTrigger trigger, string body,
                            string? networkProfileId = null)
    {
        Id = id; Name = name; Trigger = trigger; Body = body; NetworkProfileId = networkProfileId;
    }

    /// <inheritdoc cref="ChargeKeeper.Helpers.StableId.New"/>
    public static string NewId() => Helpers.StableId.New();

    /// <summary>The name a script is referred to by. A script with no name is still a script, so it
    /// falls back to its trigger rather than to an empty string.</summary>
    [JsonIgnore]
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? ScriptTriggerLabels.For(Trigger) : Name.Trim();

    /// <summary>Whether the script would do anything at all. A body of nothing but whitespace runs
    /// PowerShell for no reason and reports success, which reads as a script that worked.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Body);
}
