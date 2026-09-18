using NLog.Config;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Loads the real, shipped <c>nlog.config</c> — the only place logging is configured, now that
/// <c>AppLog</c> builds no code-side copy of it — for tests that need a working
/// <see cref="LoggingConfiguration"/> to drive or redirect.
/// </summary>
internal static class ShippedNLogConfig
{
    /// <summary>
    /// Loads nlog.config with <c>throwConfigExceptions</c> forced on. Under NLog's default a
    /// misspelled or stale-version attribute is silently ignored, leaving a config that reads
    /// correctly and does nothing.
    /// </summary>
    public static LoggingConfiguration LoadStrictly()
    {
        var xml = File.ReadAllText(RepoFiles.Find("nlog.config"));
        Assert.Contains("<nlog ", xml);
        return XmlLoggingConfiguration.CreateFromXmlString(
            xml.Replace("<nlog ", "<nlog throwConfigExceptions=\"true\" "));
    }
}
