using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// End-to-end verdicts for the MQTT connection check, against loopback only. The pure classifiers
/// are covered in <see cref="MqttConnectionProbeTests"/>; what these add is that
/// <see cref="MqttConnectionProbe.RunAsync"/> reaches each one. MQTTnet 5 returns a refused CONNACK
/// as a result code rather than throwing, and if that flips, the auth verdict degrades silently
/// into a generic failure.
/// </summary>
public class MqttProbeLoopbackTests
{
    // Pinned to TCP: these exercise the socket/CONNACK stages against a loopback listener, and Auto
    // would follow every failure with a WebSocket attempt nothing here serves.
    private static MqttProbeTarget Target(int port) =>
        new("127.0.0.1", port, "user", "secret", UseTls: false, ClientId: "chargekeeper_probe",
            Transport: MqttTransportSetting.Tcp);

    /// <summary>A port that was bound and released, so a connect to it is refused rather than hanging.</summary>
    private static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task NothingListening_IsUnreachable_NotAnAuthFailure()
    {
        var report = await MqttConnectionProbe.RunAsync(Target(ClosedPort()), CancellationToken.None);

        Assert.Equal(MqttProbeOutcome.Unreachable, report.Outcome);
        Assert.Contains("Could not reach the broker", MqttConnectionProbe.Describe(report));
    }

    [Fact]
    public async Task BrokerRefusesTheCredentials_IsAuthRejected()
    {
        using var broker = new FakeBroker(reject: true);

        var report = await MqttConnectionProbe.RunAsync(Target(broker.Port), CancellationToken.None);

        Assert.Equal(MqttProbeOutcome.AuthRejected, report.Outcome);
        Assert.Contains("rejected these credentials", MqttConnectionProbe.Describe(report));
    }

    [Fact]
    public async Task BrokerAcceptsTheConnection_IsSuccess()
    {
        using var broker = new FakeBroker(reject: false);

        var report = await MqttConnectionProbe.RunAsync(Target(broker.Port), CancellationToken.None);

        Assert.Equal(MqttProbeOutcome.Success, report.Outcome);
        Assert.Equal(MqttTransport.Tcp, report.Transport);
        Assert.Contains("Connected", MqttConnectionProbe.Describe(report));
    }

    /// <summary>Auto must actually try the second transport, not report the first one's failure as
    /// the whole answer. Nothing serves WebSocket on loopback either, so both attempts fail — what is
    /// asserted is that both were made and both are named. The port is pinned so the sweep is exactly
    /// the two transports: with the port left to the search this would be eight attempts of the same
    /// nothing.</summary>
    [Fact]
    public async Task AutoOnAPinnedPort_WhenTcpIsClosed_AlsoTriesWebSocket()
    {
        var target = new MqttProbeTarget("127.0.0.1", ClosedPort(), "user", "secret",
            UseTls: false, ClientId: "chargekeeper_probe", Transport: MqttTransportSetting.Auto);

        var report = await MqttConnectionProbe.RunAsync(target, CancellationToken.None);

        Assert.Equal(2, report.Attempts.Count);
        Assert.Equal(MqttTransport.Tcp,       report.Attempts[0].Candidate.Transport);
        Assert.Equal(MqttTransport.WebSocket, report.Attempts[1].Candidate.Transport);

        string sentence = MqttConnectionProbe.Describe(report);
        Assert.Contains("Neither transport reached the broker", sentence);
        Assert.Contains("TCP", sentence);
        Assert.Contains("WebSocket", sentence);
    }

