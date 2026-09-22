using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// One guard on the convention: an action that changes the machine or the stored settings names
/// what caused it. The defect it exists to catch is a line reading "preset 'Travel' applied" with
/// nothing saying that a network rule matched — accurate, and no use to the person reading it.
/// </summary>
public class ActionCauseTests
{
    /// <summary>
    /// The actions that change the machine's state or the stored settings, by the file that declares
    /// each and the method's own name. A hand-kept list, because "changes the machine" is not
    /// something a regular expression can recognise — a reader needs no cause, and telling the two
    /// apart is a judgement.
    /// </summary>
    private static readonly (string File, string Method)[] _actions =
    [
        ("Services/ChargeControlService.cs",   "SetSmartChargeEnabled"),
        ("Services/ChargeControlService.cs",   "SetExplicitThresholds"),
        ("Services/ChargeControlService.cs",   "ApplyPresetByName"),
        ("Services/ChargeThresholdService.cs", "SetThresholds"),
        ("Services/TravelOverrideService.cs",  "Activate"),
        ("Services/TravelOverrideService.cs",  "Cancel"),
        ("Services/TravelOverrideService.cs",  "ApplyExplicitThresholds"),
        ("Services/KeepAwakeService.cs",       "Activate"),
        ("Services/KeepAwakeService.cs",       "Deactivate"),
        ("Services/LidDelayService.cs",        "SetEnabled"),
        ("Services/LidDelayService.cs",        "SetTimeEnabled"),
        ("Services/LidDelayService.cs",        "SetDischargeEnabled"),
        ("Services/LidDelayService.cs",        "SetDelayMinutes"),
        ("Services/BatterySleepPark.cs",       "Park"),
        ("Services/BatterySleepPark.cs",       "Restore"),
        ("Services/ScreenBrightnessService.cs", "Set"),
        ("Services/ScreenBrightnessService.cs", "Restore"),
        ("Services/FocusSessionService.cs",    "Arm"),
        ("Services/FocusSessionService.cs",    "RequestCancel"),
        ("Services/ScreenCoverService.cs",     "Show"),
        ("Services/ScreenCoverService.cs",     "Hide"),
        ("Services/InputBlock.cs",             "Take"),
        ("Services/InputBlock.cs",             "Release"),
        ("Services/ScriptRunner.cs",           "Start"),
        ("Services/NetworkProfiles.cs",        "SetEnabled"),
    ];

    /// <summary>
    /// Every one of them declares the cause. A parameter rather than something ambient, so a call
    /// site that has not decided cannot compile, and so the omission is visible by reading the code
    /// instead of by reading a log that turned out to be missing the answer.
    /// </summary>
    [Fact]
    public void EveryActionThatChangesTheMachine_TakesItsCause()
    {
        List<string> missing = [];

        foreach (var (file, method) in _actions)
        {
            string source = File.ReadAllText(RepoFiles.Find(file));

            // Declarations, not calls. Three things make the difference: the four-space indent, which
            // is the file's own type rather than a nested helper; the access modifier on the same
            // line as the name; and no opening bracket between the two, without which a call in an
            // expression body — `StartInWindowsPowerShell(…) => WindowsPowerShell.Start(…)` — reads
            // as a second declaration of Start. The parameter list alone may wrap.
            var declarations = Regex.Matches(
                source,
                @"(?m)^    (?:public|internal|private|protected)[^;(\r\n]*?\b"
                    + Regex.Escape(method) + @"\s*\(([^)]*)\)");

            if (declarations.Count == 0)
            {
                missing.Add($"{file}:{method} — no declaration found");
                continue;
            }

            // Every overload, not merely the first: one that still takes no cause is a route round
            // the convention, and it is the one a call site under pressure reaches for.
            foreach (Match declaration in declarations)
                if (!declaration.Groups[1].Value.Contains("ActionCause", StringComparison.Ordinal))
                    missing.Add($"{file}:{method}");
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// A cause names the specific thing, not its category. "A network rule matched" is the reading
    /// that sent somebody to the code; the rule's own name is the reading that does not.
    /// </summary>
    [Fact]
    public void ACauseNamesTheThing_NotItsCategory()
    {
        Assert.Contains("Skibotn", ActionCause.NetworkProfile("Skibotn", joined: true).ToString(),
                        StringComparison.Ordinal);
        Assert.Contains("charge_start", ActionCause.HomeAssistant("charge_start").ToString(),
                        StringComparison.Ordinal);
        Assert.Contains("Travel", ActionCause.Preset("Travel").ToString(), StringComparison.Ordinal);
        Assert.Contains("Smart Charge", ActionCause.SettingsPage("Smart Charge").ToString(),
                        StringComparison.Ordinal);

        // Joining and leaving are one factory and must not read the same.
        Assert.NotEqual(ActionCause.NetworkProfile("Skibotn", joined: true),
                        ActionCause.NetworkProfile("Skibotn", joined: false));
    }

    /// <summary>The clause is one sentence's worth, appended rather than carried on a second line:
    /// the log rotates by size, and a cause on its own line cannot be attributed once a sibling
    /// process has written between the two.</summary>
    [Fact]
    public void TheClauseStaysOnTheOneLine()
    {
        string clause = ActionCause.Charger(connected: false).Clause;

        Assert.StartsWith(ActionCause.Separator, clause, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', clause);
        Assert.DoesNotContain('\r', clause);
    }

    /// <summary>A cause nothing filled in reads as something rather than as nothing. A sentence
    /// ending in an empty clause reads as a formatting fault, which is the wrong thing to go
    /// looking for.</summary>
    [Fact]
    public void ACauseNobodySupplied_IsVisibleRatherThanEmpty() =>
        Assert.Equal(ActionCause.Unrecorded, default(ActionCause).ToString());
}
