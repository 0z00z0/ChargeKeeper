using System.Diagnostics;
using System.Runtime.ExceptionServices;
using ChargeKeeper.Helpers;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.Discovery;

namespace ChargeKeeper.Services;

/// <summary>
/// ChargeKeeper's MQTT publisher: the module's connection and discovery publisher, wired to this
/// app's entity table, its publish groups and the services an inbound command writes. The protocol,
/// the endpoint sweep, the document and the ledger are the module's; everything domain-shaped here.
/// </summary>
/// <remarks>Inert until the module's own settings say publishing is on and a broker host is set.</remarks>
internal sealed class MqttPublisher : IDisposable
{
    /// <summary>The application's own segment at the head of every state and command topic, and the
    /// stem of the default device id. It is carried by every retained topic on every existing
    /// installation, so it is as permanent as an entity id.</summary>
    public const string TopicRoot = "chargekeeper";

    private readonly string _appVersion;
    private readonly MqttSettingsFile _settings;
    private readonly PublishGroupSet _groups;
    private readonly DiscoveryPublisher _publisher;
    private readonly MqttConnection _connection;
    private readonly SettingsActions? _ownSettingsActions;

    private readonly Memo<LiveState?> _live;
    private readonly Memo<SurfaceState?> _surface;
    private readonly Memo<PublishCapabilities> _capabilities;

    private int _disposed;

    /// <param name="appVersion">The software version the device and origin blocks report.</param>
    /// <param name="live">The live battery snapshot, or null before the first reading. Read on the
    /// MQTT threads, so it must not block on the UI.</param>
    /// <param name="charge">The charge-control seam; the live one when null.</param>
    /// <param name="settings">The settings seam; the live one when null.</param>
    public MqttPublisher(
        string appVersion,
        Func<LiveState?> live,
        IChargeControlActions? charge = null,
        ISettingsActions? settings = null)
    {
        _appVersion = appVersion;
        var log = new AppMqttLog();

        Directory.CreateDirectory(AppPaths.DataDir);
        _settings = MqttSettingsFile.In(AppPaths.DataDir);
        // Opening the store does not write the file, so "the file exists" is still a sound test for
        // "already migrated" at this point — and it has to run before anything reads the settings.
        MqttSettingsMigration.Run(SettingsService.FilePath, AppPaths.DataDir, _settings);
        _groups = new PublishGroupSet(_settings, MqttPublishGroups.Declared);

        _live = new Memo<LiveState?>(live);
        _surface = new Memo<SurfaceState?>(() => SurfaceReader.Read(_appVersion));
        // Unguarded on purpose: a vendor read that fails has to reach the announcement as a throw, so
        // "could not be read" keeps the disposition already recorded rather than withholding the
        // entities behind it. The memo carries the failure for its own window so one unanswered call
        // is not retried once per entity.
        _capabilities = new Memo<PublishCapabilities>(SurfaceReader.Capabilities);

        if (settings is null)
        {
            _ownSettingsActions = new SettingsActions();
            settings = _ownSettingsActions;
        }

        Entities = MqttEntityCatalog.Build(new MqttEntitySources
        {
            Live = () => _live.Read(),
            Surface = () => _surface.Read(),
            Capabilities = () => _capabilities.Read(),
            Charge = charge ?? new ChargeControlActions(CachedThresholds),
            Settings = settings,
            // Already memoised and gated inside ThermalStatusService itself — no per-window cache
            // needed here, unlike Live/Surface/Capabilities, which reach an EC or vendor RPC.
            SystemTemperature = () => ThermalStatusService.PublishableCelsius,
            SystemTemperatureMaximum = () => ThermalStatusService.RecommendedMaximumCelsius,
        });

        MqttConnection? connection = null;

        _publisher = new DiscoveryPublisher(new DiscoveryPublisherSetup
        {
            IsConnected       = () => connection?.IsConnected ?? false,
            TopicRoot         = TopicRoot,
            Device            = new DiscoveryDevice("ZeroZero Software", AppInfo.Name, appVersion),
            Origin            = new DiscoveryOrigin(AppInfo.Name, appVersion,
                                    SupportUrl: "https://github.com/0z00z0/ChargeKeeper"),
            Entities          = Entities,
            Ledger            = DiscoveryLedgerFile.In(AppPaths.DataDir),
            Groups            = _groups,
            Retired           = MqttEntityCatalog.Retired,
            Migrating         = MqttEntityCatalog.Migrating,
            RetiredChannels   = MqttEntityCatalog.RetiredChannels,
            SetChannelsAsync  = (channels, ct) => connection!.SetChannelsAsync(channels, ct),
            SetCommandTargets = targets => connection!.SetCommandTargets(targets),
            Log               = log,
        });

        connection = new MqttConnection(new MqttConnectionSetup
        {
            TopicRoot         = TopicRoot,
            Channels          = _publisher.Channels(),
            CommandTargets    = _publisher.CommandTargets(),
            Subscriptions     = [_publisher.BirthMessage(DiscoveryPrefix())],
            Listener          = _publisher,
            DefaultDeviceName = machine => $"{AppInfo.Name} ({machine})",
            RecallEndpoint    = () => SettingsService.Current.MqttLastGoodEndpoint,
            RememberEndpoint  = memory => SettingsService.Update(s => s.MqttLastGoodEndpoint = memory),
            CommandRefused    = OnCommandRefused,
            Log               = log,
        });
        _connection = connection;

        // A write from an inbound command reflects at once rather than waiting for a battery tick.
        if (_ownSettingsActions is { } own) own.Changed += PublishSurfaceNow;

        // Every broker edit the panel commits comes back through here, and Apply is idempotent, so a
        // change that leaves the projection identical costs nothing and never bounces the socket.
        _settings.Changed += OnSettingsChanged;
        _connection.Apply(_settings.Read().Connect());
    }

