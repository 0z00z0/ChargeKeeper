namespace ChargeKeeper.Services;

/// <summary>
/// Why an action was taken, as the line recording it says so. One factory per trigger, each taking
/// that trigger's own identity, so a line names the profile, the preset, the entity or the control
/// that decided rather than the category it belongs to.
/// </summary>
/// <remarks>
/// <para>The value cannot be built from an arbitrary phrase through a constructor: a factory is the
/// route, and its signature is what forces the identity in. Two spellings for one trigger — "Home
/// Assistant" beside "MQTT command" — are what a free-text parameter produced.</para>
/// <para>The implicit conversion from a phrase exists for the condition a state machine has already
/// worked out and already words well ("the battery target was met"). Such a phrase is a cause in
/// its own right and needs no factory; a trigger arriving from outside does.</para>
/// </remarks>
internal readonly record struct ActionCause
{
    /// <summary>What a cause reads as where a call site supplied none. Visible rather than empty: a
    /// clause ending in nothing reads as a formatting fault rather than as a missing cause.</summary>
    internal const string Unrecorded = "an unrecorded trigger";

    /// <summary>The separator between what happened and why. Shared by every sink so the two trails
    /// cannot drift apart, and unchanged from what the power lines have always carried.</summary>
    internal const string Separator = " — cause: ";

    private readonly string? _text;

    private ActionCause(string text) => _text = text;

    public override string ToString() => _text is { Length: > 0 } text ? text : Unrecorded;

    /// <summary>The cause as a clause appended to a sentence, so the line stays one sentence.</summary>
    public string Clause => Separator + ToString();

    /// <summary>A condition the caller has already worked out and worded. Not a way round the
    /// factories: a trigger arriving from outside the application has one of its own.</summary>
    public static implicit operator ActionCause(string phrase) => new(phrase);

    // ---- triggers arriving from outside --------------------------------------------------------

    /// <summary>A network profile being joined or left. The profile is named, because "a network
    /// rule matched" is the reading the owner could not act on.</summary>
    public static ActionCause NetworkProfile(string name, bool joined) =>
        new($"the network profile '{name}' being {(joined ? "joined" : "left")}");

    /// <summary>A command from Home Assistant, naming the entity it arrived on.</summary>
    public static ActionCause HomeAssistant(string entityId) =>
        new($"Home Assistant, on the '{entityId}' entity");

    /// <summary>A control on the Settings window, naming its page.</summary>
    public static ActionCause SettingsPage(string page) =>
        new($"the {page} page of the Settings window");

    /// <summary>A control on the dashboard, naming the control.</summary>
    public static ActionCause Dashboard(string control) => new($"the dashboard's {control}");

    /// <summary>An item on one of the tray icon's menus, naming the item.</summary>
    public static ActionCause TrayMenu(string item) => new($"the tray menu's {item}");

    /// <summary>The lid switch moving.</summary>
    public static ActionCause Lid(bool closed) => new($"the lid being {(closed ? "shut" : "opened")}");

    /// <summary>The power source changing.</summary>
    public static ActionCause Charger(bool connected) =>
        new($"the charger being {(connected ? "connected" : "disconnected")}");

    /// <summary>A named preset being the thing asked for.</summary>
    public static ActionCause Preset(string name) => new($"the preset '{name}'");

    // ---- the application's own decisions -------------------------------------------------------

    /// <summary>A timer or a scheduled moment arriving, named for what it was counting.</summary>
    public static ActionCause Timer(string what) => new($"{what}");

    /// <summary>A focus session starting, ending or moving one of its levers.</summary>
    public static ActionCause FocusSession(string what) => new($"the focus session {what}");

    /// <summary>Something a run that ended without tidying up left behind, put back at startup.</summary>
    public static ActionCause StartupRestore(string what) =>
        new($"{what} left by a previous run, put back at startup");

    /// <summary>The application starting.</summary>
    public static ActionCause Startup() => new("the application starting");

    /// <summary>The application closing.</summary>
    public static ActionCause Shutdown() => new("the application closing");

    /// <summary>One of the application's own repeating checks, named for what it checks.</summary>
    public static ActionCause PeriodicCheck(string what) => new($"the periodic {what}");

    /// <summary>The settling window closing on a state that outlasted it, which is what decided —
    /// never the event that opened the window.</summary>
    public static ActionCause SettledState(string state) =>
        new($"the settling window closing with the machine {state}");
}
