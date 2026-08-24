using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Protocol;

namespace ChargeKeeper.Services;

/// <summary>Live MQTT publisher for Home Assistant. Owns the broker connection, drives the pure
/// <see cref="HaDiscovery"/> contract onto it, and routes the inbound command topics to the app's
/// charge-control services. Never logs the broker password or any payload.</summary>
internal sealed class HomeAssistantService : IDisposable
{
    private readonly string _swVersion;
    private readonly IMqttClient _client;
    private readonly IChargeControlActions _actions;
    private readonly IHaSettingsActions _settingsActions;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Drained on a dedicated worker off the MQTT receive callback, which must return promptly. Single
    // reader: one command's read-modify-write must finish before the next starts.
    private readonly Channel<HaCommand> _commands =
        Channel.CreateUnbounded<HaCommand>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _commandWorker;

    private volatile bool _enabled;

    /// <summary>A snapshot of everything one connect round needs: the staged choices the plan reads,
    /// plus the credentials and identity every candidate shares. Taken on the Apply thread and never
    /// read back out of settings, so the maintain loop cannot pick up half of the next Apply.</summary>
    /// <remarks>Options are built per candidate rather than up front: port and transport together
    /// decide how the broker is addressed, and the sweep can hold eight of them.</remarks>
    private sealed record ConnectionOptions(
        MqttEndpointRequest Request, string Password, bool UseTls,
        string ClientId, string AvailabilityTopic)
    {
        public MqttClientOptions For(MqttEndpointCandidate candidate)
        {
            var ob = new MqttClientOptionsBuilder()
                .WithTransport(candidate.Transport, Request.Host, candidate.Port, UseTls)
                .WithClientId(ClientId)
                .WithCleanSession()
                // MQTTnet pings within this period on an idle link, so the broker won't drop a quiet
                // connection and a silently-dead one surfaces rather than lingering "connected".
                .WithKeepAlivePeriod(KeepAlive)
                // Pinned rather than left to the library default: this is what a dead candidate costs
                // before the next is tried, so it has to be a known number.
                .WithTimeout(ConnectTimeout)
                .WithWillTopic(AvailabilityTopic)
                .WithWillPayload(HaDiscovery.Offline)
                .WithWillRetain()
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
            if (!string.IsNullOrEmpty(Request.Username))
                ob = ob.WithCredentials(Request.Username, Password);
            return ob.Build();
        }
    }

    private ConnectionOptions? _options;

    // Where the broker answered last, so a machine that moves between the internal network and the
    // way in from outside pays the full sweep once per move rather than once per reconnect. Mirrored
    // into settings so it survives a restart. A single reference swap, because the maintain loop and
    // Apply both touch it and the four fields have to be read as one.
    private MqttEndpointMemory? _memory;

    private MqttEndpointMemory? Memory
    {
        get => Volatile.Read(ref _memory);
        set => Volatile.Write(ref _memory, value);
    }
    private string _nodeId = "", _stateTopic = "", _statusTopic = "", _availTopic = "", _discoveryPrefix = "homeassistant", _deviceName = "";
    private string? _lastStateJson;   // republished on (re)connect so a fresh HA restart gets current values
    private string? _lastSurfaceJson; // the settings payload's own dedupe, on its own topic
    // Guards the cached payloads, which the battery, command-worker and maintain-loop threads all touch.
    private readonly object _stateLock = new();
    // Which groups are announced. Written from ApplySettings, read on the MQTT threads; guarded by
    // _stateLock, because a record struct cannot be volatile and a torn read would announce a mix.
    private HaCategorySet _categories = HaCategorySet.All;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // A superseded node id and the prefix its retained topics were published under. Held rather than
    // evicted inline because the change can land while disconnected: best-effort now, guaranteed on
    // the next connect. Written under _gate, taken with Interlocked.Exchange.
    private sealed record StaleIdentity(string NodeId, string DiscoveryPrefix);
    private StaleIdentity? _staleIdentity;

    // Honoured on the maintain-loop thread, so the forced socket drop can't race its own ConnectAsync.
    private volatile bool _reconnectRequested;

