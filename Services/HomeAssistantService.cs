using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Protocol;

namespace ChargeKeeper.Services;

/// <summary>
/// Live MQTT publisher for Home Assistant (TODO #28). Owns the broker connection and drives the pure
/// <see cref="HaDiscovery"/> contract onto it: on connect it publishes the retained discovery configs
/// + "online" availability (so HA auto-creates the ChargeKeeper device), then an entity state payload
/// whenever <see cref="PublishState"/> is called; a Last-Will-and-Testament flips availability to
/// "offline" if the process dies. It also SUBSCRIBES to the charge-control command topics (issue #30)
/// and routes each inbound message through <see cref="HaCommand.TryParse"/> +
/// <see cref="HaCommandDispatcher"/> to the app's charge-control services. Reconnects with backoff
/// while enabled. Turning the feature off clears the retained discovery so HA drops the device; a
/// normal app exit keeps it (just goes offline). NEVER logs the broker password or any payload.
/// </summary>
internal sealed class HomeAssistantService : IDisposable
{
    private readonly string _swVersion;
    private readonly IMqttClient _client;
    private readonly IChargeControlActions _actions;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Inbound commands are handed to this single-reader queue and processed one-at-a-time on a
    // dedicated worker, OFF the MQTT receive callback (issue #30 review): the callback must return
    // promptly (never run the blocking vendor RPC inline), and a read-modify-write pair for one
    // command must finish before the next starts (else two near-simultaneous threshold sets each read
    // the old pair and clobber each other). A single reader gives both properties for free.
    private readonly Channel<HaCommand> _commands =
        Channel.CreateUnbounded<HaCommand>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _commandWorker;

