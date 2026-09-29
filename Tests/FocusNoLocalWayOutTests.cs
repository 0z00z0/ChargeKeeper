using System.Text.RegularExpressions;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// What stops a focus session leaving a machine nobody can use, held against the source: an input
/// block that outlives the application, and a cover nothing can take down.
/// </summary>
/// <remarks>Source text rather than behaviour, like the dashboard's other structural guards: the
/// windows are WinUI code-behind that cannot be driven without a display, and what must never
/// appear there is a call, not a value.</remarks>
public class FocusNoLocalWayOutTests
{
    private static string Source(string relativePath) =>
        File.ReadAllText(RepoFiles.Find(relativePath));

    [Fact]
    public void TheInputBlockLapsesRatherThanPersisting() =>
        // The whole safety of the input lever: the thread holding the block gives it up unless
        // something keeps pushing a deadline forward, so the application hanging cannot leave a
        // machine nobody can type on. A block that waited to be told to stop could.
        Assert.Matches(new Regex(@"DateTimeOffset\.UtcNow - _renewedAt > RenewalWindow"),
                       Source(Path.Combine("Services", "InputBlock.cs")));

    [Fact]
    public void TheInputBlockIsReleasedWhenTheApplicationCloses() =>
        // Not left to what ending the process does to a block, which cannot be measured from
        // inside it. The release names the shutdown as its cause, so the line recording it says
        // why the machine started answering again.
        Assert.Contains("InputBlock.Release(ActionCause.ApplicationClosing())",
                        Source(Path.Combine("Services", "FocusSessionService.cs")),
                        StringComparison.Ordinal);

    [Fact]
    public void TheOneCloseThatWorksLiftsTheRefusalFirst() =>
        // The session's own teardown and a rebuild both go through Dismiss, which allows the close
        // before asking for it. A teardown that did not would leave a cover nothing can take down.
        Assert.Matches(new Regex(@"_refusal\?\.Allow\(\);\s*Close\(\);"),
                       Source(Path.Combine("UI", "ScreenCoverWindow.xaml.cs")));
}
