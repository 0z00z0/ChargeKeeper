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
    // Guards the compare-and-set of _lastStateJson: PublishState (battery + command-worker threads) and
    // OnConnectedAsync (maintain-loop thread) both read+write it, so an unsynchronised check-then-set
    // could drop a real change (a stale write making the next change wrongly deduped) or double-publish.
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Set by OnPowerResume; honoured on the maintain-loop thread so the resume-forced socket drop +
    // reconnect happens THERE, never racing the loop's own ConnectAsync/OnConnectedAsync (a separate
    // fire-and-forget DisconnectAsync used to race it). Volatile: cross-thread flag.
    private volatile bool _reconnectRequested;

    // Coalesces a burst of charge-control StateChanged signals into at most one in-flight fresh EC read
    // plus one trailing read, so slider drags / a threshold-set-while-override (which fires BOTH events)
    // don't queue N sequential blocking vendor reads when only the last matters.
    private readonly CoalescingGate _reflectGate = new();

    // Wakes the maintain loop out of its inter-poll delay early — signalled on a detected disconnect
    // (OnClientDisconnectedAsync) or a resume-from-standby (OnPowerResume), so a reconnect + "online"
    // republish happens within moments instead of after the full poll/backoff (issue #41). Volatile:
    // swapped for a fresh instance each time it's consumed.
    private volatile TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Connection-loop timing (issue #41). A generous keep-alive keeps an idle link up; drop detection
    // is now event-driven (DisconnectedAsync → Wake), so the connected re-poll can be long — it's only
    // a stability re-check now, not the primary drop detector, so a battery device isn't woken every
    // 10 s for nothing.
    private static readonly TimeSpan KeepAlive      = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectedPoll  = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(3);
    private const double MaxBackoffSeconds = 60;
    // A connection that lived at least this long is treated as a genuine session, so its drop reconnects
    // fast (backoff reset). A connection that dropped sooner is a flap (broker accepts the CONNECT then
    // drops almost immediately) → keep escalating the backoff so we can't tight-spin reconnecting it.
    private static readonly TimeSpan StableConnection = TimeSpan.FromSeconds(30);
    // Debounce before the post-command fresh read: lets a burst of near-simultaneous StateChanged
    // signals collapse into one read AND lets the in-progress device write land first, so we don't
    // publish an interim state (e.g. a stale "Smart Charge off" seen between a deactivate signal and
    // the threshold write completing).
    private static readonly TimeSpan ReflectDebounce = TimeSpan.FromMilliseconds(250);

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
        // Give the live actions the app's already-maintained cached thresholds (from the same
        // CurrentStateProvider snapshot the normal publish path trusts) so a single-bound
        // charge_start/charge_stop set reads its companion value from cache instead of a dedicated
        // pre-write EC RPC (the guaranteed-fresh read still happens AFTER the write). Late-bound: the
        // delegate reads CurrentStateProvider at call time, since it's assigned after construction.
        _actions = actions ?? new ChargeControlActions(CachedThresholds);
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
        DateTime? connectedSince = null;   // when the current live session started; null while disconnected

        while (!ct.IsCancellationRequested && _enabled)
        {
            // A resume-from-standby forces a reconnect: the NIC was suspended so the socket is often
            // half-dead while IsConnected still reads true. Drop it HERE (on the loop thread) so it
            // can't race the loop's own ConnectAsync. A resume is not a flap — reset the backoff.
            if (_reconnectRequested)
            {
                _reconnectRequested = false;
                try { if (_client.IsConnected) await _client.DisconnectAsync().ConfigureAwait(false); } catch { }
                connectedSince = null;
                backoff = InitialBackoff;
            }

            try
            {
                if (!_client.IsConnected && _options is { } opt)
                {
                    // If we just lost a *brief* session, that's a flap (broker accepts then instantly
                    // drops); escalate and wait out the backoff BEFORE retrying so we can't tight-spin.
                    // A genuine drop of a session that lasted (or a first attempt) reconnects at once.
                    if (connectedSince is { } since && DateTime.UtcNow - since < StableConnection)
                    {
                        backoff = NextBackoff(backoff);
                        connectedSince = null;
                        if (!await DelayOrWake(backoff, ct).ConfigureAwait(false)) break;
                    }
                    connectedSince = null;

                    await _client.ConnectAsync(opt, ct).ConfigureAwait(false);
                    await OnConnectedAsync(ct).ConfigureAwait(false);   // republishes online + fresh state
                    connectedSince = DateTime.UtcNow;
                }
                else if (_client.IsConnected && connectedSince is { } s && DateTime.UtcNow - s >= StableConnection)
                {
                    // Session has proven stable → clear the backoff so the NEXT genuine drop is fast.
                    backoff = InitialBackoff;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLog.Error("HomeAssistantService.Connect", Sanitize(ex));   // message only, never creds
                backoff = NextBackoff(backoff);
                connectedSince = null;
            }
            // Re-poll occasionally while healthy (long — drops are event-driven now); back off while
            // failing. A genuine live-connection drop or a resume cuts the wait short via Wake() (#41).
            if (!await DelayOrWake(_client.IsConnected ? ConnectedPoll : backoff, ct).ConfigureAwait(false))
                break;
        }
    }

    /// <summary>Exponential backoff step, capped — pure so the cap/growth is unit-tested.</summary>
    internal static TimeSpan NextBackoff(TimeSpan current) =>
        TimeSpan.FromSeconds(Math.Min(current.TotalSeconds * 2, MaxBackoffSeconds));

    /// <summary>
    /// Whether a DisconnectedAsync event should wake the maintain loop for an early reconnect. Only a
    /// drop of a genuinely-LIVE connection should (ClientWasConnected). MQTTnet also fires this event
    /// with ClientWasConnected=false when ConnectAsync itself fails — waking on THAT short-circuits the
    /// exponential backoff into near-continuous reconnect hammering. Pure so it's unit-tested.
    /// </summary>
    internal static bool ShouldWakeOnDisconnect(bool enabled, bool clientWasConnected) =>
        enabled && clientWasConnected;

    /// <summary>Whether a session that lived <paramref name="lifetime"/> counts as stable (vs a flap).</summary>
    internal static bool IsStableConnection(TimeSpan lifetime) => lifetime >= StableConnection;

    /// <summary>
    /// Waits up to <paramref name="delay"/>, returning early when <see cref="Wake"/> is signalled.
    /// Returns false only when the service is cancelled (the caller then breaks the maintain loop).
    /// </summary>
    private async Task<bool> DelayOrWake(TimeSpan delay, CancellationToken ct)
    {
        var wake = _wake.Task;
        // Linked CTS so that when Wake() wins, we CANCEL the losing Task.Delay instead of abandoning it
        // (an uncancelled timer would otherwise linger up to a full poll/backoff — up to 60 s).
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var delayTask = Task.Delay(delay, delayCts.Token);
            var winner = await Task.WhenAny(delayTask, wake).ConfigureAwait(false);
            if (winner == wake)
            {
                delayCts.Cancel();
                try { await delayTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                // Consume this wake and re-arm for the next one. A signal that races this swap at
                // worst costs one poll interval before the next reconnect attempt — benign.
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
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
        if (ShouldWakeOnDisconnect(_enabled, e.ClientWasConnected)) Wake();
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
        // Signal the maintain loop to force a reconnect and wake it — it performs the socket drop +
        // reconnect on its OWN thread (see the top of MaintainConnectionAsync), so this can't race the
        // loop's in-flight ConnectAsync/OnConnectedAsync the way a direct DisconnectAsync here would.
        _reconnectRequested = true;
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
            lock (_stateLock) { _lastStateJson = json; }   // set-with-lock; publish unconditionally on connect
            await PublishAsync(_stateTopic, json, retain: true, ct).ConfigureAwait(false);
        }
        else
        {
            string? last;
            lock (_stateLock) { last = _lastStateJson; }
            if (last is not null)
                await PublishAsync(_stateTopic, last, retain: true, ct).ConfigureAwait(false);
        }
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
        // Atomic compare-and-set: two threads (battery tick + command worker) mustn't both pass the
        // "unchanged?" check and race the write, nor let a stale write dedupe the next real change.
        lock (_stateLock)
        {
            if (string.Equals(json, _lastStateJson, StringComparison.Ordinal)) return;  // unchanged → don't republish
            _lastStateJson = json;
        }
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
        // Coalesce: only the first signal of a burst starts the reflect loop; the rest just arm a
        // trailing pass. This collapses a slider drag / a threshold-set-while-override (which fires
        // BOTH ChargeControlService AND TravelOverrideService) into at most one in-flight fresh read
        // plus one trailing read, instead of one blocking vendor read per signal.
        if (_reflectGate.Signal())
            _ = ReflectLoopAsync();
    }

    /// <summary>
    /// Debounced, coalescing driver for the post-command fresh-state republish. Runs one pass per
    /// coalesced burst: a short debounce (so a burst collapses AND the in-progress device write lands
    /// before we read — avoiding an interim stale publish), then a single fresh read+publish. If more
    /// signals arrived during the pass, runs once more; otherwise ends. The blocking vendor read runs
    /// here on a background continuation, never on the StateChanged caller (tray/command-worker) thread.
    /// </summary>
    private async Task ReflectLoopAsync()
    {
        do
        {
            _reflectGate.BeginPass();
            try
            {
                await Task.Delay(ReflectDebounce).ConfigureAwait(false);
                if (_enabled)
                    PublishFreshStateAfterCommand();
            }
            catch (Exception ex) { AppLog.Error("HomeAssistantService.Reflect", Sanitize(ex)); }
        }
        while (_reflectGate.ShouldRepeat());
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

    /// <summary>
    /// The app's already-maintained cached Smart Charge thresholds, taken from the same
    /// <see cref="CurrentStateProvider"/> snapshot the normal publish path trusts. Supplied to the
    /// live <see cref="ChargeControlActions"/> so a single-bound charge_start/charge_stop set reads its
    /// companion value from cache — not a dedicated pre-write EC RPC. Returns null when Smart Charge is
    /// off/unset or no reading exists yet, so the dispatcher falls back to a sensible default pair.
    /// </summary>
    private (int Start, int Stop)? CachedThresholds() =>
        CurrentStateProvider?.Invoke() is { SmartChargeEnabled: true, ChargeStart: int start, ChargeStop: int stop }
            ? (start, stop)
            : null;

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

/// <summary>
/// Collapses a burst of signals into at most one in-flight run plus one trailing run. Lock-based and
/// side-effect-free (it only tracks the running/pending flags) so the coalescing decision is
/// unit-tested without threads. Used by <see cref="HomeAssistantService"/> to coalesce charge-control
/// StateChanged signals into a bounded number of blocking vendor reads.
/// <list type="bullet">
/// <item><see cref="Signal"/> — records a signal; returns true only to the caller that must START the
///   loop (a signal while a loop already runs returns false but arms a trailing pass).</item>
/// <item><see cref="BeginPass"/> — claims the pending work at the top of each pass.</item>
/// <item><see cref="ShouldRepeat"/> — true to run another pass (a signal arrived during the last one),
///   otherwise clears the running flag and returns false to end the loop.</item>
/// </list>
/// </summary>
internal sealed class CoalescingGate
{
    private readonly object _lock = new();
    private bool _running;
    private bool _pending;

    public bool Signal()
    {
        lock (_lock)
        {
            _pending = true;
            if (_running) return false;
            _running = true;
            return true;
        }
    }

    public void BeginPass()
    {
        lock (_lock) { _pending = false; }
    }

    public bool ShouldRepeat()
    {
        lock (_lock)
        {
            if (_pending) return true;
            _running = false;
            return false;
        }
    }
}
