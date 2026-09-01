using System.Runtime.CompilerServices;
using ChargeKeeper.Services;
using NLog;
using NLog.Targets;

namespace ChargeKeeper.Tests;

/// <summary>
/// Points this process's logging at a disposable directory before any test runs.
/// </summary>
/// <remarks>
/// <para><c>nlog.config</c> is copied beside the test assembly (the app needs it beside the exe, and
/// a ProjectReference carries it along), so NLog auto-discovers it and its
/// <c>${specialfolder:folder=ApplicationData}</c> targets resolve to the real
/// <c>%AppData%\ChargeKeeper\</c>. A test run then appends to the log an installed ChargeKeeper is
/// writing, and genuine events and fixtures interleave there indistinguishably without reading
/// stack frames.</para>
/// <para>Assigning <see cref="LogManager.Configuration"/> here beats that auto-discovery, and it
/// runs before <see cref="AppLog"/>'s static initialiser — which reads the same property — so both
/// the shipped-config route and AppLog's own fallback land in the temp directory. The redirect is
/// asserted by <c>TestLogRedirectTests</c>; without that assertion this file could stop working and
/// nothing would say so.</para>
/// </remarks>
internal static class TestLogRedirect
{
    /// <summary>Where this test run's log files go. Under the temp directory, one per process.</summary>
    internal static string Directory { get; } = Path.Combine(
        Path.GetTempPath(), "ChargeKeeper.Tests", $"run-{Environment.ProcessId}");

    /// <summary>The per-user directory a shipped ChargeKeeper writes to. Nothing here may touch it.</summary>
    internal static string RealDataDirectory => AppPaths.DataDir;

    [ModuleInitializer]
    internal static void RedirectAwayFromTheUserLog()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            // Built from the shipped policy rather than hand-rolled, so a target added to
            // nlog.config is redirected too instead of quietly keeping the real path.
            // Assigned BEFORE the rewrite: both file targets sit inside RetryingTargetWrapper, and
            // AllTargets only reaches through a wrapper once the configuration is installed.
            LogManager.Configuration = AppLog.BuildFallbackConfiguration();

            foreach (var target in LogManager.Configuration.AllTargets.OfType<FileTarget>())
            {
                string name = Path.GetFileName(
                    target.FileName.Render(LogEventInfo.CreateNullEvent()));
                target.FileName = Path.Combine(Directory, name);
            }

            LogManager.ReconfigExistingLoggers();
        }
        catch
        {
            // A module initialiser that throws takes the whole run down with a
            // TypeInitializationException. TestLogRedirectTests fails loudly instead.
        }
    }

    /// <summary>Whether <paramref name="path"/> lands inside the real per-user data directory.</summary>
    internal static bool IsUnderRealDataDirectory(string path)
    {
        string real = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RealDataDirectory));
        string full = Path.GetFullPath(path);
        return full.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, real, StringComparison.OrdinalIgnoreCase);
    }
}
