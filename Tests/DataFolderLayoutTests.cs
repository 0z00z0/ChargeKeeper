using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The move into Logs and History runs against a person's own logs and battery history on the first
/// start of a new version, so a file it lost would be lost for good.
/// </summary>
public sealed class DataFolderLayoutTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ChargeKeeper.Tests", $"layout-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Dictionary<string, string> Snapshot() =>
        Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)
                 .ToDictionary(p => Path.GetRelativePath(_dir, p), File.ReadAllText,
                               StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TheMoveLosesNoFile_AndLeavesWhatItCannotMoveWhereItWas()
    {
        Write("app.log", "app, current");
        Write("app_2026-09-10_00.log", "app, archive");
        Write("power.log", "power, written by an older version");
        Write("power_2026-09-12_00.log", "power, archive");
        Write("update-install.log", "setup");
        Write(Path.Combine("dumps", "ChargeKeeper.exe.1234.dmp"), "dump");
        Write("battery-level-history.csv", "level");
        Write("battery-capacity-history.csv", "capacity");
        Write("performance-history.csv", "performance");
        Write("settings.json", "settings");
        Write("mqtt.json", "mqtt");
        Write("update-refused.txt", "refused");
        // Already written by the new version: the destination is taken.
        Write(Path.Combine("Logs", "power.log"), "power, written by the new version");

        var before = Snapshot();
        IReadOnlyList<DataFolderLayout.Outcome> outcomes;
        // Held without sharing, as another process could hold it.
        using (new FileStream(Path.Combine(_dir, "performance-history.csv"),
                              FileMode.Open, FileAccess.Read, FileShare.None))
            outcomes = DataFolderLayout.MoveIntoSubfolders(_dir);
        var after = Snapshot();

        Assert.Equal(before.Values.Order(StringComparer.Ordinal), after.Values.Order(StringComparer.Ordinal));

        Assert.Equal("app, current",   after[Path.Combine("Logs", "app.log")]);
        Assert.Equal("app, archive",   after[Path.Combine("Logs", "app_2026-09-10_00.log")]);
        Assert.Equal("power, archive", after[Path.Combine("Logs", "power_2026-09-12_00.log")]);
        Assert.Equal("setup",          after[Path.Combine("Logs", "update-install.log")]);
        Assert.Equal("dump",           after[Path.Combine("Logs", "dumps", "ChargeKeeper.exe.1234.dmp")]);
        Assert.Equal("level",          after[Path.Combine("History", "battery-level-history.csv")]);
        Assert.Equal("capacity",       after[Path.Combine("History", "battery-capacity-history.csv")]);
        Assert.Equal("settings",       after["settings.json"]);
        Assert.Equal("mqtt",           after["mqtt.json"]);
        Assert.Equal("refused",        after["update-refused.txt"]);

        Assert.Equal("power, written by an older version", after["power.log"]);
        Assert.Equal("power, written by the new version",  after[Path.Combine("Logs", "power.log")]);
        Assert.Equal("performance",                        after["performance-history.csv"]);
        Assert.Equal(2, outcomes.Count(o => !o.Moved));
    }
}
