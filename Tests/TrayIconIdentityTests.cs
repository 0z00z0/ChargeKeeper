using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Guards the value Windows files the tray icon's settings under. The shell keys an icon's record
/// — above all whether it sits in the visible area or behind the overflow chevron — on this GUID,
/// so a changed value costs every installation the position its owner chose, with no recovery and
/// no error. The value is therefore pinned as a literal here: a regenerated identity fails the
/// suite rather than shipping.
/// </summary>
public class TrayIconIdentityTests
{
    /// <summary>The one value in the product that must never be regenerated. Stated as its own
    /// literal, not read back from the constant under test, so an edit there has something to
    /// disagree with.</summary>
    private const string PinnedIdentity = "05290CC3-5F1D-4AD4-8F5D-722D2D0772A1";

    private static string AppSourceWithoutComments() =>
        System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(RepoFiles.Find("App.xaml.cs")), @"//[^\r\n]*", string.Empty);

    [Fact]
    public void TheIdentity_IsTheValueEveryInstallationAlreadyHas()
    {
        Assert.Equal(new Guid(PinnedIdentity), TrayIconIdentity.Value);
    }

    /// <summary>Same rule as the creation call itself: the icon is a way to reach the application,
    /// not what the application is for, so nothing about it may unwind start-up.</summary>
    [Fact]
    public void ApplyingTheIdentity_CannotAbandonStartup()
    {
        string body = SourceMethods.Body(AppSourceWithoutComments(), "InitTrayIcon");

        int identity = body.IndexOf("TrayIconIdentity.Value", StringComparison.Ordinal);
        Assert.True(identity >= 0, "InitTrayIcon no longer applies the stable identity.");

        // The guard has to open before the assignment and report after it.
        Assert.Contains("try", body[..identity], StringComparison.Ordinal);
        Assert.Contains("InitTrayIcon.Identity", body[identity..], StringComparison.Ordinal);
    }

    /// <summary>H.NotifyIcon's CustomName setter writes its own path-derived GUID back over Id, so
    /// setting a name would discard the stable identity without any sign that it had.</summary>
    [Fact]
    public void TheIdentity_IsNotDiscardedByNamingTheIcon()
    {
        Assert.DoesNotContain("CustomName", AppSourceWithoutComments(), StringComparison.Ordinal);
    }
}