    /// <summary>The module's settings store, for the panel. The panel writes through it, never
    /// around it.</summary>
    public IMqttSettingsStore Settings => _settings;

    /// <summary>The declared publish groups and their state, for the panel.</summary>
    public PublishGroupSet Groups => _groups;

    /// <summary>When something last reached the broker, and what the broker last asked for.</summary>
    public MqttActivity Activity => _connection.Activity;

    /// <summary>What the connection is doing. Asked rather than held: the link comes and goes on its
    /// own, so a cached answer is stale the moment the page stops looking.</summary>
    public MqttConnectionState State => _connection.State;

    public bool IsConnected => _connection.IsConnected;

    /// <summary>The entity table in force, for the panel's entity-id-to-name lookup and for tests.</summary>
    public MqttEntitySet Entities { get; }

    /// <summary>The name published for this machine when the device-name box is empty. One expression,
    /// so the panel's placeholder and what the publisher falls back to cannot disagree.</summary>
    public string DefaultDeviceName => $"{AppInfo.Name} ({Environment.MachineName})";

    /// <summary>Where the broker last answered, for the panel's Status rows. Read-only there.</summary>
    public MqttEndpointMemory? RecallEndpoint() => SettingsService.Current.MqttLastGoodEndpoint;

    /// <summary>Re-applies the connection from the stored settings. What the panel's
    /// <c>ConnectionChanged</c> is wired to, and what keeps its device-id promise: the ledger evicts
    /// the superseded identity by what it actually published.</summary>
    public void ApplyConnection()
    {
        // The birth-message filter carries the discovery prefix, so a prefix change needs the
        // subscription rebuilt; it takes effect at the next connect, which the apply below causes.
        _connection.SetSubscriptions([_publisher.BirthMessage(DiscoveryPrefix())]);
        _connection.Apply(_settings.Read().Connect());
    }

    /// <summary>Re-announces the document. What a group toggle and a renamed preset come through:
    /// the announced entity set and the selects' options are baked into the retained document, so
    /// republishing state alone would leave the broker with the set captured at connect time.</summary>
    public void Republish()
    {
        _surface.Invalidate();
        _publisher.Republish();
    }

    /// <summary>Every channel, dedupe bypassed. What "Publish now" needs, where nothing leaving the
    /// machine is indistinguishable from a dead connection.</summary>
    public Task<bool> PublishNowAsync()
    {
        _live.Invalidate();
        _surface.Invalidate();
        return _connection.PublishNowAsync();
    }

    /// <summary>Takes a battery tick's snapshot and signals a publish. The snapshot is built by the
    /// caller under its own lock, so it arrives coherent rather than being re-read here.</summary>
    public void PublishState(LiveState state)
    {
        _live.Set(state);
        _connection.RequestPublish();
    }