    /// <summary>A successful run has to report which port and transport answered — that record is the
    /// whole of what makes the next connect a single attempt instead of another sweep.</summary>
    [Fact]
    public async Task ASuccessfulRun_ReportsThePortAndTransportThatAnswered()
    {
        using var broker = new FakeBroker(reject: false);

        var report = await MqttConnectionProbe.RunAsync(Target(broker.Port), CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(broker.Port, report.Candidate.Port);
        Assert.Equal(MqttTransport.Tcp, report.Candidate.Transport);
    }

    /// <summary>The cache is where the sweep starts, not where it stops. A remembered endpoint that
    /// has gone dead must not strand the connection: the live broker sits elsewhere, and the sweep
    /// behind the stale entry has to reach it.</summary>
    [Fact]
    public async Task AStaleCacheEntry_CostsOneAttempt_NotTheConnection()
    {
        int dead = ClosedPort();
        var stale = new MqttEndpointMemory("127.0.0.1", "user", dead, MqttTransport.Tcp);

        // The port is left to the search, or pinning one would filter the stale entry out before it
        // could be tried — which is a different behaviour from the one under test here.
        var target = new MqttProbeTarget("127.0.0.1", null, "user", "secret",
            UseTls: false, ClientId: "chargekeeper_probe",
            Transport: MqttTransportSetting.Tcp, Memory: stale);

        var report = await MqttConnectionProbe.RunAsync(target, CancellationToken.None);

        Assert.Equal(dead, report.Attempts[0].Candidate.Port);   // the remembered endpoint leads
        Assert.True(report.Attempts.Count > 1);                  // …and losing it does not end the run
    }

    /// <summary>The progress line is what the page shows under the button row while a sweep runs, so
    /// the port stage has to be reported before anything is known about the endpoint, and each
    /// candidate's verdict as it lands.</summary>
    [Fact]
    public async Task ARun_ReportsTheStagesItIsOnAndEachVerdict()
    {
        using var broker = new FakeBroker(reject: false);
        var seen = new List<string>();

        await MqttConnectionProbe.RunAsync(Target(broker.Port), CancellationToken.None,
            new Progress<MqttDetectProgress>(p => { lock (seen) seen.Add(MqttConnectionProbe.Describe(p)); }));

        // Progress<T> posts asynchronously, so settle before reading rather than racing the last report.
        await Task.Delay(250);
        lock (seen)
        {
            Assert.Contains($"Trying TCP on port {broker.Port}…", seen);
            Assert.Contains($"TCP on port {broker.Port} connected.", seen);
        }
    }

    /// <summary>Cancellation is what a closing window and a retyped host both do. The run has to come
    /// back rather than keep a socket open behind a page that is gone.</summary>
    [Fact]
    public async Task ACancelledRun_StopsRatherThanFinishingTheSweep()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var report = await MqttConnectionProbe.RunAsync(Target(ClosedPort()), cts.Token);

        Assert.True(report.Attempts.Count <= 1);   // at most the one already in flight
    }

    /// <summary>
    /// The smallest thing that can answer a CONNECT: accept, read far enough to see the client's
    /// protocol version, reply with a matching CONNACK, then hold the socket open long enough for
    /// the client to call the session live.
    /// <para>It accepts in a loop and drops zero-byte connections because the probe's stage-1
    /// reachability check opens and closes a socket before any MQTT traffic.</para>
    /// </summary>
    private sealed class FakeBroker : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        public int Port { get; }

        public FakeBroker(bool reject)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => ServeAsync(reject));
        }

        private async Task ServeAsync(bool reject)
        {
            try
            {
                var buf = new byte[512];
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    int n = await stream.ReadAsync(buf, _stop.Token);
                    if (n == 0) continue;   // the stage-1 TCP check; wait for the real CONNECT

                    await stream.WriteAsync(Connack(ProtocolLevel(buf), reject), _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                    // The client treats the session as established only once the socket survives the
                    // CONNACK, so don't slam it shut in the same breath.
                    await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token);
                }
            }
            catch { /* torn down by Dispose */ }
        }

        // CONNECT variable header: 2-byte protocol-name length, the name, then the level byte.
        private static byte ProtocolLevel(byte[] connect)
        {
            int i = 1;
            while ((connect[i] & 0x80) != 0) i++;   // past the remaining-length varint
            i++;
            int nameLength = (connect[i] << 8) | connect[i + 1];
            return connect[i + 2 + nameLength];
        }

        // v5 carries a reason code plus a property length; v3.1.1 carries a return code. 0x87/0x05
        // are each version's "not authorised".
        private static byte[] Connack(byte level, bool reject) => level >= 5
            ? [0x20, 0x03, 0x00, reject ? (byte)0x87 : (byte)0x00, 0x00]
            : [0x20, 0x02, 0x00, reject ? (byte)0x05 : (byte)0x00];

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
