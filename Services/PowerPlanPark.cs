using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>The Windows power plans, and which one is active.</summary>
internal interface IPowerPlanSetting
{
    /// <summary>The active plan, or null when it cannot be read.</summary>
    Guid? ReadActive();

    /// <summary>Makes a plan active. False when Windows refused it.</summary>
    bool SetActive(Guid plan);

    /// <summary>The name Windows shows for a plan, or null when the plan is not on this machine.</summary>
    string? NameOf(Guid plan);
}

/// <summary>Where the plan displaced by a profile is kept so a crash cannot lose it.</summary>
internal interface IPowerPlanRecord
{
    Guid? Read();

    /// <summary>False when the record did not reach disk.</summary>
    bool Save(Guid plan);

    void Clear();
}

/// <summary>
/// Switches the Windows power plan for as long as a network profile asks for one, and puts back the
/// plan that was running before the first such profile took over.
/// </summary>
/// <remarks>
/// The displaced plan reaches the record before the plan changes, and is never re-captured while a
/// profile holds it, so moving between two profiles that each name a plan still restores the plan
/// the machine was on before either. A record left behind by a run that ended is restored the next
/// time no profile asks for a plan, which includes the first reconcile after a start.
/// </remarks>
internal sealed class PowerPlanPark(
    IPowerPlanSetting setting,
    IPowerPlanRecord record,
    Action<string, string> log)
{
    private readonly Lock _gate = new();

    /// <summary>Applies <paramref name="wanted"/>, or puts the displaced plan back when no profile
    /// asks for one. The single entry point, so nothing can park without a matching restore.</summary>
    public void Reconcile(Guid? wanted, string cause)
    {
        lock (_gate)
        {
            if (wanted is { } plan) Park(plan, cause);
            else Restore(cause);
        }
    }

    private void Park(Guid wanted, string cause)
    {
        if (setting.NameOf(wanted) is not { } wantedName)
        {
            log("Windows power plan left as it is: the profile names a plan this machine no longer has", cause);
            return;
        }

        var active = setting.ReadActive();
        if (active is { } running && running == wanted) return;

        if (record.Read() is null)
        {
            if (active is not { } displaced)
            {
                log("Windows power plan left as it is: the active plan could not be read", cause);
                return;
            }

            if (!record.Save(displaced))
            {
                log($"Windows power plan left on {Describe(displaced)}: the plan in use could not be saved first, " +
                    "so it could not be put back after a crash", cause);
                return;
            }
        }

        if (setting.SetActive(wanted)) log($"Windows power plan switched to {wantedName}", cause);
        else log($"Windows power plan could not be switched to {wantedName}", cause);
    }

    private void Restore(string cause)
    {
        if (record.Read() is not { } saved) return;

        if (setting.NameOf(saved) is null)
        {
            record.Clear();
            log("Windows power plan left as it is: the plan in use before a profile took over no longer exists", cause);
            return;
        }

        if (!setting.SetActive(saved))
        {
            log($"Windows power plan could not be put back to {Describe(saved)} — retrying at next start", cause);
            return;
        }

        record.Clear();
        log($"Windows power plan back to {Describe(saved)}", cause);
    }

    private string Describe(Guid plan) => setting.NameOf(plan) ?? plan.ToString();
}

/// <summary>The live power plans.</summary>
internal sealed class WindowsPowerPlanSetting : IPowerPlanSetting
{
    public Guid? ReadActive() => NativeMethods.ActivePowerPlan();

    public bool SetActive(Guid plan) => NativeMethods.SetActivePowerPlan(plan);

    public string? NameOf(Guid plan) => NativeMethods.PowerPlanName(plan);
}

/// <summary>The record in settings.json, in the Network section beside the profiles themselves.</summary>
internal sealed class SettingsPowerPlanRecord : IPowerPlanRecord
{
    public Guid? Read() =>
        Guid.TryParse(SettingsService.Read(s => s.NetworkSavedPowerPlan), out var plan) ? plan : null;

    public bool Save(Guid plan) => SettingsService.Update(s => s.NetworkSavedPowerPlan = plan.ToString());

    public void Clear() => SettingsService.Update(s => s.NetworkSavedPowerPlan = null);
}