    /// <summary>Signals a publish of the settings, network and diagnostic values. They have no tick of
    /// their own, so every source that can move one of them calls this; an unchanged payload is
    /// deduped, so a redundant signal costs nothing.</summary>
    public void PublishSurfaceNow()
    {
        _surface.Invalidate();
        _connection.RequestPublish();
    }

    /// <summary>The host's power-mode handler calls this. The connection does not subscribe to system
    /// events itself, because the unsubscribe lifetime belongs to the host.</summary>
    public void OnPowerResume()
    {
        // A resume is exactly when a vendor interface is least likely to answer, and exactly when the
        // readings are most likely to be stale.
        _live.Invalidate();
        _surface.Invalidate();
        _capabilities.Invalidate();
        _connection.OnPowerResume();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _settings.Changed -= OnSettingsChanged;
        if (_ownSettingsActions is { } own) own.Changed -= PublishSurfaceNow;

        // Teardown is synchronous, bounded and idempotent, and publishes offline before the socket goes.
        _connection.Dispose();
        _publisher.Dispose();
        _settings.Dispose();
    }

    /// <summary>The discovery prefix the birth-message filter is composed from. A stored prefix that is
    /// blank means the module's own default, not an empty segment.</summary>
    private string DiscoveryPrefix() =>
        _settings.Read().DiscoveryPrefix is { Length: > 0 } prefix
            ? prefix
            : MqttSettings.DefaultDiscoveryPrefix;

    private void OnSettingsChanged() => ApplyConnection();

    private void OnCommandRefused(MqttCommandRefusal refusal) =>
        AppLog.Info($"MQTT: {Entities.NameOf(refusal.EntityId)} refused ({refusal.Outcome})"
                  + (refusal.Detail is { Length: > 0 } detail ? $": {detail}" : "."));

    /// <summary>The charge thresholds the last battery tick saw, for a single-bound number-set to
    /// combine against. Null when there is no reading yet, which falls back to a live device read.</summary>
    private (int Start, int Stop)? CachedThresholds() =>
        _live.Read() is { ChargeStart: { } start, ChargeStop: { } stop } ? (start, stop) : null;

    /// <summary>
    /// One value, read at most once per window however many entities ask for it.
    /// </summary>
    /// <remarks>
    /// <para>An announcement pass asks forty-eight entities in turn and a publish pass asks them again,
    /// and two of the three sources behind them reach a vendor interface. Without this, one pass is
    /// forty-eight EC or WMI calls.</para>
    /// <para>A failure is held for the window too, and rethrown. The alternative is to retry the call
    /// that just failed once per entity, which is the worst thing to do to an interface that is not
    /// answering — and a resume from standby is when that happens.</para>
    /// </remarks>
    private sealed class Memo<T>(Func<T> produce)
    {
        /// <summary>Long enough to cover one pass over the whole table, short enough that nothing has
        /// to remember to invalidate it. Every signal that knows a value has moved invalidates anyway;
        /// this is the floor under the ones that do not.</summary>
        private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(1);

        private readonly Lock _gate = new();
        private T? _value;
        private ExceptionDispatchInfo? _fault;
        private long _taken = long.MinValue;

        public T Read()
        {
            lock (_gate)
            {
                if (Fresh())
                {
                    _fault?.Throw();
                    return _value!;
                }

                try
                {
                    _value = produce();
                    _fault = null;
                }
                catch (Exception ex)
                {
                    _value = default;
                    _fault = ExceptionDispatchInfo.Capture(ex);
                }

                _taken = Stopwatch.GetTimestamp();
                _fault?.Throw();
                return _value!;
            }
        }

        /// <summary>Stores a value the caller already has, and makes it the fresh one.</summary>
        public void Set(T value)
        {
            lock (_gate)
            {
                _value = value;
                _fault = null;
                _taken = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>Forces the next read to produce. Called by every signal that knows the value moved.</summary>
        public void Invalidate()
        {
            lock (_gate) _taken = long.MinValue;
        }

        private bool Fresh() =>
            _taken != long.MinValue && Stopwatch.GetElapsedTime(_taken) < MaxAge;
    }
}

/// <summary>The module's log sink over <see cref="AppLog"/>. The module owns no logging framework and
/// sanitises an exception before it gets here, so no staged credential reaches the file.</summary>
internal sealed class AppMqttLog : IMqttLog
{
    public void Info(string message) => AppLog.Info(message);

    public void Error(string source, Exception? ex) => AppLog.Error(source, ex);
}
