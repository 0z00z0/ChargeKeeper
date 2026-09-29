using Xunit;

namespace ChargeKeeper.Tests;

// Where the sleep-delay reading points: the Windows sleep setting, read and never written.
public class SleepDelayPolicyTests
{
    private static string NativeMethodsSource() =>
        File.ReadAllText(RepoFiles.Find(Path.Combine("Helpers", "NativeMethods.cs")));

    /// <summary>
    /// The declaration of <c>ReadSleepDelay</c>, which is expression-bodied like the lid-action read
    /// beside it and so cannot be pulled out by <see cref="SourceMethods"/>. It ends at the
    /// wrapper's fallback argument, which is the last thing on its final line.
    /// </summary>
    private static string ReadSleepDelayDeclaration()
    {
        string source = NativeMethodsSource();
        int start = source.IndexOf("ReadSleepDelay()", StringComparison.Ordinal);
        Assert.True(start >= 0, "NativeMethods no longer declares ReadSleepDelay.");

        int end = source.IndexOf("}, null);", start, StringComparison.Ordinal);
        Assert.True(end > start, "ReadSleepDelay no longer ends at the active-scheme wrapper.");
        return source[start..end];
    }

    [Fact]
    public void ReadSleepDelay_ReadsTheSleepSettingAndWritesNothing()
    {
        // The reading is a P/Invoke pair no test can stand up, so the guard is on where it points.
        // The write calls sit in the same file against the same wrapper, and a sleep delay written
        // by accident would change when the machine sleeps — the one thing #174 must not do.
        string declaration = ReadSleepDelayDeclaration();

        Assert.Contains("GUID_SUB_SLEEP", declaration, StringComparison.Ordinal);
        Assert.Contains("GUID_STANDBYIDLE", declaration, StringComparison.Ordinal);
        Assert.Contains("PowerReadACValueIndex", declaration, StringComparison.Ordinal);
        Assert.Contains("PowerReadDCValueIndex", declaration, StringComparison.Ordinal);
        Assert.DoesNotContain("PowerWrite", declaration, StringComparison.Ordinal);
    }
}
