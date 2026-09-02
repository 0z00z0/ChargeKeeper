using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The rule that decides whether the retired install folder may be deleted. It is the application's
/// backstop for the upgrade where the installer could not remove the folder, and getting it wrong
/// means either a resurrectable copy left behind or a startup task pointing at a deleted binary.
/// </summary>
public class LegacyInstallSweepTests
{
    private const string Programs = @"C:\Users\Someone\AppData\Local\Programs";
    private const string CurrentExe = Programs + @"\ChargeKeeper\ChargeKeeper.exe";
    private const string LegacyExe  = Programs + @"\Lenovo Power Tray\ChargeKeeper.exe";

    [Fact]
    public void RemovesOnlyWhenTheMoveHasAlreadyHappenedAndNothingStillStartsFromTheOldFolder() =>
        Assert.True(LegacyInstallSweep.MayRemove(CurrentExe, legacyDirExists: true, aTaskTargetsLegacyExe: false));

    [Fact]
    public void KeepsTheFolderWhileAScheduledTaskStillStartsFromIt() =>
        // Deleting it here would leave that task naming a binary that no longer exists, which costs
        // the installation its start at logon.
        Assert.False(LegacyInstallSweep.MayRemove(CurrentExe, legacyDirExists: true, aTaskTargetsLegacyExe: true));

    [Fact]
    public void RemovesNothingWhileStillRunningFromTheOldFolder() =>
        // The migration has not happened; the folder is the live installation.
        Assert.False(LegacyInstallSweep.MayRemove(LegacyExe, legacyDirExists: true, aTaskTargetsLegacyExe: false));

    [Fact]
    public void RemovesNothingFromABuildOutput() =>
        Assert.False(LegacyInstallSweep.MayRemove(
            @"C:\repo\bin\x64\Debug\net10.0-windows\ChargeKeeper.exe",
            legacyDirExists: true, aTaskTargetsLegacyExe: false));

    [Fact]
    public void ThereIsNothingToDoWhenTheOldFolderIsAlreadyGone() =>
        Assert.False(LegacyInstallSweep.MayRemove(CurrentExe, legacyDirExists: false, aTaskTargetsLegacyExe: false));

    [Fact]
    public void AnUnknownExecutablePathRemovesNothing() =>
        Assert.False(LegacyInstallSweep.MayRemove(null, legacyDirExists: true, aTaskTargetsLegacyExe: false));
}
