using System.Text.RegularExpressions;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Holds the entity count written out in prose to the count the catalogue actually declares.
/// </summary>
/// <remarks>
/// The number appears spelled out in a class summary, a set of remarks and the README, each of them
/// far from the table it describes. Nothing recomputes them, so they drift every time an entity is
/// added and the drift is invisible: three separate figures were current at once before this test
/// existed. A reader who trusts a stale figure mis-sizes an announcement pass or a migration.
/// </remarks>
public class DocumentedEntityCountTests
{
    /// <summary>Where the count is written in words, and the pattern that captures it. Each pattern
    /// must match, so rewording a sentence out of the guard's reach fails rather than passing.</summary>
    private static readonly (string File, string Pattern)[] Sites =
    [
        (Path.Combine("Services", "MqttEntityCatalog.cs"),
         @"published surface: (?<n>[a-z]+-[a-z]+) entities"),
        (Path.Combine("Services", "MqttPublisher.cs"),
         @"announcement pass asks (?<n>[a-z]+-[a-z]+) entities"),
        (Path.Combine("Services", "MqttPublisher.cs"),
         @"(?<n>[a-z]+-[a-z]+) EC or WMI calls"),
        (Path.Combine("Services", "MqttSettingsMigration.cs"),
         @"rather than announcing (?<n>[a-z]+-[a-z]+) new ones"),
        (Path.Combine("Tests", "MqttEntityCatalogTests.cs"),
         @"declaration: the (?<n>[a-z]+-[a-z]+) entity ids"),
        (Path.Combine("Tests", "MqttSettingsMigrationTests.cs"),
         @"carry-over renames all (?<n>[a-z]+-[a-z]+) entities"),
        ("README.md",
         @"device with (?<n>[a-z]+-[a-z]+) entities"),
        ("README.md",
         @"the topic root, the (?<n>[a-z]+-[a-z]+) entity"),
    ];

    /// <summary>Spelled forms for the range this catalogue plausibly occupies. An unmapped count
    /// fails loudly rather than passing on a comparison nothing could satisfy.</summary>
    private static readonly Dictionary<int, string> Words = new()
    {
        [40] = "forty",       [41] = "forty-one",   [42] = "forty-two",   [43] = "forty-three",
        [44] = "forty-four",  [45] = "forty-five",  [46] = "forty-six",   [47] = "forty-seven",
        [48] = "forty-eight", [49] = "forty-nine",  [50] = "fifty",       [51] = "fifty-one",
        [52] = "fifty-two",   [53] = "fifty-three", [54] = "fifty-four",  [55] = "fifty-five",
    };

    /// <summary>The catalogue's own count, composed the way the publisher composes it.</summary>
    private static int DeclaredEntityCount() => MqttTestBed.Declared().All.Count;

    [Fact]
    public void EveryProseCountMatchesTheCatalogue()
    {
        int count = DeclaredEntityCount();
        Assert.True(Words.ContainsKey(count),
            $"The catalogue declares {count} entities, outside the range this test spells. " +
             "Extend the table above with its written form.");
        string expected = Words[count];

        foreach ((string file, string pattern) in Sites)
        {
            string text = File.ReadAllText(RepoFiles.Find(file));
            var matches = Regex.Matches(text, pattern);

            Assert.True(matches.Count > 0,
                $"{file}: nothing matched /{pattern}/. The sentence carrying the entity count was " +
                 "reworded out of this guard's reach — update the pattern rather than deleting it.");

            foreach (Match match in matches)
            {
                string written = match.Groups["n"].Value;
                Assert.True(written == expected,
                    $"{file} says '{written}' where the catalogue declares {count} ('{expected}').");
            }
        }
    }
}
