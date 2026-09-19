using System.Runtime.CompilerServices;
using ZeroZero.Primitives;

namespace ChargeKeeper.Services;

/// <summary>The shared library's log sink over <see cref="AppLog"/> — the one sink every adopted
/// component writes through. A component owns no logging framework and sanitises an exception
/// before it gets here, so no staged credential reaches the file.</summary>
/// <remarks>The class column names the file that created the sink, taken when it is constructed,
/// so a component's lines read as coming from the code that wired it rather than from here.</remarks>
internal sealed class AppLogSink([CallerFilePath] string ownerFilePath = "") : ILogSink
{
    public void Info(string message) => AppLog.Info(message, ownerFilePath);

    public void Error(string source, Exception? ex) => AppLog.Error(source, ex, ownerFilePath);
}
