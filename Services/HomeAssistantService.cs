using MQTTnet;
using MQTTnet.Protocol;

namespace ChargeKeeper.Services;

/// <summary>
/// Live MQTT publisher for Home Assistant (TODO #28). Owns the broker connection and drives the pure
/// <see cref="HaDiscovery"/> contract onto it: on connect it publishes the retained discovery configs
/// + "online" availability (so HA auto-creates the ChargeKeeper device), then an entity state payload
/// whenever <see cref="PublishState"/> is called; a Last-Will-and-Testament flips availability to
/// "offline" if the process dies. Reconnects with backoff while enabled. Turning the feature off
/// clears the retained discovery so HA drops the device; a normal app exit keeps it (just goes
/// offline). NEVER logs the broker password.
/// </summary>
internal sealed class HomeAssistantService : IDisposable
{
    private readonly string _swVersion;
    private readonly IMqttClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile bool _enabled;
    private MqttClientOptions? _options;
    private string _nodeId = "", _stateTopic = "", _availTopic = "", _discoveryPrefix = "homeassistant", _deviceName = "";
    private string? _lastStateJson;   // republished on (re)connect so a fresh HA restart gets current values
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// Supplies the current live state to publish immediately on every (re)connect, so a connect
    /// that happens before the first battery tick still shows real values (previously only
    /// <see cref="_lastStateJson"/> was republished, and that's empty until <see cref="PublishState"/>
    /// first runs). Returns null before the first battery reading; falls back to
    /// <see cref="_lastStateJson"/> when null or unset.
    /// </summary>
    public Func<HaState?>? CurrentStateProvider { get; set; }

    public HomeAssistantService(string swVersion)
    {
        _swVersion = swVersion;
        _client = new MqttClientFactory().CreateMqttClient();
    }

    /// <summary>
    /// (Re)configures from settings. Starts publishing when enabled AND a host is set; stops (and
    /// clears retained discovery) otherwise. Safe to call repeatedly — on startup and on every
    /// settings change (the tray toggle) — it reconciles to the desired state.
    /// </summary>
    public void ApplySettings(AppSettings s)
    {
        bool shouldRun = s.HomeAssistantEnabled && !string.IsNullOrWhiteSpace(s.MqttBrokerHost);
        _ = ApplyAsync(s, shouldRun);
    }

    private async Task ApplyAsync(AppSettings s, bool shouldRun)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!shouldRun)
            {
                await StopInternalAsync(clearDiscovery: true).ConfigureAwait(false);
                return;
            }

            string machine  = Environment.MachineName;
            _nodeId          = HaDiscovery.NodeId(machine);
            _stateTopic      = HaDiscovery.StateTopic(_nodeId);
            _availTopic      = HaDiscovery.AvailabilityTopic(_nodeId);
            _discoveryPrefix = string.IsNullOrWhiteSpace(s.MqttDiscoveryPrefix) ? "homeassistant" : s.MqttDiscoveryPrefix.Trim();
            _deviceName      = $"ChargeKeeper ({machine})";

            var ob = new MqttClientOptionsBuilder()
                .WithTcpServer(s.MqttBrokerHost.Trim(), s.MqttBrokerPort)
                .WithClientId(_nodeId)
                .WithCleanSession()
                .WithWillTopic(_availTopic)
                .WithWillPayload(HaDiscovery.Offline)
                .WithWillRetain()
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
            if (!string.IsNullOrEmpty(s.MqttUsername))
                ob = ob.WithCredentials(s.MqttUsername, s.MqttPassword);
            if (s.MqttUseTls)
                ob = ob.WithTlsOptions(o => { });
            _options = ob.Build();

            bool wasRunning = _enabled;
            _enabled = true;
            if (_loop is null || _loop.IsCompleted)
            {
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => MaintainConnectionAsync(_cts.Token));
            }
            else if (wasRunning)
            {
                // Options changed while already running (host/creds edit) — bounce the socket so the
                // maintain loop reconnects with the new options.
                try { await _client.DisconnectAsync().ConfigureAwait(false); } catch { /* loop retries */ }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task MaintainConnectionAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested && _enabled)
        {
            try
            {
                if (!_client.IsConnected && _options is { } opt)
                {
                    await _client.ConnectAsync(opt, ct).ConfigureAwait(false);
                    await OnConnectedAsync(ct).ConfigureAwait(false);
                    backoff = TimeSpan.FromSeconds(3);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLog.Error("HomeAssistantService.Connect", Sanitize(ex));   // message only, never creds
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
            try { await Task.Delay(_client.IsConnected ? TimeSpan.FromSeconds(15) : backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task OnConnectedAsync(CancellationToken ct)
    {
        AppLog.Info($"HomeAssistant: connected; publishing discovery for '{_nodeId}'.");
        foreach (var (topic, json) in HaDiscovery.DiscoveryConfigs(_nodeId, _discoveryPrefix, _deviceName, _swVersion))
            await PublishAsync(topic, json, retain: true, ct).ConfigureAwait(false);
        await PublishAsync(_availTopic, HaDiscovery.Online, retain: true, ct).ConfigureAwait(false);
        // Publish a FRESH current state right away so a connect before any battery tick still shows
        // live values. Fall back to the last cached snapshot when no provider is set / it has no
        // reading yet (both null on a very early first connect → nothing published, as before).
        if (CurrentStateProvider?.Invoke() is { } current)
        {
            string json = HaDiscovery.StatePayload(current);
            _lastStateJson = json;
            await PublishAsync(_stateTopic, json, retain: true, ct).ConfigureAwait(false);
        }
        else if (_lastStateJson is { } last)
            await PublishAsync(_stateTopic, last, retain: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes an entity state snapshot (retained, so HA has a value immediately on restart).
    /// Cheap no-op when disabled or disconnected — the snapshot is cached and re-sent on next connect.
    /// Call from the battery-report path.
    /// </summary>
    public void PublishState(HaState state)
    {
        string json = HaDiscovery.StatePayload(state);
        _lastStateJson = json;
        if (_enabled && _client.IsConnected)
            _ = PublishAsync(_stateTopic, json, retain: true, CancellationToken.None);
    }

    private async Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await _client.PublishAsync(msg, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { AppLog.Error("HomeAssistantService.Publish", Sanitize(ex)); }
    }

    private async Task StopInternalAsync(bool clearDiscovery)
    {
        _enabled = false;
        _cts?.Cancel();
        try
        {
            if (_client.IsConnected)
            {
                if (clearDiscovery)
                    // User disabled the feature — remove the retained discovery configs so HA drops
                    // the device entirely (a normal exit skips this and just goes offline, keeping it).
                    foreach (var (component, objectId) in HaDiscovery.Entities)
                        await PublishAsync(HaDiscovery.ConfigTopic(_discoveryPrefix, component, _nodeId, objectId),
                                           "", retain: true, CancellationToken.None).ConfigureAwait(false);

                await PublishAsync(_availTopic, HaDiscovery.Offline, retain: true, CancellationToken.None).ConfigureAwait(false);
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
        }
        catch { /* best-effort teardown */ }
    }

    // Guarantees a thrown broker error can never carry the password into the log — we log only the
    // exception type + message, both broker-generated, and drop the stack/inner chain.
    private static Exception Sanitize(Exception ex) => new($"{ex.GetType().Name}: {ex.Message}");

    public void Dispose()
    {
        // Graceful exit: go offline but KEEP the retained discovery so the device persists in HA.
        try { StopInternalAsync(clearDiscovery: false).Wait(TimeSpan.FromSeconds(3)); } catch { }
        _client.Dispose();
        _cts?.Dispose();
        _gate.Dispose();
    }
}
