using System.Text.RegularExpressions;
using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The experimental promotion of the tray icons out of the overflow flyout. Nothing here touches
/// the machine's own tray: the policy is exercised against a fake store, and the two properties
/// that cannot be seen from a passing test — that nothing is written while the setting is off, and
/// that the original value is kept before anything is overwritten — are what these pin.
/// </summary>
public class TrayIconPromotionTests
{
    /// <summary>A store in memory. <see cref="Missing"/> stands for an icon the shell has no record
    /// of, which is not the same as one recorded as not promoted.</summary>
    private sealed class FakeStore : ITrayPromotionStore
    {
        private readonly Dictionary<Guid, int?> _values = [];
        private readonly HashSet<Guid>          _known  = [];

        internal List<string> Writes { get; } = [];

        internal void Known(Guid icon, int? value)
        {
            _known.Add(icon);
            if (value is { } v) _values[icon] = v;
        }

        public int? Read(Guid icon) => _values.TryGetValue(icon, out var v) ? v : null;

        public bool Write(Guid icon, int? value)
        {
            Writes.Add($"{icon:B} = {(value is { } v ? v.ToString() : "removed")}");
            if (!_known.Contains(icon)) return false;

            if (value is { } flag) _values[icon] = flag;
            else                   _values.Remove(icon);

            return true;
        }
    }

    private static readonly Guid Main   = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Second = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void PromotingRemembersWhatWasThereBeforeItWrites()
    {
        var store = new FakeStore();
        store.Known(Main, null);          // the shell knows the icon and has never promoted it
        store.Known(Second, 0);           // recorded as explicitly not promoted
        var memory = new List<TrayPromotionMemory>();

        Assert.True(TrayIconPromotion.Apply(true, [Main, Second], memory, store));

        Assert.Equal(1, store.Read(Main));
        Assert.Equal(1, store.Read(Second));
        Assert.Equal(2, memory.Count);
        Assert.Null(memory.Single(m => m.Icon == TrayIconPromotion.Braced(Main)).Previous);
        Assert.Equal(0, memory.Single(m => m.Icon == TrayIconPromotion.Braced(Second)).Previous);
    }

    [Fact]
    public void ASecondPassDoesNotOverwriteWhatWasRemembered()
    {
        var store = new FakeStore();
        store.Known(Main, 0);
        var memory = new List<TrayPromotionMemory>();

        TrayIconPromotion.Apply(true, [Main], memory, store);
        TrayIconPromotion.Apply(true, [Main], memory, store);

        // Recording the value this class itself wrote would make the restore promote it for ever.
        Assert.Single(memory);
        Assert.Equal(0, memory[0].Previous);
    }

    [Fact]
    public void SwitchingItOffPutsBackExactlyWhatWasThere()
    {
        var store = new FakeStore();
        store.Known(Main, null);
        store.Known(Second, 0);
        var memory = new List<TrayPromotionMemory>();

        TrayIconPromotion.Apply(true, [Main, Second], memory, store);
        Assert.True(TrayIconPromotion.Apply(false, [Main, Second], memory, store));

        // Absent before, absent after — a zero would be a value the shell never held.
        Assert.Null(store.Read(Main));
        Assert.Equal(0, store.Read(Second));
        Assert.Empty(memory);
    }

    // The two application-side properties the requirement states.

    [Fact]
    public void NothingIsWrittenWhileTheSettingIsOffAndThereIsNothingToRestore()
    {
        string body = SourceMethods.Body(
            Regex.Replace(File.ReadAllText(RepoFiles.Find("App.xaml.cs")), @"//[^\r\n]*", string.Empty),
            "ApplyTrayPromotion");

        int guard = body.IndexOf("stored.Count == 0", StringComparison.Ordinal);
        int apply = body.IndexOf("TrayIconPromotion.Apply", StringComparison.Ordinal);
        Assert.True(guard >= 0, "The off-and-nothing-to-restore short circuit is gone.");
        Assert.True(guard < apply, "The registry is reached before the short circuit.");
    }
}
