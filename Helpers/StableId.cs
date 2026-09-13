namespace ChargeKeeper.Helpers;

/// <summary>An identifier for something a person names and renames — a script, a network profile.
/// What is stored refers to the identifier, so a rename breaks nothing. Format-free hexadecimal, so
/// it carries no punctuation a log line or a file name would have to escape.</summary>
internal static class StableId
{
    public static string New() => Guid.NewGuid().ToString("N");
}
