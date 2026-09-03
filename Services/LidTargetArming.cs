namespace ChargeKeeper.Services;

/// <summary>What became of the battery target when a lid close armed its wait.</summary>
internal enum LidTargetArm
{
    /// <summary>The target is switched off, so no target was in play.</summary>
    SwitchedOff,

    /// <summary>Switched on, but no battery reading has reached the service, so there was nothing to
    /// judge the target against. The one outcome that means something is wrong rather than
    /// ordinary.</summary>
    NoReading,

    /// <summary>Armed and outstanding: the machine stays awake until the battery comes down to it.</summary>
    Armed,

    /// <summary>The battery was already at or below the target as the lid closed.</summary>
    AlreadyThere,

    /// <summary>The pack was taking charge, so the target could never arrive.</summary>
    Charging,
}

/// <summary>
/// What a lid close decided about its battery target, and the sentence that says so. Rules and
/// wording only — no timer, no settings and no OS — so what a reader of the power trail ends up
/// seeing is testable without closing a lid.
/// </summary>
/// <remarks>
/// A target that is configured but never armed used to produce no entry at all, which is
/// indistinguishable in the trail from one that armed and is quietly holding. The two have opposite
/// answers to "did the battery target do anything", so every outcome is recorded, including the
/// ones where nothing was armed.
/// </remarks>
internal static class LidTargetArming
{
    /// <summary>What the lid close decided, from the state it decided on.</summary>
    /// <param name="enabled">Whether the battery target is switched on.</param>
    /// <param name="hasReading">Whether a battery reading has reached the service.</param>
    /// <param name="decision">What the watch made of that reading; null where none was consulted.</param>
    public static LidTargetArm Decide(bool enabled, bool hasReading, LidDischargeDecision? decision)
    {
        if (!enabled)   return LidTargetArm.SwitchedOff;
        if (!hasReading) return LidTargetArm.NoReading;

        return decision switch
        {
            LidDischargeDecision.Hold          => LidTargetArm.Armed,
            LidDischargeDecision.TargetReached => LidTargetArm.AlreadyThere,
            LidDischargeDecision.Charging      => LidTargetArm.Charging,
            _                                  => LidTargetArm.NoReading,
        };
    }

    /// <summary>The trail entry for <paramref name="arm"/>, as a headline and the reason beside it.
    /// <paramref name="target"/> is the clamped level; <paramref name="level"/> the reading the
    /// decision was taken on, where there was one.</summary>
    public static (string What, string Why) Describe(LidTargetArm arm, int target, int? level) => arm switch
    {
        LidTargetArm.Armed =>
            ($"Sleep also comes as soon as the battery reaches {target} %",
             $"lid closed with a battery target set, and the battery at {level} %"),

        LidTargetArm.AlreadyThere =>
            ("The battery was already at its lid-close target",
             $"the target is {target} % and the battery was at {level} %"),

        LidTargetArm.Charging =>
            ("No battery target on this lid close",
             "the battery is charging, so the target can never arrive"),

        LidTargetArm.SwitchedOff =>
            ("No battery target on this lid close", "the setting is off"),

        // Worth its own wording: the target is configured and nothing is holding it, which is a
        // fault in the feed rather than a state the user asked for.
        _ =>
            ("No battery target on this lid close",
             "no battery reading has reached the lid-close service, so the target could not be judged"),
    };
}
