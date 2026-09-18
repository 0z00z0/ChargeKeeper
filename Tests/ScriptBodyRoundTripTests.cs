using System;
using System.IO;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// A script of several lines must come back as several lines. The editing box separates lines with
/// a lone carriage return, so that is what the document holds and what has to survive both the file
/// and the assignment back into the box.
/// </summary>
public class ScriptBodyRoundTripTests : IDisposable
{
    private const string Body = "Write-Output 'one'\rWrite-Output 'two'\rWrite-Output 'three'";

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"ck-script-body-{Guid.NewGuid():N}");

    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>The stored body, character for character. A body truncated at the first break is a
    /// script whose remaining lines are gone with no way back.</summary>
    [Fact]
    public void AScriptOfSeveralLinesSurvivesTheDocument()
    {
        var written = new AppSettings();
        written.Scripts.Add(new ScriptDefinition(
            ScriptDefinition.NewId(), "Several lines", ScriptTrigger.MainsConnected, Body));

        Assert.True(SettingsService.WriteTo(written, File_));
        var loaded = SettingsService.ReadFrom(File_);

        Assert.NotNull(loaded);
        var script = Assert.Single(loaded!.Scripts);
        Assert.Equal(Body, script.Body);
        Assert.Equal(2, script.Body.Split('\r').Length - 1);
    }

    /// <summary>A TextBox that is still single-line when Text is assigned keeps only the first line,
    /// and turning it multi-line afterwards does not bring the rest back
    /// (microsoft-ui-xaml#10956). The order is therefore load-bearing and cannot be read off the
    /// control at run time from a test, so it is pinned in the source that builds the box.</summary>
    [Fact]
    public void TheScriptBoxAcceptsReturnsBeforeItIsGivenItsText()
    {
        string source = File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "SettingsWindow.xaml.cs")));
        string body   = SourceMethods.Body(source, "BuildScriptRow");

        // The assignments themselves, not the comment above them, which names both.
        int acceptsReturn = body.IndexOf("AcceptsReturn       = true", StringComparison.Ordinal);
        int text          = body.IndexOf("Text                = script.Body", StringComparison.Ordinal);

        Assert.True(acceptsReturn >= 0, "BuildScriptRow no longer sets AcceptsReturn on the box.");
        Assert.True(text >= 0, "BuildScriptRow no longer assigns the script body to the box.");
        Assert.True(acceptsReturn < text,
                    "AcceptsReturn must be set before Text, or the box keeps only the first line.");
    }

    /// <summary>Run now must commit the row's current box contents unconditionally before running,
    /// not through Flush()'s typed-flag guard: a focus change to the button can race the TextChanged
    /// notification that sets the flag, which would let a just-edited box run its stale, stored
    /// body.</summary>
    [Fact]
    public void RunNowCommitsTheBoxUnconditionallyBeforeRunning()
    {
        string source = File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "SettingsWindow.xaml.cs")));
        string body   = SourceMethods.Body(source, "BuildScriptRow");

        int runNowClick = body.IndexOf("runNow.Click", StringComparison.Ordinal);
        Assert.True(runNowClick >= 0, "BuildScriptRow no longer wires the Run now click handler.");

        int commit     = body.IndexOf("Commit();", runNowClick, StringComparison.Ordinal);
        int runScript  = body.IndexOf("RunScriptNow(index, error);", runNowClick, StringComparison.Ordinal);
        int guardedCall = body.IndexOf("Flush(); RunScriptNow", runNowClick, StringComparison.Ordinal);

        Assert.True(commit >= 0 && commit < runScript,
                    "Run now must call Commit() directly before RunScriptNow.");
        Assert.True(guardedCall < 0,
                    "Run now must not run through Flush(), which skips the commit when TextChanged has not yet fired.");
    }
}
