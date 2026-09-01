using Windows.System.Power;

namespace ChargeKeeper.Helpers;

/// <summary>Where the machine's power is coming from, as every surface that colours by it needs it:
/// on battery, taking charge, or connected but not charging.</summary>
internal enum PowerState
{
    /// <summary>On battery.</summary>
    Discharging,

    /// <summary>Connected and taking charge.</summary>
    Charging,

    /// <summary>Connected and not taking charge — full, or held at a charge limit.</summary>
    IdleOnMains,
}

/// <summary>Derives <see cref="PowerState"/> from what the app already reads, so no surface invents
/// its own rule. Windows reports the third state itself: <see cref="BatteryStatus.Idle"/> is
/// external power with no charge flowing.</summary>
internal static class PowerStates
{
    /// <summary>The state a Windows battery status stands for. Anything that is not charging and not
    /// idle is on battery, the pre-first-report <see cref="BatteryStatus.NotPresent"/> seed included:
    /// no reading is not a reason to claim mains power.</summary>
    internal static PowerState From(BatteryStatus status) => status switch
    {
        BatteryStatus.Charging => PowerState.Charging,
        BatteryStatus.Idle     => PowerState.IdleOnMains,
        _                      => PowerState.Discharging,
    };

    /// <summary>The same state from the two published flags. Charging wins over the mains flag; the
    /// two are read from one status, so they cannot disagree.</summary>
    internal static PowerState From(bool isCharging, bool onAc) =>
        isCharging ? PowerState.Charging :
        onAc       ? PowerState.IdleOnMains :
                     PowerState.Discharging;

    /// <summary>The state as it is published and shown, in the app's own vocabulary.</summary>
    internal static string Label(PowerState state) => state switch
    {
        PowerState.Charging    => "Charging",
        PowerState.IdleOnMains => "Idle on mains",
        _                      => "Discharging",
    };
}
