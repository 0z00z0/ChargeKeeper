using System;
using System.Net.Sockets;
using System.Threading;
using ChargeKeeper.Services;
using MQTTnet;
using Xunit;

namespace ChargeKeeper.Tests;

// The MQTT page's two status lines. Only the rendering is tested — MqttActivity itself is two
// interlocked slots written by the live MQTT threads, and the clock is passed in so "2 min ago" is
// pinned without waiting for one.
public class MqttStatusFormatterTests
{
    private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Relative_NothingYet_SaysSoRatherThanBlankOrZero()
    {
        Assert.Equal("Nothing published yet", MqttStatusFormatter.DescribeLastPublish(null, Now));
        Assert.Equal("Nothing received yet",  MqttStatusFormatter.DescribeLastCommand(null, Now));
    }

    [Fact]
    public void Relative_ScalesFromSecondsToDays()
    {
        Assert.Equal("just now",    MqttStatusFormatter.Relative(Now.AddSeconds(-5),  Now, "never"));
        Assert.Equal("just now",    MqttStatusFormatter.Relative(Now.AddSeconds(-59), Now, "never"));
        Assert.Equal("1 min ago",   MqttStatusFormatter.Relative(Now.AddMinutes(-1),  Now, "never"));
        Assert.Equal("2 min ago",   MqttStatusFormatter.Relative(Now.AddMinutes(-2),  Now, "never"));
        Assert.Equal("59 min ago",  MqttStatusFormatter.Relative(Now.AddMinutes(-59), Now, "never"));
        Assert.Equal("1 hour ago",  MqttStatusFormatter.Relative(Now.AddHours(-1),    Now, "never"));
        Assert.Equal("23 hours ago", MqttStatusFormatter.Relative(Now.AddHours(-23),  Now, "never"));
        Assert.Equal("1 day ago",   MqttStatusFormatter.Relative(Now.AddDays(-1),     Now, "never"));
        Assert.Equal("9 days ago",  MqttStatusFormatter.Relative(Now.AddDays(-9),     Now, "never"));
    }

    // A timestamp written just before a clock adjustment reads as future-dated; that must not surface
    // as a negative age.
    [Fact]
    public void Relative_FutureTimestamp_ReadsAsJustNow()
    {
        Assert.Equal("just now", MqttStatusFormatter.Relative(Now.AddMinutes(5), Now, "never"));
    }

    [Fact]
    public void LastCommand_NamesTheEntityTheUserSeesInHomeAssistant()
    {
        var record = new MqttCommandRecord(Now.AddMinutes(-3), HaCommandKind.SetPreset);
        Assert.Equal("Charge preset — 3 min ago", MqttStatusFormatter.DescribeLastCommand(record, Now));

        Assert.Equal("Smart Charge",        MqttStatusFormatter.CommandLabel(HaCommandKind.SmartCharge));
        Assert.Equal("Charge start",        MqttStatusFormatter.CommandLabel(HaCommandKind.ChargeStart));
        Assert.Equal("Charge stop",         MqttStatusFormatter.CommandLabel(HaCommandKind.ChargeStop));
        Assert.Equal("Charge to full once", MqttStatusFormatter.CommandLabel(HaCommandKind.ChargeToFull));
    }
}

// The connection check's decision layer. The socket work itself isn't unit-testable here; what is
// testable — and what the feature exists for — is that "nothing answered", "answered and said no to
// these credentials" and "connected" come out as three DIFFERENT things.
public class MqttConnectionProbeTests
{
    [Fact]
    public void Connack_AuthCodes_AreTheirOwnOutcome()
    {
        Assert.Equal(MqttProbeOutcome.AuthRejected,
            MqttConnectionProbe.ClassifyConnack(MqttClientConnectResultCode.BadUserNameOrPassword, null).Outcome);
        // A broker with anonymous access off answers NotAuthorized to a blank username — same user error.
        Assert.Equal(MqttProbeOutcome.AuthRejected,
            MqttConnectionProbe.ClassifyConnack(MqttClientConnectResultCode.NotAuthorized, null).Outcome);
    }

    [Fact]
    public void Connack_Success_AndOtherRefusals_AreNotAuthFailures()
    {
        Assert.Equal(MqttProbeOutcome.Success,
            MqttConnectionProbe.ClassifyConnack(MqttClientConnectResultCode.Success, null).Outcome);
        Assert.Equal(MqttProbeOutcome.Rejected,
            MqttConnectionProbe.ClassifyConnack(MqttClientConnectResultCode.ClientIdentifierNotValid, null).Outcome);
        Assert.Equal(MqttProbeOutcome.Rejected,
            MqttConnectionProbe.ClassifyConnack(MqttClientConnectResultCode.ServerUnavailable, null).Outcome);
    }