    private volatile bool _enabled;
    private MqttClientOptions? _options;
    private string _nodeId = "", _stateTopic = "", _availTopic = "", _discoveryPrefix = "homeassistant", _deviceName = "";
    private string? _lastStateJson;   // republished on (re)connect so a fresh HA restart gets current values
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Wakes the maintain loop out of its inter-poll delay early — signalled on a detected disconnect
    // (OnClientDisconnectedAsync) or a resume-from-standby (OnPowerResume), so a reconnect + "online"
    // republish happens within moments instead of after the full poll/backoff (issue #41). Volatile:
    // swapped for a fresh instance each time it's consumed.
    private volatile TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Connection-loop timing (issue #41). A generous keep-alive keeps an idle link up; a short-ish
    // connected re-poll plus the wake signal shrink the reconnect window without busy-spinning.
    private static readonly TimeSpan KeepAlive      = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectedPoll  = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(3);
    private const double MaxBackoffSeconds = 60;

    /// <summary>
    /// Supplies the current live state to publish immediately on every (re)connect, so a connect
    /// that happens before the first battery tick still shows real values (previously only
    /// <see cref="_lastStateJson"/> was republished, and that's empty until <see cref="PublishState"/>
    /// first runs). Returns null before the first battery reading; falls back to
    /// <see cref="_lastStateJson"/> when null or unset.
    /// </summary>
    public Func<HaState?>? CurrentStateProvider { get; set; }

    public HomeAssistantService(string swVersion, IChargeControlActions? actions = null)
    {
        _swVersion = swVersion;
        _actions = actions ?? new ChargeControlActions();
        _client = new MqttClientFactory().CreateMqttClient();
        // Route inbound charge-control commands (issue #30). Registered once for the client's life;
        // the handler is a no-op for anything that isn't a recognised command topic. It only enqueues;
        // the actual dispatch runs on the single-worker loop below.
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        // Reconnect + republish "online" the instant a drop is detected, not after the poll interval
        // (issue #41).
        _client.DisconnectedAsync += OnClientDisconnectedAsync;
        _commandWorker = Task.Run(ProcessCommandsAsync);

        // Reflect a charge-control change (tray OR MQTT command) to HA the moment it settles, so the
        // smart_charge switch + charge_start/stop numbers + preset select show what actually took
        // effect (issue #40 item 3) instead of waiting for the next battery tick. Both events funnel
        // to the same handler: ChargeControlService covers the composed threshold/smart-charge/preset
        // ops; TravelOverrideService covers the async "charge to 100 % once" activate/revert (whose
        // restore completes on a background task) so the published state is the settled truth.
        // Static events — unsubscribed in Dispose so a disposed instance doesn't keep publishing.
        ChargeControlService.StateChanged  += OnChargeControlChanged;
        TravelOverrideService.StateChanged += OnChargeControlChanged;
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
                // Explicit generous keep-alive (issue #41): MQTTnet auto-sends a PINGREQ within this
                // period whenever the link is otherwise idle (no battery change to publish), so the
                // broker won't drop a quiet connection — and a link that died silently (e.g. the NIC
                // suspended in modern standby) is detected within ~1 keep-alive and surfaces as a
                // DisconnectedAsync we react to, rather than lingering "connected" while HA shows the
                // Last-Will "offline".
                .WithKeepAlivePeriod(KeepAlive)
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
        var backoff = InitialBackoff;
        while (!ct.IsCancellationRequested && _enabled)
        {
            try
            {
                if (!_client.IsConnected && _options is { } opt)
                {
                    await _client.ConnectAsync(opt, ct).ConfigureAwait(false);
                    await OnConnectedAsync(ct).ConfigureAwait(false);   // republishes online + fresh state
                    backoff = InitialBackoff;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLog.Error("HomeAssistantService.Connect", Sanitize(ex));   // message only, never creds
                backoff = NextBackoff(backoff);
            }
            // Re-check periodically while healthy; back off while failing. Either wait is cut short by
            // Wake() (a detected disconnect or a resume-from-standby) so we reconnect promptly (#41).
            if (!await DelayOrWake(_client.IsConnected ? ConnectedPoll : backoff, ct).ConfigureAwait(false))
                break;
        }
    }

    /// <summary>Exponential backoff step, capped — pure so the cap/growth is unit-tested.</summary>
    internal static TimeSpan NextBackoff(TimeSpan current) =>
        TimeSpan.FromSeconds(Math.Min(current.TotalSeconds * 2, MaxBackoffSeconds));

    /// <summary>
    /// Waits up to <paramref name="delay"/>, returning early when <see cref="Wake"/> is signalled.
    /// Returns false only when the service is cancelled (the caller then breaks the maintain loop).
    /// </summary>
    private async Task<bool> DelayOrWake(TimeSpan delay, CancellationToken ct)
    {
        var wake = _wake.Task;
        try
        {
            var winner = await Task.WhenAny(Task.Delay(delay, ct), wake).ConfigureAwait(false);
            if (winner == wake)
                // Consume this wake and re-arm for the next one. A signal that races this swap at
                // worst costs one poll interval before the next reconnect attempt — benign.
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            else
                await winner.ConfigureAwait(false);   // observe cancellation raised by the delay
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException) { return false; }
    }

    private void Wake() => _wake.TrySetResult();

    /// <summary>
    /// MQTTnet fired a disconnect (broker close, or a keep-alive ping that failed after the NIC
    /// suspended in modern standby). Wake the maintain loop so it reconnects + republishes "online"
    /// immediately, shrinking the window where HA shows the Last-Will "offline" while the PC is
    /// actually alive (issue #41).
    /// </summary>
    private Task OnClientDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (_enabled) Wake();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Call on resume-from-standby (issue #41). Modern standby suspends the NIC, so the broker's
    /// Last-Will flips the device "offline" and the TCP socket dies silently; this forces an immediate
    /// reconnect + "online" + fresh-state republish so the sensors don't linger "Unavailable" after
    /// the machine wakes. No-op when the feature is disabled.
    /// <para>
    /// MUST be wired from the app's PowerModeChanged/Resume handler — this class deliberately does not
    /// subscribe to <c>SystemEvents</c> itself (it would then own an unsubscribe lifetime that belongs
    /// to App). See the note in the PR: add <c>_ha?.OnPowerResume();</c> to App.OnPowerModeChanged.
    /// </para>
    /// </summary>
    public void OnPowerResume()
    {
        if (!_enabled) return;
        _ = ForceReconnectAsync();
    }

    private async Task ForceReconnectAsync()
    {
        // Drop any half-dead socket so the maintain loop's IsConnected check reconnects; then wake it
        // so it doesn't wait out the poll. DisconnectAsync on an already-dead socket is a fast no-op.
        try { if (_client.IsConnected) await _client.DisconnectAsync().ConfigureAwait(false); } catch { }
        Wake();
    }

    private async Task OnConnectedAsync(CancellationToken ct)
    {
        AppLog.Info($"HomeAssistant: connected; publishing discovery for '{_nodeId}'.");
        // Preset names populate the "Charge preset" select's options (issue #30). Read fresh on each
        // connect so a reconnect picks up any preset edits made while offline.
        var presetNames = SettingsService.Current.Presets.Select(p => p.Name).ToList();
        foreach (var (topic, json) in HaDiscovery.DiscoveryConfigs(_nodeId, _discoveryPrefix, _deviceName, _swVersion, presetNames))
            await PublishAsync(topic, json, retain: true, ct).ConfigureAwait(false);
        // Evict any retained discovery from the OLD (pre-#29) entity ids so an upgrading user doesn't
        // keep ghost entities alongside the renamed ones.
        await ClearLegacyDiscoveryAsync(ct).ConfigureAwait(false);
        await PublishAsync(_availTopic, HaDiscovery.Online, retain: true, ct).ConfigureAwait(false);

        // Subscribe to the single command wildcard so HA can drive Smart Charge / thresholds / preset
        // (issue #30). One filter covers every command entity; the handler routes by object-id.
        await _client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(HaDiscovery.CommandTopicFilter(_nodeId))
                                       .WithAtLeastOnceQoS())
                .Build(),
            ct).ConfigureAwait(false);
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
    /// No-op when the feature is disabled (fix: return BEFORE building/serializing the payload, so a
    /// battery tick costs nothing while off). When enabled but the payload is unchanged from the last
    /// one, skips the network publish too — a stationary SoC shouldn't re-send a retained message
    /// every tick. Still caches while disconnected so the snapshot is re-sent on the next connect.
    /// Call from the battery-report path.
    /// </summary>
    public void PublishState(HaState state)
    {
        if (!_enabled) return;
        string json = HaDiscovery.StatePayload(state);
        if (string.Equals(json, _lastStateJson, StringComparison.Ordinal)) return;  // unchanged → don't republish
        _lastStateJson = json;
        if (_client.IsConnected)
            _ = PublishAsync(_stateTopic, json, retain: true, CancellationToken.None);
    }

    /// <summary>
    /// Inbound MQTT handler for the charge-control command topics (issue #30). Runs on the MQTT
    /// receive callback, so it does only cheap work: skip retained command payloads (a command is an
    /// event, not state — a leftover retained payload would otherwise re-fire on every reconnect),
    /// parse defensively via <see cref="HaCommand.TryParse"/> (ignoring anything malformed/off-topic),
    /// then hand the parsed command to the single-worker queue and return. The blocking dispatch +
    /// fresh-state republish happen on <see cref="ProcessCommandsAsync"/>, never here. Never throws
    /// (the MQTT loop must survive a bad payload) and never logs the payload.
    /// </summary>
    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            string topic = e.ApplicationMessage.Topic;
            if (HaDiscovery.CommandObjectId(_nodeId, topic) is not { } objectId) return Task.CompletedTask;

            // Ignore retained messages on command topics. With CleanSession + resubscribe-on-connect,
            // a retained cmd/* payload (e.g. a stale PRESS or threshold) is redelivered and would
            // re-fire on every reconnect. Commands are events; only state topics carry retained value.
            if (e.ApplicationMessage.Retain)
            {
                AppLog.Info($"HomeAssistant: ignored retained command '{objectId}'.");
                return Task.CompletedTask;
            }

            string payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";
            if (!HaCommand.TryParse(objectId, payload, out var cmd))
            {
                AppLog.Info($"HomeAssistant: ignored command '{objectId}' (unrecognised/invalid payload).");
                return Task.CompletedTask;
            }

            AppLog.Info($"HomeAssistant: command '{objectId}' → {cmd.Kind}; queued.");
            _commands.Writer.TryWrite(cmd);   // unbounded + non-blocking; the worker drains it in order
        }
        catch (Exception ex) { AppLog.Error("HomeAssistantService.OnMessage", Sanitize(ex)); }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The single-worker command loop (issue #30 review). Drains <see cref="_commands"/> one command
    /// at a time — so a blocking read-modify-write for one command completes before the next starts —
    /// dispatching each to the (now synchronous) <see cref="IChargeControlActions"/> and then
    /// publishing a FRESH state snapshot so HA reflects the change immediately. Runs for the object's
    /// lifetime; ends when <see cref="Dispose"/> completes the channel writer.
    /// </summary>
    private async Task ProcessCommandsAsync()
    {
        await foreach (var cmd in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                // The dispatch drives the shared ChargeControlService (or TravelOverrideService for
                // "charge to 100 % once"), whose StateChanged event we subscribe to — so the FRESH
                // reflect happens via OnChargeControlChanged, covering the async override
                // activate/revert timing too. No separate publish call is needed here.
                HaCommandDispatcher.Dispatch(cmd, _actions);   // synchronous read-modify-write on this worker
            }
            catch (Exception ex) { AppLog.Error("HomeAssistantService.Command", Sanitize(ex)); }
        }
    }

    /// <summary>
    /// Reflect a settled charge-control change to HA (issue #40 item 3). Subscribed to
    /// <see cref="ChargeControlService.StateChanged"/> AND
    /// <see cref="TravelOverrideService.StateChanged"/>, so a change from ANY source — tray toggle,
    /// inbound MQTT command, network-profile auto-apply, or the override auto-revert at full charge —
    /// republishes the genuinely-current Smart Charge / thresholds / preset. No-op while disabled.
    /// </summary>
    private void OnChargeControlChanged()
    {
        if (!_enabled) return;
        PublishFreshStateAfterCommand();
    }

    /// <summary>
    /// Publishes state right after a command's write completes, reflecting the change with a FRESH
    /// device read rather than App's stale cached threshold state (which the command path never
    /// refreshes). Takes the battery fields from <see cref="CurrentStateProvider"/> but overrides the
    /// charge-control fields (Smart Charge / thresholds / preset) from a live
    /// <see cref="ChargeThresholdService.Read"/> + the persisted active preset.
    /// </summary>
    private void PublishFreshStateAfterCommand()
    {
        if (CurrentStateProvider?.Invoke() is not { } baseState) return;
        var fresh = ChargeThresholdService.Read();
        string? activePreset = SettingsService.Current.ActivePreset;
        PublishState(HaStateBuilder.ApplyChargeControl(baseState, fresh, activePreset));
    }

    /// <summary>Publishes empty retained payloads to the OLD (pre-#29) discovery config topics to evict ghosts.</summary>
    private async Task ClearLegacyDiscoveryAsync(CancellationToken ct)
    {
        foreach (var (component, objectId) in HaDiscovery.LegacyEntities)
            await PublishAsync(HaDiscovery.ConfigTopic(_discoveryPrefix, component, _nodeId, objectId),
                               "", retain: true, ct).ConfigureAwait(false);
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
                {
                    // User disabled the feature — remove the retained discovery configs so HA drops
                    // the device entirely (a normal exit skips this and just goes offline, keeping it).
                    // Clear the OLD (pre-#29) ids too so an upgraded-then-disabled user leaves no ghosts.
                    foreach (var (component, objectId) in HaDiscovery.Entities.Concat(HaDiscovery.LegacyEntities))
                        await PublishAsync(HaDiscovery.ConfigTopic(_discoveryPrefix, component, _nodeId, objectId),
                                           "", retain: true, CancellationToken.None).ConfigureAwait(false);
                }

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
        // Detach from the static charge-control events so a disposed instance stops publishing.
        ChargeControlService.StateChanged  -= OnChargeControlChanged;
        TravelOverrideService.StateChanged -= OnChargeControlChanged;
        // Stop the command worker: complete the channel so ProcessCommandsAsync drains and exits.
        try { _commands.Writer.TryComplete(); } catch { }
        // Graceful exit: go offline but KEEP the retained discovery so the device persists in HA.
        try { StopInternalAsync(clearDiscovery: false).Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _commandWorker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _client.Dispose();
        _cts?.Dispose();
        _gate.Dispose();
    }
}