    // Collapses a burst of charge-control signals into one in-flight fresh EC read plus one trailing
    // read, so a slider drag doesn't queue a blocking vendor read per signal.
    private readonly CoalescingGate _reflectGate = new();

    // The same coalescing for the settings snapshot, which several unrelated events can signal at once.
    private readonly CoalescingGate _surfaceGate = new();

    // Cuts the maintain loop's inter-poll delay short. Volatile: swapped for a fresh instance on use.
    private volatile TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly TimeSpan KeepAlive      = TimeSpan.FromSeconds(60);
    // Drop detection is event-driven (DisconnectedAsync → Wake), so this is only a stability re-check
    // and can be long — a battery device isn't woken every few seconds for nothing.
    private static readonly TimeSpan ConnectedPoll  = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(3);
    // What one unreachable transport costs before the next is tried. Same budget the page's
    // connection check gives each transport, so the two agree on how long "no answer" takes.
    private static readonly TimeSpan ConnectTimeout = MqttConnectionProbe.Timeout;
    private const double MaxBackoffSeconds = 60;
    // A session shorter than this is a flap, so its backoff keeps escalating instead of resetting.
    private static readonly TimeSpan StableConnection = TimeSpan.FromSeconds(30);
    // Lets the in-progress device write land before the post-command read, so no interim state is
    // published, and collapses a burst of signals into one read.
    private static readonly TimeSpan ReflectDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>Supplies the current live state to publish on every (re)connect, so a connect before
    /// the first battery tick still shows real values. Null until the first reading.</summary>
    public Func<HaState?>? CurrentStateProvider { get; set; }

    /// <summary>Supplies the settings/network/diagnostic snapshot. Separate from
    /// <see cref="CurrentStateProvider"/> because it changes on its own events, not on a battery tick.</summary>
    public Func<HaSurfaceState?>? CurrentSurfaceProvider { get; set; }

    /// <summary>Supplies the vendor capability gates the announcement is filtered through.</summary>
    public Func<HaCapabilities>? CapabilityProvider { get; set; }

    public HomeAssistantService(string swVersion, IChargeControlActions? actions = null,
                               IHaSettingsActions? settingsActions = null)
    {
        _swVersion = swVersion;
        _actions = actions ?? new ChargeControlActions(CurrentDeviceThresholds);
        if (settingsActions is null)
        {
            var live = new HaSettingsActions();
            // A settings write from the broker must reflect back at once; the battery tick that would
            // otherwise carry it can be minutes away.
            live.Changed += PublishSurfaceNow;
            _settingsActions = live;
        }
        else
            _settingsActions = settingsActions;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync += OnClientDisconnectedAsync;
        _commandWorker = Task.Run(ProcessCommandsAsync);

        // TravelOverrideService is needed alongside ChargeControlService because its activate/revert
        // completes on a background task. Static events — unsubscribed in Dispose.
        ChargeControlService.StateChanged  += OnChargeControlChanged;
        TravelOverrideService.StateChanged += OnChargeControlChanged;
    }

    /// <summary>Reconciles to the settings' desired state; safe to call repeatedly.</summary>
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

            string machine        = Environment.MachineName;
            string previousId     = _nodeId;
            string previousPrefix = _discoveryPrefix;
            _nodeId          = HaDiscovery.EffectiveNodeId(s.MqttNodeId, machine);
            _stateTopic      = HaDiscovery.StateTopic(_nodeId);
            _statusTopic     = HaDiscovery.StatusTopic(_nodeId);
            _availTopic      = HaDiscovery.AvailabilityTopic(_nodeId);
            _discoveryPrefix = string.IsNullOrWhiteSpace(s.MqttDiscoveryPrefix) ? "homeassistant" : s.MqttDiscoveryPrefix.Trim();
            _deviceName      = string.IsNullOrWhiteSpace(s.MqttDeviceName) ? $"ChargeKeeper ({machine})" : s.MqttDeviceName.Trim();
            lock (_stateLock) { _categories = s.MqttCategories; }

            // The id is the device identity end to end, so a change orphans every retained topic the
            // old id owned. Record it under the prefix it was published with, which may have changed
            // in this same call.
            if (previousId.Length > 0 && previousId != _nodeId)
            {
                AppLog.Info($"HomeAssistant: node id '{previousId}' → '{_nodeId}'; evicting the old device.");
                _staleIdentity = new(previousId, previousPrefix);
            }
            await ClearStaleIdentityAsync(CancellationToken.None).ConfigureAwait(false);

