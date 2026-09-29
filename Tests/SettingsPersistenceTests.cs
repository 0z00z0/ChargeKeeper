using System;
using System.IO;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The settings write path, exercised against a real file. <c>SettingsService</c>'s own path is
/// fixed and shared, so these go through <c>WriteTo</c>/<c>ReadFrom</c> rather than swapping it —
/// swapping it would race every other test class reading <c>Current</c>.
/// </summary>
public class SettingsPersistenceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"ck-settings-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>A write that cannot land says so. Without a reported outcome a settings change that
    /// never reached disk is indistinguishable from one that did.</summary>
    [Fact]
    public void AWriteThatCannotLandIsReported()
    {
        // A path whose parent is an existing FILE: the directory can never be created, so the write
        // fails without depending on permissions the test runner may happen to hold.
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocker");
        System.IO.File.WriteAllText(blocker, "");

        Assert.False(SettingsService.WriteTo(new AppSettings(), Path.Combine(blocker, "settings.json")));
    }
}
