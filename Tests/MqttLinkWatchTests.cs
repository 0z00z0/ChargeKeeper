using System;
using System.Diagnostics;
using System.IO;
using ChargeKeeper.Services;
using Xunit;
using ZeroZero.Mqtt;

namespace ChargeKeeper.Tests;

/// <summary>
/// A broker out of reach filled the log with an error per channel per publish. The loss and the
/// return are now one line each, and nothing is requested from the connection while it is down.
/// </summary>
public class MqttLinkWatchTests
{
    [Fact]
    public void ALossAndItsReturnAreOneLineEach_HoweverManyRetriesLieBetween()
    {
        var watch = new MqttLinkWatch();
        long start = Stopwatch.GetTimestamp();

        Assert.Null(watch.OnState(MqttConnectionState.Connected, start));

        Assert.Contains("not connected", watch.OnState(MqttConnectionState.Retrying, start), StringComparison.Ordinal);
        foreach (var state in new[] { MqttConnectionState.Searching, MqttConnectionState.Retrying,
                                      MqttConnectionState.Connecting, MqttConnectionState.Failed })
            Assert.Null(watch.OnState(state, start));

        long later = start + 5 * 60 * Stopwatch.Frequency;
        Assert.Equal("MQTT: connected to the broker again after 5 min — publishing resumes, and the current state is sent again.",
                     watch.OnState(MqttConnectionState.Connected, later));
        Assert.Null(watch.OnState(MqttConnectionState.Connected, later));
    }

    [Fact]
    public void SwitchingPublishingOffIsNotALoss()
    {
        var watch = new MqttLinkWatch();
        long now = Stopwatch.GetTimestamp();

        Assert.NotNull(watch.OnState(MqttConnectionState.Retrying, now));
        Assert.Null(watch.OnState(MqttConnectionState.Disabled, now));
        Assert.Null(watch.OnState(MqttConnectionState.Connected, now));
    }

    /// <summary>The publisher owns a live connection and a settings file, so the gate is read out
    /// of the source: both publish requests go through it, and nothing else asks the connection.</summary>
    [Fact]
    public void NoPublishIsRequestedWhileTheConnectionIsDown()
    {
        string source = File.ReadAllText(RepoFiles.Find("Services/MqttPublisher.cs"));

        Assert.Contains("if (_connection.IsConnected) _connection.RequestPublish();",
                        SourceMethods.Body(source, "RequestPublishWhileConnected"), StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(source, "_connection.RequestPublish("));
        Assert.Contains("RequestPublishWhileConnected()", SourceMethods.Body(source, "PublishState"), StringComparison.Ordinal);
        Assert.Contains("RequestPublishWhileConnected()", SourceMethods.Body(source, "PublishSurfaceNow"), StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