            // Snapshotted on this thread, so the maintain loop never reads the topic and identity
            // fields the next ApplySettings may already be rewriting.
            _options = new ConnectionOptions(
                new MqttEndpointRequest(s.MqttBrokerHost, s.MqttUsername, s.MqttBrokerPort, s.MqttTransportMode),
                s.MqttPassword, s.MqttUseTls, _nodeId, _availTopic);
            Memory = s.MqttLastGoodEndpoint;

            bool wasRunning = _enabled;
            _enabled = true;
            if (!wasRunning)
            {
                // A cancelled loop may still be unwinding; abandon it and start a fresh one — it exits
                // on its own token. IsCompleted lags Cancel, so it cannot gate the restart. Capture the
                // token in a local: a later ApplyAsync could swap the field before the lambda runs.
                var cts = new CancellationTokenSource();
                _cts?.Dispose();
                _cts = cts;
                _loop = Task.Run(() => MaintainConnectionAsync(cts.Token));
            }
            else
            {
                // Options changed while running — bounce the socket so the loop reconnects with them.
                try { await _client.DisconnectAsync().ConfigureAwait(false); } catch { /* loop retries */ }
            }
        }
        // ApplySettings discards this task, so an unhandled throw would silently disable the feature.
        catch (Exception ex) { AppLog.Error("HomeAssistantService.Apply", Sanitise(ex)); }
        finally { _gate.Release(); }
    }

    /// <summary>Connects over the first candidate <see cref="MqttTransportPlan"/> offers that works.
    /// False once the sweep is spent, which is the caller's cue to back off.</summary>
    /// <remarks>The remembered endpoint leads the sweep, so the usual reconnect is one attempt. When
    /// it has stopped working — the machine moved, or the broker was republished elsewhere — the
    /// sweep behind it finds the new one and the cache is rewritten, which is why a stale entry costs
    /// an attempt rather than the feature.</remarks>
    private async Task<bool> ConnectUsingPlanAsync(ConnectionOptions options, CancellationToken ct)
    {
        var attempts = new List<MqttEndpointAttempt>();
        while (MqttTransportPlan.NextEndpoint(options.Request, Memory, attempts) is { } candidate)
        {
            MqttProbeResult result;
            try
            {
                // MQTTnet 5 hands a refused CONNACK back as a result code rather than throwing, so
                // the code has to be read — otherwise a rejection looks like a live connection until
                // the first publish fails.
                var connack = await _client.ConnectAsync(options.For(candidate), ct).ConfigureAwait(false);
                result = MqttConnectionProbe.ClassifyConnack(
                    connack?.ResultCode ?? MqttClientConnectResultCode.UnspecifiedError, connack?.ReasonString);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result = MqttConnectionProbe.ClassifyConnectException(ex, ct); }

            if (result.Outcome == MqttProbeOutcome.Success)
            {
                RememberEndpoint(options.Request, candidate);
                return true;
            }

            // A half-open session from a refused CONNACK would make the next attempt look connected.
            try { if (_client.IsConnected) await _client.DisconnectAsync().ConfigureAwait(false); } catch { }
            attempts.Add(new(candidate, result));
        }

        // One line per failed round, naming every candidate tried — the log's whole account of why
        // nothing is publishing. The details are OS/broker text, never a staged credential.
        AppLog.Error("HomeAssistantService.Connect: no endpoint connected — " +
            string.Join("; ", attempts.Select(a =>
                $"{MqttConnectionProbe.Name(a.Candidate.Transport)}:{a.Candidate.Port}: {a.Result.Detail}")),
            null);
        return false;
    }

    /// <summary>Persists where the broker answered. State, not a setting: the user's choices are left
    /// exactly as they made them, and the sweep is what reads this back. The username belongs to the
    /// entry because a broker commonly fronts a separate listener per account; the password never
    /// does, and nothing here may hold one.</summary>
    private void RememberEndpoint(MqttEndpointRequest request, MqttEndpointCandidate candidate)
    {
        var found = new MqttEndpointMemory(
            (request.Host ?? "").Trim(), (request.Username ?? "").Trim(), candidate.Port, candidate.Transport);
        if (found == Memory) return;
        Memory = found;
        SettingsService.Update(s => s.MqttLastGoodEndpoint = found);
    }

    private async Task MaintainConnectionAsync(CancellationToken ct)
    {
        var backoff = InitialBackoff;
        DateTime? connectedSince = null;   // when the current live session started; null while disconnected

        while (!ct.IsCancellationRequested && _enabled)
        {
            // Modern standby suspends the NIC, so after a resume the socket is often half-dead while
            // IsConnected still reads true. A resume is not a flap — reset the backoff.
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
                    // A session that died young is a flap; wait out the escalating backoff before
                    // retrying. A drop of a session that lasted, or a first attempt, reconnects at once.
                    if (connectedSince is { } since && DateTime.UtcNow - since < StableConnection)
                    {
                        backoff = NextBackoff(backoff);
                        connectedSince = null;
                        if (!await DelayOrWake(backoff, ct).ConfigureAwait(false)) break;
                    }
                    connectedSince = null;

                    if (await ConnectUsingPlanAsync(opt, ct).ConfigureAwait(false))
                    {
                        await OnConnectedAsync(ct).ConfigureAwait(false);   // republishes online + fresh state
                        connectedSince = DateTime.UtcNow;
                    }
                    else
                        backoff = NextBackoff(backoff);   // every transport failed; wait longer before the next round
                }
                else if (_client.IsConnected && connectedSince is { } s && DateTime.UtcNow - s >= StableConnection)
                {
                    backoff = InitialBackoff;   // proven stable, so the next genuine drop reconnects fast
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLog.Error("HomeAssistantService.Connect", Sanitise(ex));   // message only, never creds
                // OnConnectedAsync can throw with the socket up, leaving connectedSince unset and
                // neither branch reachable next pass — a healthy-looking connection that never gets its
                // discovery, availability or subscription. Drop it so the next pass retries.
                try { if (_client.IsConnected) await _client.DisconnectAsync().ConfigureAwait(false); } catch { }
                backoff = NextBackoff(backoff);
                connectedSince = null;
            }
            // Long re-poll while healthy, backoff while failing; a drop or a resume cuts the wait short.
            if (!await DelayOrWake(_client.IsConnected ? ConnectedPoll : backoff, ct).ConfigureAwait(false))
                break;
        }
    }

    /// <summary>Exponential backoff step, capped.</summary>
    internal static TimeSpan NextBackoff(TimeSpan current) =>
        TimeSpan.FromSeconds(Math.Min(current.TotalSeconds * 2, MaxBackoffSeconds));

    /// <summary>Whether a DisconnectedAsync event should wake the loop for an early reconnect. MQTTnet
    /// also raises it with ClientWasConnected=false when ConnectAsync itself fails, and waking on that
    /// short-circuits the backoff into near-continuous reconnect hammering.</summary>
    internal static bool ShouldWakeOnDisconnect(bool enabled, bool clientWasConnected) =>
        enabled && clientWasConnected;

    /// <summary>Whether a session that lived <paramref name="lifetime"/> counts as stable, not a flap.</summary>
    internal static bool IsStableConnection(TimeSpan lifetime) => lifetime >= StableConnection;

    /// <summary>Waits up to <paramref name="delay"/>, early on <see cref="Wake"/>. False on cancel.</summary>
    private async Task<bool> DelayOrWake(TimeSpan delay, CancellationToken ct)
    {
        var wake = _wake.Task;
        // Linked CTS so a winning Wake() cancels the losing Task.Delay rather than abandoning its timer.
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var delayTask = Task.Delay(delay, delayCts.Token);
            var winner = await Task.WhenAny(delayTask, wake).ConfigureAwait(false);
            if (winner == wake)
            {
                delayCts.Cancel();
                try { await delayTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                // Re-arm. A signal racing the swap costs at worst one poll interval.
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
                await winner.ConfigureAwait(false);   // observe cancellation raised by the delay
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException) { return false; }
    }

    private void Wake() => _wake.TrySetResult();

    /// <summary>Wakes the maintain loop on a disconnect so it reconnects and republishes "online" at
    /// once, shrinking the window where HA shows the Last Will "offline" while the PC is alive.</summary>
    private Task OnClientDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (ShouldWakeOnDisconnect(_enabled, e.ClientWasConnected)) Wake();
        return Task.CompletedTask;
    }

    /// <summary>Forces a reconnect and fresh-state republish after resume-from-standby, so the sensors
    /// don't linger "Unavailable". Must be called from App's PowerModeChanged handler: this class does
    /// not subscribe to <c>SystemEvents</c> itself, because the unsubscribe lifetime belongs to App.</summary>
    public void OnPowerResume()
    {
        if (!_enabled) return;
        // The loop drops and reconnects on its own thread, so this can't race its in-flight ConnectAsync.
        _reconnectRequested = true;
        Wake();
    }

    private async Task OnConnectedAsync(CancellationToken ct)
    {
        AppLog.Info(Memory is { } found
            ? $"HomeAssistant: connected over {MqttConnectionProbe.Name(found.Transport)} on port {found.Port}; " +
              $"publishing discovery for '{_nodeId}'."
            : $"HomeAssistant: connected; publishing discovery for '{_nodeId}'.");
        // Evict a superseded node id first, so HA never sees both devices at once.
        await ClearStaleIdentityAsync(ct).ConfigureAwait(false);
        // Read under the settings lock: Presets is the live list, which the Settings window mutates in
        // place, and an unsynchronised enumeration throws and skips the whole connect sequence.
        await PublishDiscoveryAsync(ct).ConfigureAwait(false);
        await ClearLegacyDiscoveryAsync(ct).ConfigureAwait(false);
        await PublishAsync(_availTopic, HaDiscovery.Online, retain: true, ct).ConfigureAwait(false);

        // One wildcard covers every command entity; the handler routes by object-id.
        await _client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(HaDiscovery.CommandTopicFilter(_nodeId))
                                       .WithAtLeastOnceQoS())
                .Build(),
            ct).ConfigureAwait(false);
        // A fresh state if there is one, otherwise the cached snapshot; both null on a first connect.
        if (CurrentStateProvider?.Invoke() is { } current)
        {
            string json = HaDiscovery.StatePayload(current);
            lock (_stateLock) { _lastStateJson = json; }   // set, but publish unconditionally on connect
            await PublishAsync(_stateTopic, json, retain: true, ct).ConfigureAwait(false);
        }
        else
        {
            string? last;
            lock (_stateLock) { last = _lastStateJson; }
            if (last is not null)
                await PublishAsync(_stateTopic, last, retain: true, ct).ConfigureAwait(false);
        }

        // The settings payload has no "first tick" to wait for, so it is always publishable.
        if (CurrentSurfaceProvider?.Invoke() is { } surface)
        {
            string json = HaSurfacePayload.Build(surface);
            lock (_stateLock) { _lastSurfaceJson = json; }
            await PublishAsync(_statusTopic, json, retain: true, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The announcement and its complement, in one pass: a retained config for every entity
    /// this configuration announces, and an empty retained payload for every entity it does not. The
    /// second half is what actually deletes a switched-off group's entities — the discovery convention
    /// removes a component when its config topic is emptied. Leave it out and the retained config that
    /// created the entity stays on the broker, and the entity lingers as "unavailable" for ever.</summary>
    private async Task PublishDiscoveryAsync(CancellationToken ct)
    {
        HaCategorySet categories;
        lock (_stateLock) { categories = _categories; }
        var capabilities = CapabilityProvider?.Invoke() ?? HaCapabilities.Full;

        var presetNames = SettingsService.Read(s => s.Presets.Select(p => p.Name).ToList());
        foreach (var (topic, json) in HaDiscovery.DiscoveryConfigs(
                     _nodeId, _discoveryPrefix, _deviceName, _swVersion, presetNames,
                     HaEntityCatalog.Announce(categories, capabilities)))
            await PublishAsync(topic, json, retain: true, ct).ConfigureAwait(false);

        foreach (string topic in HaDiscovery.RemovalTopics(
                     _nodeId, _discoveryPrefix, HaEntityCatalog.Withheld(categories, capabilities)))
            await PublishAsync(topic, "", retain: true, ct).ConfigureAwait(false);
    }

    /// <summary>Publishes an entity state snapshot, retained so HA has a value immediately on restart.
    /// An unchanged payload is cached but not sent; the cache is updated while disconnected too, ready
    /// for the next connect.</summary>
    public void PublishState(HaState state)
    {
        if (!_enabled) return;
        string json = HaDiscovery.StatePayload(state);
        // Compare-and-set under the lock, so a stale write can't dedupe the next real change.
        lock (_stateLock)
        {
            if (string.Equals(json, _lastStateJson, StringComparison.Ordinal)) return;
            _lastStateJson = json;
        }
        if (_client.IsConnected)
            _ = PublishAsync(_stateTopic, json, retain: true, CancellationToken.None);
    }

    /// <summary>Inbound handler for the command topics. Runs on the MQTT receive callback, so it only
    /// parses and enqueues. Never throws — the MQTT loop must survive a bad payload — and never logs
    /// the payload.</summary>
    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            string topic = e.ApplicationMessage.Topic;
            if (HaDiscovery.CommandObjectId(_nodeId, topic) is not { } objectId) return Task.CompletedTask;

            // A command is an event, not state. With CleanSession plus resubscribe-on-connect, a
            // retained cmd/* payload would be redelivered and re-fire on every reconnect.
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
            // Recorded on acceptance, not dispatch: the status line answers "is the broker reaching us".
            MqttActivity.RecordCommand(cmd.Kind);
            _commands.Writer.TryWrite(cmd);   // unbounded and non-blocking; the worker drains it in order
        }
        catch (Exception ex) { AppLog.Error("HomeAssistantService.OnMessage", Sanitise(ex)); }
        return Task.CompletedTask;
    }

    /// <summary>Drains <see cref="_commands"/> one command at a time, so a blocking read-modify-write
    /// completes before the next starts. Ends when <see cref="Dispose"/> completes the channel writer.</summary>
    private async Task ProcessCommandsAsync()
    {
        await foreach (var cmd in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                // The fresh reflect happens via the services' StateChanged, not a publish here.
                // Synchronous read-modify-write on this worker.
                if (!HaCommandDispatcher.Dispatch(cmd, _actions, _settingsActions))
                    AppLog.Info($"HomeAssistant: refused command {cmd.Kind} (the value is not one this machine accepts).");
            }
            catch (Exception ex) { AppLog.Error("HomeAssistantService.Command", Sanitise(ex)); }
        }
    }

    /// <summary>Republishes the current Smart Charge, thresholds and preset after a change from any
    /// source — tray toggle, inbound command, profile auto-apply, or the override's auto-revert.</summary>
    private void OnChargeControlChanged()
    {
        if (!_enabled) return;
        if (_reflectGate.Signal())
            _ = ReflectLoopAsync();
    }

    /// <summary>Debounced driver for the post-command republish. The blocking vendor read runs here on
    /// a background continuation, never on the StateChanged caller's thread.</summary>
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
            catch (Exception ex) { AppLog.Error("HomeAssistantService.Reflect", Sanitise(ex)); }
        }
        while (_reflectGate.ShouldRepeat());
    }

    /// <summary>Publishes state once a command's write completes, taking the charge-control fields from
    /// a fresh device read because the command path never refreshes App's cached threshold state.</summary>
    private void PublishFreshStateAfterCommand()
    {
        if (CurrentStateProvider?.Invoke() is not { } baseState) return;
        var fresh = ChargeThresholdService.Read();
        var presets = SettingsService.Read(s => s.Presets.ToList());
        PublishState(HaStateBuilder.ApplyChargeControl(baseState, fresh, presets));
        // The one-shot override lives in the settings payload, so it reflects here too.
        PublishSurfaceNow();
    }

    /// <summary>The device's current thresholds, the companion value for a single-bound threshold set.
    /// One EC read, on the command worker where the write already blocks; null when unreadable.</summary>
    private static (int Start, int Stop)? CurrentDeviceThresholds() =>
        ChargeThresholdService.Read() is { Enabled: true } t ? (t.Start, t.Stop) : null;

    /// <summary>Re-runs the announcement after a change to what is announced: a preset renamed (the
    /// select's option list is baked into its config at publish time), or a group toggled. Also
    /// republishes the settings payload, since a group coming back on needs a value to show.</summary>
    public void RepublishDiscovery()
    {
        if (!_enabled) return;
        lock (_stateLock) { _categories = SettingsService.Read(s => s.MqttCategories); }
        if (!_client.IsConnected) return;   // the next connect runs the whole sequence anyway
        _ = RepublishDiscoveryAsync();
    }

    private async Task RepublishDiscoveryAsync()
    {
        try
        {
            await PublishDiscoveryAsync(CancellationToken.None).ConfigureAwait(false);
            PublishSurfaceNow();   // a group coming back on has no value until this lands
        }
        catch (Exception ex) { AppLog.Error("HomeAssistantService.RepublishDiscovery", Sanitise(ex)); }
    }

    /// <summary>Publishes the settings/network/diagnostic snapshot, retained and deduped like the
    /// battery one. The cache is updated while disconnected too, ready for the next connect.</summary>
    public void PublishSurface(HaSurfaceState surface)
    {
        if (!_enabled) return;
        string json = HaSurfacePayload.Build(surface);
        lock (_stateLock)
        {
            if (string.Equals(json, _lastSurfaceJson, StringComparison.Ordinal)) return;
            _lastSurfaceJson = json;
        }
        if (_client.IsConnected)
            _ = PublishAsync(_statusTopic, json, retain: true, CancellationToken.None);
    }

    /// <summary>Whether there is a live link to publish onto: the feature running and the client
    /// connected. What a "publish now" can act on, and what the Settings page gates its button by.</summary>
    public bool IsConnected => _enabled && _client.IsConnected;

    /// <summary>Publishes the current state and settings snapshots on demand. Nothing is announced
    /// and no config topic is written, so this republishes what the entities already are rather than
    /// re-declaring that they exist. False when nothing reached the broker.</summary>
    /// <remarks>
    /// The dedupe caches are bypassed on purpose, and updated as if the payload had changed. Dropping
    /// an unchanged payload is right for a signal and wrong for a button: pressing it and having
    /// nothing leave the machine is indistinguishable from a dead connection. Which groups are
    /// switched on needs no filter here — a withheld group's entities are never announced and its
    /// config topic is emptied, so nothing consumes the fields it would have read.
    /// </remarks>
    public async Task<bool> PublishCurrentStateAsync()
    {
        if (!IsConnected) return false;
        try
        {
            bool sent = false;
            if (CurrentStateProvider?.Invoke() is { } state)
            {
                string json = HaDiscovery.StatePayload(state);
                lock (_stateLock) { _lastStateJson = json; }
                sent |= await PublishAsync(_stateTopic, json, retain: true, CancellationToken.None).ConfigureAwait(false);
            }
            if (CurrentSurfaceProvider?.Invoke() is { } surface)
            {
                string json = HaSurfacePayload.Build(surface);
                lock (_stateLock) { _lastSurfaceJson = json; }
                sent |= await PublishAsync(_statusTopic, json, retain: true, CancellationToken.None).ConfigureAwait(false);
            }
            return sent;
        }
        catch (Exception ex)
        {
            AppLog.Error("HomeAssistantService.PublishCurrentState", Sanitise(ex));
            return false;
        }
    }

    /// <summary>Reflects a change straight back, without waiting for a battery tick — a settings write
    /// from the broker, a keep-awake session ending, a network location moving. Signals rather than
    /// publishes: the snapshot reaches a vendor service and an adapter enumeration, and the loudest
    /// caller is a Settings toggle on the UI thread.</summary>
    public void PublishSurfaceNow()
    {
        if (!_enabled) return;
        if (_surfaceGate.Signal())
            _ = Task.Run(SurfaceLoopAsync);
    }

    /// <summary>Coalesced driver for the settings publish. The trailing pass is what guarantees the
    /// last snapshot wins: two concurrent reads could otherwise let an older one take the dedupe slot
    /// and strand the newer value.</summary>
    private async Task SurfaceLoopAsync()
    {
        do
        {
            _surfaceGate.BeginPass();
            try
            {
                if (_enabled && CurrentSurfaceProvider?.Invoke() is { } surface) PublishSurface(surface);
            }
            catch (Exception ex) { AppLog.Error("HomeAssistantService.PublishSurface", Sanitise(ex)); }
            await Task.Yield();   // never hold the pool thread across the repeat check
        }
        while (_surfaceGate.ShouldRepeat());
    }

    /// <summary>Empties every retained topic a superseded node id owned, so HA deletes the old device
    /// rather than leaving a ghost. No-op while disconnected: the next connect runs it.</summary>
    private async Task ClearStaleIdentityAsync(CancellationToken ct)
    {
        if (!_client.IsConnected) return;
        if (Interlocked.Exchange(ref _staleIdentity, null) is not { } stale) return;
        foreach (string topic in HaDiscovery.TopicsToClear(stale.NodeId, stale.DiscoveryPrefix))
            await PublishAsync(topic, "", retain: true, ct).ConfigureAwait(false);
    }

    /// <summary>Empties the superseded config topics so an upgrading user keeps no ghost entities.</summary>
    private async Task ClearLegacyDiscoveryAsync(CancellationToken ct)
    {
        foreach (var (component, objectId) in HaDiscovery.LegacyEntities)
            await PublishAsync(HaDiscovery.ConfigTopic(_discoveryPrefix, component, _nodeId, objectId),
                               "", retain: true, ct).ConfigureAwait(false);
    }

    /// <summary>False when the message did not reach the broker. Only a caller the user is watching
    /// needs to know — everything else publishes into the background, where the log is the trace.</summary>
    private async Task<bool> PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
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
            // The one choke point every outbound message passes through.
            MqttActivity.RecordPublish();
            return true;
        }
        catch (Exception ex) { AppLog.Error("HomeAssistantService.Publish", Sanitise(ex)); return false; }
    }

    private async Task StopInternalAsync(bool clearDiscovery, CancellationToken ct = default)
    {
        _enabled = false;
        _cts?.Cancel();
        try
        {
            if (_client.IsConnected)
            {
                if (clearDiscovery)
                {
                    // Feature turned off: empty every retained topic the node owns, state included, so
                    // HA drops the device. An "offline" publish here would re-retain what this cleared.
                    foreach (string topic in HaDiscovery.TopicsToClear(_nodeId, _discoveryPrefix))
                        await PublishAsync(topic, "", retain: true, ct).ConfigureAwait(false);
                }
                else
                    // A normal exit keeps the retained discovery, so the device persists in HA.
                    await PublishAsync(_availTopic, HaDiscovery.Offline, retain: true, ct).ConfigureAwait(false);

                await _client.DisconnectAsync(cancellationToken: ct).ConfigureAwait(false);
            }
        }
        catch { /* best-effort teardown */ }
    }

    // Keeps a thrown broker error from carrying the password into the log: type and message only.
    private static Exception Sanitise(Exception ex) => new($"{ex.GetType().Name}: {ex.Message}");

    public void Dispose()
    {
        ChargeControlService.StateChanged  -= OnChargeControlChanged;
        TravelOverrideService.StateChanged -= OnChargeControlChanged;
        try { _commands.Writer.TryComplete(); } catch { }
        // Reached from the tray's Exit on the UI thread, so run the teardown off it and bound it. The
        // token expires before the wait, so the wait ends because the work ended rather than with a
        // QoS 1 publish still in flight into _client.Dispose(), waiting on a PUBACK a half-dead socket
        // never sends. Left undisposed: the token can outlive this call, and the process is exiting.
        var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        try { Task.Run(() => StopInternalAsync(clearDiscovery: false, stopCts.Token)).Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { _commandWorker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _client.Dispose();
        _cts?.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Collapses a burst of signals into at most one in-flight run plus one trailing run. Tracks
/// only the running/pending flags, so the coalescing decision is testable without threads.</summary>
internal sealed class CoalescingGate
{
    private readonly object _lock = new();
    private bool _running;
    private bool _pending;

    /// <summary>Records a signal, returning true only to the caller that must start the loop; a signal
    /// arriving while one runs returns false but arms a trailing pass.</summary>
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

    /// <summary>True to run another pass; otherwise clears the running flag and ends the loop.</summary>
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
