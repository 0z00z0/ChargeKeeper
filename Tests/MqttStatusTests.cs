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

    // The broker line reports what is actually in force: a pinned value where there is one, the
    // remembered one where there is not, and no half-answer in between.
    [Fact]
    public void Broker_ReportsPinnedValuesAndFoundOnesAlike()
    {
        var memory = new MqttEndpointMemory("mq.laget.no", "ck", 443, MqttTransport.WebSocket, Encrypted: true);

        Assert.Equal("mq.laget.no:443 over WebSocket", MqttStatusFormatter.DescribeBroker(
            new MqttEndpointRequest("mq.laget.no", "ck", null, MqttTransportSetting.Auto), memory));

        Assert.Equal("mq.laget.no:8883 over TCP", MqttStatusFormatter.DescribeBroker(
            new MqttEndpointRequest("mq.laget.no", "ck", 8883, MqttTransportSetting.Tcp), null));

        // A pinned port with the transport left to the search still reads off the cache.
        Assert.Equal("mq.laget.no:9001 over WebSocket", MqttStatusFormatter.DescribeBroker(
            new MqttEndpointRequest("mq.laget.no", "ck", 9001, MqttTransportSetting.Auto), memory));
    }

    // A cache belongs to its own host: on another one it says nothing, and reporting its address
    // there would name a broker this machine has never reached.
    [Fact]
    public void Broker_WithNothingPinnedAndNothingFoundHere_SaysSoRatherThanGuessing()
    {
        var elsewhere = new MqttEndpointMemory("mq.laget.no", "ck", 443, MqttTransport.WebSocket, Encrypted: true);

        Assert.Equal("10.0.20.22 — not connected yet", MqttStatusFormatter.DescribeBroker(
            new MqttEndpointRequest("10.0.20.22", "ck", null, MqttTransportSetting.Auto), elsewhere));
        Assert.Equal("Not set", MqttStatusFormatter.DescribeBroker(
            new MqttEndpointRequest("  ", "ck", null, MqttTransportSetting.Auto), elsewhere));
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

// The connection check's decision layer. The socket work is not unit-testable here; what matters is
// that "nothing answered", "these credentials were refused" and "connected" stay three verdicts.
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

    // A typo'd host and a closed port are both unreachable, while a broker that answers nothing is a
    // timeout: the user's next move differs.
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

    // The budget expiring is a timeout; the caller's token being cancelled is no verdict about the
    // broker at all.
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
        var tcp = MqttTransport.Tcp;
        string success     = MqttConnectionProbe.Describe(new(MqttProbeOutcome.Success, ""), tcp);
        string unreachable = MqttConnectionProbe.Describe(new(MqttProbeOutcome.Unreachable, "connection refused"), tcp);
        string auth        = MqttConnectionProbe.Describe(new(MqttProbeOutcome.AuthRejected, "NotAuthorized"), tcp);

        Assert.Equal(3, new[] { success, unreachable, auth }.Distinct().Count());
        Assert.Contains("Connected", success);
        Assert.Contains("Could not reach", unreachable);
        Assert.Contains("rejected these credentials", auth);
    }

    // The transport is the point of the answer under Auto, so no verdict may be silent about it.
    [Fact]
    public void Describe_NamesTheTransportInEverySentence()
    {
        foreach (var outcome in Enum.GetValues<MqttProbeOutcome>())
        {
            Assert.Contains("TCP", MqttConnectionProbe.Describe(new(outcome, "x"), MqttTransport.Tcp));
            Assert.Contains("WebSocket", MqttConnectionProbe.Describe(new(outcome, "x"), MqttTransport.WebSocket));
        }
    }

    [Fact]
    public void IsFailure_IsTrueForEverythingButSuccess()
    {
        Assert.False(MqttConnectionProbe.IsFailure(Report((MqttTransport.Tcp, MqttProbeOutcome.Success))));
        Assert.True (MqttConnectionProbe.IsFailure(Report((MqttTransport.Tcp, MqttProbeOutcome.AuthRejected))));
        Assert.True (MqttConnectionProbe.IsFailure(Report((MqttTransport.Tcp, MqttProbeOutcome.Unreachable))));
    }

    // A run that reached the broker names the transport that did and keeps the other as context; a
    // run that reached nothing has no single verdict, so it lists every transport tried.
    [Fact]
    public void Describe_Report_SeparatesAFallbackFromReachingNothing()
    {
        string fellBack = MqttConnectionProbe.Describe(Report(
            (MqttTransport.Tcp, MqttProbeOutcome.Unreachable),
            (MqttTransport.WebSocket, MqttProbeOutcome.Success)));
        Assert.StartsWith("Connected over WebSocket.", fellBack);
        Assert.Contains("TCP could not be reached", fellBack);

        string nothing = MqttConnectionProbe.Describe(Report(
            (MqttTransport.Tcp, MqttProbeOutcome.Unreachable),
            (MqttTransport.WebSocket, MqttProbeOutcome.TimedOut)));
        Assert.StartsWith("Neither transport reached the broker.", nothing);
        Assert.Contains("TCP could not be reached", nothing);
        Assert.Contains("WebSocket did not answer", nothing);

        Assert.NotEqual(fellBack, nothing);
    }

    // A refused credential and a closed port must not read alike: one sends the user to the broker's
    // user list, the other to its ports.
    [Fact]
    public void Describe_Report_TellsRefusedCredentialsFromAClosedPort()
    {
        string auth = MqttConnectionProbe.Describe(Report((MqttTransport.Tcp, MqttProbeOutcome.AuthRejected)));
        string shut = MqttConnectionProbe.Describe(Report((MqttTransport.Tcp, MqttProbeOutcome.Unreachable)));

        Assert.Contains("rejected these credentials", auth);
        Assert.Contains("Could not reach the broker", shut);
        Assert.DoesNotContain("credential", shut);
    }

    [Fact]
    public void Describe_Report_WithNothingTried_SaysThereIsNoHost()
    {
        Assert.Equal("No broker host set.", MqttConnectionProbe.Describe(new MqttProbeReport([])));
    }

    private static MqttProbeReport Report(params (MqttTransport Transport, MqttProbeOutcome Outcome)[] attempts) =>
        new([.. attempts.Select(a => new MqttEndpointAttempt(new MqttEndpointCandidate(1883, a.Transport), a.Outcome))]);

    // The lines the page shows under the button row while a sweep runs. Pinned exactly: they are the
    // only account of what a search that can take tens of seconds is doing, and every one of them has
    // to name the endpoint it is about.
    [Fact]
    public void DescribeProgress_NamesTheEndpointAtEveryStage()
    {
        Assert.Equal("Trying WebSocket on port 443…",
            MqttConnectionProbe.Describe(new MqttDetectProgress(MqttDetectStage.Port, 443, MqttTransport.WebSocket)));
        Assert.Equal("Trying WebSocket on port 443 — asking the broker…",
            MqttConnectionProbe.Describe(new MqttDetectProgress(MqttDetectStage.Transport, 443, MqttTransport.WebSocket)));
        Assert.Equal("Trying TCP on port 1883 — asking the broker…",
            MqttConnectionProbe.Describe(new MqttDetectProgress(MqttDetectStage.Transport, 1883, MqttTransport.Tcp)));
    }

    // Progress is an account of the search, not a spinner: each candidate's own verdict is reported
    // as it lands, in the same words the summary would use for it.
    [Fact]
    public void DescribeProgress_ReportsEachCandidatesOutcomeAsItLands()
    {
        Assert.Equal("TCP on port 1883 connected.", Finished(1883, MqttTransport.Tcp,
            new MqttProbeResult(MqttProbeOutcome.Success, "")));
        Assert.Equal("TCP on port 8883 could not be reached (nothing is listening on that port).",
            Finished(8883, MqttTransport.Tcp,
                new MqttProbeResult(MqttProbeOutcome.Unreachable, "nothing is listening on that port")));
        Assert.Equal("WebSocket on port 443 rejected these credentials.", Finished(443, MqttTransport.WebSocket,
            new MqttProbeResult(MqttProbeOutcome.AuthRejected, "NotAuthorized")));

        // Never reported without a result, but an empty one must not read as a success.
        Assert.Equal("TCP on port 1883 — no answer recorded.",
            MqttConnectionProbe.Describe(new MqttDetectProgress(MqttDetectStage.Finished, 1883, MqttTransport.Tcp)));
    }

    private static string Finished(int port, MqttTransport transport, MqttProbeResult result) =>
        MqttConnectionProbe.Describe(new MqttDetectProgress(MqttDetectStage.Finished, port, transport, result));

    // A run that reached nothing can now hold eight attempts of the same clause; one of each is the
    // whole of what the user can act on.
    [Fact]
    public void Describe_Report_DoesNotRepeatTheSameClause()
    {
        string sentence = MqttConnectionProbe.Describe(new MqttProbeReport(
        [
            new(new MqttEndpointCandidate(1883, MqttTransport.Tcp), MqttProbeOutcome.Unreachable),
            new(new MqttEndpointCandidate(8883, MqttTransport.Tcp), MqttProbeOutcome.Unreachable),
            new(new MqttEndpointCandidate(443,  MqttTransport.WebSocket), MqttProbeOutcome.Unreachable),
        ]));

        Assert.StartsWith("Neither transport reached the broker.", sentence);
        Assert.Equal(1, CountOf(sentence, "TCP could not be reached"));
        Assert.Equal(1, CountOf(sentence, "WebSocket could not be reached"));
    }

    // The verdict's context is the other transport, not whichever attempt happened to be second-last:
    // a sweep can put several same-transport failures between the two facts worth reporting together.
    [Fact]
    public void Describe_Report_TakesItsContextFromTheOtherTransport()
    {
        string sentence = MqttConnectionProbe.Describe(new MqttProbeReport(
        [
            new(new MqttEndpointCandidate(1883, MqttTransport.Tcp), MqttProbeOutcome.Unreachable),
            new(new MqttEndpointCandidate(9001, MqttTransport.WebSocket), MqttProbeOutcome.TimedOut),
            new(new MqttEndpointCandidate(443,  MqttTransport.WebSocket), MqttProbeOutcome.Success),
        ]));

        Assert.StartsWith("Connected over WebSocket.", sentence);
        Assert.Contains("TCP could not be reached", sentence);
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
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