    [Fact]
    public void Connack_CarriesTheBrokersOwnReasonWhenItSuppliesOne()
    {
        var withReason = MqttConnectionProbe.ClassifyConnack(MqttClientConnectResultCode.NotAuthorized, "acl denied");
        Assert.Equal("NotAuthorized: acl denied", withReason.Detail);

        var without = MqttConnectionProbe.ClassifyConnack(MqttClientConnectResultCode.NotAuthorized, "  ");
        Assert.Equal("NotAuthorized", without.Detail);
    }

    // A typo'd host and a closed port are both "unreachable", but a broker that answers nothing at all
    // is a timeout — the user's next move differs (fix the name vs check the machine is up).
    [Fact]
    public void SocketErrors_SeparateUnreachableFromNoAnswer()
    {
        Assert.Equal(MqttProbeOutcome.Unreachable, MqttConnectionProbe.ClassifySocketError(SocketError.HostNotFound).Outcome);
        Assert.Equal(MqttProbeOutcome.Unreachable, MqttConnectionProbe.ClassifySocketError(SocketError.ConnectionRefused).Outcome);
        Assert.Equal(MqttProbeOutcome.Unreachable, MqttConnectionProbe.ClassifySocketError(SocketError.HostUnreachable).Outcome);
        Assert.Equal(MqttProbeOutcome.TimedOut,    MqttConnectionProbe.ClassifySocketError(SocketError.TimedOut).Outcome);
        Assert.Equal(MqttProbeOutcome.Failed,      MqttConnectionProbe.ClassifySocketError(SocketError.AccessDenied).Outcome);
    }

    [Fact]
    public void ConnectException_UnwrapsToTheSocketErrorMqttnetWrapped()
    {
        var wrapped = new InvalidOperationException("connect failed",
            new SocketException((int)SocketError.ConnectionRefused));
        Assert.Equal(MqttProbeOutcome.Unreachable,
            MqttConnectionProbe.ClassifyConnectException(wrapped, CancellationToken.None).Outcome);
    }

    // The budget expiring is a timeout; the CALLER's token being cancelled (window closed) is not a
    // verdict about the broker at all.
    [Fact]
    public void ConnectException_TellsATimeoutFromAUserCancellation()
    {
        var cancelled = new OperationCanceledException();
        Assert.Equal(MqttProbeOutcome.TimedOut,
            MqttConnectionProbe.ClassifyConnectException(cancelled, CancellationToken.None).Outcome);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(MqttProbeOutcome.Failed,
            MqttConnectionProbe.ClassifyConnectException(cancelled, cts.Token).Outcome);
    }

    [Fact]
    public void ConnectException_UnknownFailure_KeepsTheTypeButNeverTheCredentials()
    {
        var ex = new InvalidOperationException("TLS handshake failed");
        var result = MqttConnectionProbe.ClassifyConnectException(ex, CancellationToken.None);
        Assert.Equal(MqttProbeOutcome.Failed, result.Outcome);
        Assert.Equal("InvalidOperationException: TLS handshake failed", result.Detail);
    }

    [Fact]
    public void Describe_GivesEachOutcomeADistinctSentence()
    {
        string success     = MqttConnectionProbe.Describe(new(MqttProbeOutcome.Success, ""));
        string unreachable = MqttConnectionProbe.Describe(new(MqttProbeOutcome.Unreachable, "connection refused"));
        string auth        = MqttConnectionProbe.Describe(new(MqttProbeOutcome.AuthRejected, "NotAuthorized"));

        Assert.Equal(3, new[] { success, unreachable, auth }.Distinct().Count());
        Assert.Contains("Connected", success);
        Assert.Contains("Could not reach", unreachable);
        Assert.Contains("rejected these credentials", auth);
    }

    [Fact]
    public void IsFailure_IsTrueForEverythingButSuccess()
    {
        Assert.False(MqttConnectionProbe.IsFailure(new(MqttProbeOutcome.Success, "")));
        Assert.True (MqttConnectionProbe.IsFailure(new(MqttProbeOutcome.AuthRejected, "")));
        Assert.True (MqttConnectionProbe.IsFailure(new(MqttProbeOutcome.Unreachable, "")));
    }

    // The probe must never present itself to the broker as the publisher: an identical client id makes
    // the broker evict the live session, so pressing "Test connection" would drop the real connection.
    [Fact]
    public void ProbeClientId_IsNeverThePublishersOwnId()
    {
        Assert.NotEqual("chargekeeper_b1", MqttConnectionProbe.ProbeClientId("chargekeeper_b1"));
        Assert.StartsWith("chargekeeper_b1", MqttConnectionProbe.ProbeClientId("chargekeeper_b1"));
    }
}
