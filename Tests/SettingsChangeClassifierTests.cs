using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using ChargeKeeper.Services;
using Xunit;
using Xunit.Sdk;

namespace ChargeKeeper.Tests;

/// <summary>
/// The classification behind <c>SettingsService.ChangeCommitted</c>: whether a committed change
/// reached anything outside this process. The direction is the point — the named list is what to
/// skip, so a property added later counts as mattering without anyone remembering to say so.
/// </summary>
public class SettingsChangeClassifierTests
{
    [Fact]
    public void AnUnchangedSettingsObjectDoesNotMatter()
    {
        var before = new AppSettings();
        var after  = new AppSettings();

        Assert.False(SettingsChangeClassifier.IsMaterial(before, after));
    }

    /// <summary>The endpoint memory is written back on every successful broker connect, which is
    /// what makes this the case worth having.</summary>
    [Fact]
    public void AChangeToAnExcludedFieldDoesNotMatter()
    {
        var before = new AppSettings();
        var after  = new AppSettings
        {
            MqttLastGoodEndpoint = new ZeroZero.Mqtt.MqttEndpointMemory(
                "broker.invalid", "user", 1883, ZeroZero.Mqtt.MqttTransport.Tcp),
            SettingsWindowX = 120,
            SettingsWindowY = 240,
        };

        Assert.False(SettingsChangeClassifier.IsMaterial(before, after));
    }

    [Fact]
    public void AChangeToAnyOtherFieldMatters()
    {
        var before = new AppSettings();
        var after  = new AppSettings { LowBatteryWarningPct = before.LowBatteryWarningPct + 1 };

        Assert.True(SettingsChangeClassifier.IsMaterial(before, after));
    }

    /// <summary>An excluded field moving alongside a published one still matters: the exclusion
    /// removes a field from the comparison, never a whole change from it.</summary>
    [Fact]
    public void AnExcludedFieldMovingBesideAPublishedOneStillMatters()
    {
        var before = new AppSettings();
        var after  = new AppSettings { SettingsWindowX = 120, IconMode = TrayIconMode.Numeric };

        Assert.True(SettingsChangeClassifier.IsMaterial(before, after));
    }

    /// <summary>
    /// The guard the design rests on. Every persisted setting either is excluded by name — and then
    /// really does read as not mattering — or reaches the comparison and makes a change matter. A
    /// property added without a decision fails here rather than silently falling out of every
    /// notification.
    /// </summary>
    /// <remarks>Read-only properties are skipped: they are derived from the settable ones and
    /// cannot move on their own.</remarks>
    [Fact]
    public void EverySettingsPropertyIsEitherExcludedByNameOrReachesTheComparison()
    {
        foreach (var property in PersistedProperties())
        {
            var before = new AppSettings();
            var after  = new AppSettings();
            MoveOff(after, property);

            Assert.True(
                SettingsChangeClassifier.Snapshot(before) != SettingsChangeClassifier.Snapshot(after),
                $"'{property.Name}' moved but the settings snapshot did not, so it never reaches the "
              + "comparison and no change to it can ever be announced.");

            bool excluded = SettingsChangeClassifier.UnpublishedProperties.Contains(property.Name);
            bool material = SettingsChangeClassifier.IsMaterial(before, after);

            if (excluded)
                Assert.False(material, $"'{property.Name}' is excluded by name but still read as mattering.");
            else
                Assert.True(material, $"'{property.Name}' is not excluded by name and must make a change matter.");
        }
    }

    /// <summary>A name that no longer matches a property excludes nothing, and would leave the
    /// property it was meant to cover republishing in silence.</summary>
    [Fact]
    public void EveryExcludedNameIsStillAPropertyThatExists()
    {
        var names = PersistedProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var excluded in SettingsChangeClassifier.UnpublishedProperties)
            Assert.Contains(excluded, names);
    }

    private static PropertyInfo[] PersistedProperties() =>
        typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToArray();

    /// <summary>Moves one property to a value it does not already hold, whatever its type. An
    /// unhandled type throws rather than passing quietly: a new kind of setting has to be given a
    /// rule here before the guard above can vouch for it.</summary>
    private static void MoveOff(AppSettings settings, PropertyInfo property)
    {
        object? value   = property.GetValue(settings);
        Type    bare    = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        bool    nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null;

        if (bare == typeof(bool))
        {
            property.SetValue(settings, value is bool flag ? !flag : true);
            return;
        }

        if (bare == typeof(int))
        {
            property.SetValue(settings, value is int number ? number + 1 : 1);
            return;
        }

        if (bare == typeof(string))
        {
            property.SetValue(settings, value as string == "moved" ? "moved again" : "moved");
            return;
        }

        if (bare.IsEnum)
        {
            object first = Enum.GetValues(bare).Cast<object>().First(v => !Equals(v, value));
            property.SetValue(settings, first);
            return;
        }

        if (value is IList list)
        {
            if (list.Count > 0) list.RemoveAt(list.Count - 1);
            else list.Add(Instantiate(property.PropertyType.GetGenericArguments()[0]));
            return;
        }

        if (!bare.IsValueType || nullable)
        {
            property.SetValue(settings, value is null ? Instantiate(bare) : null);
            return;
        }

        throw new XunitException(
            $"'{property.Name}' is a {property.PropertyType.Name}, which this guard has no rule for. "
          + "Add one, or the property is neither exercised nor protected.");
    }

    /// <summary>Any instance of a type, built through whichever constructor asks for least. Only
    /// distinctness matters — nothing reads the values back.</summary>
    private static object Instantiate(Type type)
    {
        var constructor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        var arguments   = constructor.GetParameters().Select(p => SampleValue(p.ParameterType)).ToArray();
        return constructor.Invoke(arguments);
    }

    private static object? SampleValue(Type type)
    {
        Type bare = Nullable.GetUnderlyingType(type) ?? type;

        if (bare == typeof(string)) return "sample";
        if (bare.IsEnum)            return Enum.GetValues(bare).GetValue(0);
        if (Nullable.GetUnderlyingType(type) is not null) return null;
        return bare.IsValueType ? Activator.CreateInstance(bare) : null;
    }
}
