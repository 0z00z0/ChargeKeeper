using System;
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
    /// asserted is that both were made and both are named.</summary>
    [Fact]
    public async Task Auto_WhenTcpIsClosed_AlsoTriesWebSocket()
    {
        var target = new MqttProbeTarget("127.0.0.1", ClosedPort(), "user", "secret",
            UseTls: false, ClientId: "chargekeeper_probe", Transport: MqttTransportSetting.Auto);

        var report = await MqttConnectionProbe.RunAsync(target, CancellationToken.None);

        Assert.Equal(2, report.Attempts.Count);
        Assert.Equal(MqttTransport.Tcp,       report.Attempts[0].Transport);
        Assert.Equal(MqttTransport.WebSocket, report.Attempts[1].Transport);

        string sentence = MqttConnectionProbe.Describe(report);
        Assert.Contains("Neither transport reached the broker", sentence);
        Assert.Contains("TCP", sentence);
        Assert.Contains("WebSocket", sentence);
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
