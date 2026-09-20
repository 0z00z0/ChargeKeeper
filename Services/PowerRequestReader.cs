using System.Diagnostics;

namespace ChargeKeeper.Services;

/// <summary>
/// The outstanding power requests, read by running <c>powercfg /requests</c> and parsing what it
/// prints (<see cref="PowerRequestText"/>).
/// </summary>
/// <remarks>
/// The command refuses without administrator rights — measured — so this reading exists only because
/// the application runs elevated; a child process it starts inherits the rights. A refusal, a
/// missing command or a shape the parser does not understand all come back as no reading, never as
/// an empty list: "nothing is holding the machine awake" is a claim, and a failed read is no
/// evidence for it.
/// </remarks>
internal static class PowerRequestReader
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>The requests Windows currently holds, or null where the list could not be read.</summary>
    internal static IReadOnlyList<PowerRequestEntry>? Read()
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName               = "powercfg.exe",
                Arguments              = "/requests",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var child = Process.Start(start);
            if (child is null) return null;

            string output = child.StandardOutput.ReadToEnd();
            if (!child.WaitForExit((int)Patience.TotalMilliseconds)) return null;
            if (child.ExitCode != 0) return null;

            return PowerRequestText.Parse(output, ThisApplicationExe);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{nameof(PowerRequestReader)}.{nameof(Read)}", ex);
            return null;
        }
    }

    /// <summary>This application's own executable file name, for telling its holds from a stranger's.</summary>
    internal static string ThisApplicationExe
    {
        get
        {
            try
            {
                string? path = Environment.ProcessPath;
                return path is { Length: > 0 } ? Path.GetFileName(path) : "";
            }
            catch { return ""; }
        }
    }
}
