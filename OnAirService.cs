namespace OnAir;

/// <summary>How "on air" things are. Video implies the call is live too, so it outranks Audio.</summary>
enum OnAirLevel { Off = 0, Audio = 1, Video = 2 }

/// <summary>What the tray shows: the current level, and a one-line description.</summary>
sealed record OnAirStatus(OnAirLevel Level, string Text);

/// <summary>
/// Watches the microphone and camera and switches each outlet according to its level: outlets
/// with no level are on at Audio or Video, others only at their own level. The level can also be
/// forced from the tray.
/// </summary>
sealed class OnAirService(AppConfig config)
{
    public const string InstanceName = @"Local\OnAir.Monitor";
    public const string StopEventName = @"Local\OnAir.Stop";

    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    readonly AutoResetEvent _wake = new(false);
    int _forced = -1;   // forced OnAirLevel, or -1 to follow the devices
    OnAirStatus? _status;

    /// <summary>Raised on a background thread whenever the status changes.</summary>
    public event Action<OnAirStatus>? StatusChanged;

    /// <summary>The level to hold regardless of the devices, or null to follow them.</summary>
    public OnAirLevel? Forced
    {
        get => Volatile.Read(ref _forced) is var v and >= 0 ? (OnAirLevel)v : null;
        set
        {
            int v = value is { } level ? (int)level : -1;
            if (Interlocked.Exchange(ref _forced, v) == v)
                return;
            Log.Write(value is { } forced ? $"Forced to {forced}." : "Following devices.");
            _wake.Set();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var apiKey = config.RequireApiKey();
        config.RequireOutlets();

        using var devices = new DeviceMonitor();
        using var govee = new GoveeClient(apiKey);
        var outlets = new OutletController(govee, config.Outlets);

        Log.Write($"On Air monitor started for {config.Outlets.Count} outlet(s).");
        try
        {
            await ResolveWithRetryAsync(outlets, ct);
            await MonitorAsync(devices, outlets, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (config.TurnOffOnExit && outlets.IsResolved)
            {
                using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));
                try
                {
                    if (await outlets.SetAllAsync(false, exitCts.Token))
                        Log.Write("Outlets turned off.");
                }
                catch (OperationCanceledException)
                {
                    Log.Write("Timed out turning outlets off.");
                }
            }
            Log.Write("Stopped.");
        }
    }

    /// <summary>Asks a running monitor to stop. Returns false if none is running.</summary>
    public static bool SignalStop()
    {
        if (!EventWaitHandle.TryOpenExisting(StopEventName, out var stopEvent))
            return false;
        using (stopEvent)
            stopEvent.Set();
        return true;
    }

    void Report(OnAirLevel level, string text)
    {
        var status = new OnAirStatus(level, text);
        if (status == _status)
            return;
        _status = status;
        StatusChanged?.Invoke(status);
    }

    async Task ResolveWithRetryAsync(OutletController outlets, CancellationToken ct)
    {
        Report(OnAirLevel.Off, "Connecting to Govee");
        while (true)
        {
            try
            {
                await outlets.ResolveAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is GoveeException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                Log.Write($"Could not look up Govee devices: {ex.Message} Retrying in {RetryDelay.TotalSeconds:0}s.");
                Report(OnAirLevel.Off, "Can't Reach Govee - Retrying");
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    async Task MonitorAsync(DeviceMonitor devices, OutletController outlets, CancellationToken ct)
    {
        var onDelay = TimeSpan.FromSeconds(Math.Max(0, config.OnDelaySeconds));
        var offDelay = TimeSpan.FromSeconds(Math.Max(0, config.OffDelaySeconds));
        var poll = TimeSpan.FromSeconds(Math.Max(1, config.PollIntervalSeconds));

        var mic = new Debouncer(onDelay, offDelay);
        var cam = new Debouncer(onDelay, offDelay);
        var groups = config.Outlets
            .GroupBy(o => o.Level)
            .OrderBy(g => g.Key)
            .Select(g => new OutletGroup(g.Key, g.ToList()))
            .ToList();
        List<string>? lastMicApps = null;
        List<string>? lastCamApps = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow;
            var forced = Forced;
            var micApps = InUseApps(devices, CaptureDevice.Microphone, ref lastMicApps);
            var camApps = InUseApps(devices, CaptureDevice.Camera, ref lastCamApps);
            mic.Update(micApps.Count > 0, now);
            cam.Update(camApps.Count > 0, now);

            var detected = cam.Value ? OnAirLevel.Video : mic.Value ? OnAirLevel.Audio : OnAirLevel.Off;
            var level = forced ?? detected;

            await Task.WhenAll(groups.Select(g => g.ApplyAsync(outlets, g.IsOnAt(level), ct)));

            var text = level == OnAirLevel.Off ? "Off Air" : $"On Air ({level})";
            if (forced != null)
                text = level == OnAirLevel.Off ? "Off Air (Forced)" : $"On Air ({level}, Forced)";
            else if (level != OnAirLevel.Off)
                text += " - " + string.Join(", ", micApps.Concat(camApps).Select(DeviceMonitor.FriendlyName).Distinct(StringComparer.OrdinalIgnoreCase));
            if (groups.All(g => g.Applied == null))
                text = "Starting";
            bool failing = groups.Any(g => g.Applied != g.Desired);
            if (failing)
                text += " - Govee Error, Retrying";
            // While a switch is failing, show Off unless some lights did come on.
            var shown = failing && !groups.Any(g => g.Applied == true) ? OnAirLevel.Off : level;
            Report(shown, text);

            // Wake early if a pending on/off delay expires before the next poll.
            var wait = new[] { poll, mic.Pending(now) ?? poll, cam.Pending(now) ?? poll }.Min();
            if (wait < TimeSpan.FromMilliseconds(100))
                wait = TimeSpan.FromMilliseconds(100);

            devices.WaitForChange(wait, ct.WaitHandle, _wake);
        }
    }

    List<string> InUseApps(DeviceMonitor devices, CaptureDevice device, ref List<string>? last)
    {
        var apps = devices.GetInUseApps(device)
            .Where(a => !config.IsExcluded(a))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (last == null || !apps.SequenceEqual(last, StringComparer.OrdinalIgnoreCase))
        {
            Log.Write(apps.Count > 0
                ? $"{device} in use by: {string.Join(", ", apps.Select(DeviceMonitor.FriendlyName))}"
                : $"{device} idle.");
            last = apps;
        }
        return apps;
    }

    /// <summary>Holds a device's in-use state steady until a change has lasted the on/off delay.</summary>
    sealed class Debouncer(TimeSpan onDelay, TimeSpan offDelay)
    {
        bool? _raw;
        DateTime _changedAt;

        public bool Value { get; private set; }

        public void Update(bool raw, DateTime now)
        {
            if (_raw == null)
                Value = raw;        // at startup, take the current state without waiting
            else if (raw != _raw)
                _changedAt = now;
            _raw = raw;
            if (raw != Value && now - _changedAt >= Delay)
                Value = raw;
        }

        /// <summary>Time until a pending change takes effect, or null if none is pending.</summary>
        public TimeSpan? Pending(DateTime now) =>
            _raw is { } raw && raw != Value ? Delay - (now - _changedAt) : null;

        TimeSpan Delay => _raw == true ? onDelay : offDelay;
    }

    /// <summary>The outlets sharing one level (null = any), switched together.</summary>
    sealed class OutletGroup(OnAirLevel? level, IReadOnlyList<OutletConfig> outlets)
    {
        readonly string _name = level?.ToString() ?? "Audio/Video";
        DateTime _nextAttempt = DateTime.MinValue;

        public bool? Applied { get; private set; }   // state last sent successfully; null = unknown
        public bool? Desired { get; private set; }

        public bool IsOnAt(OnAirLevel current) =>
            level is { } only ? current == only : current != OnAirLevel.Off;

        public async Task ApplyAsync(OutletController controller, bool on, CancellationToken ct)
        {
            // A new target is tried right away; only repeats of a failed one wait for the retry delay.
            if (on != Desired)
                _nextAttempt = DateTime.MinValue;
            Desired = on;
            if (on == Applied || DateTime.UtcNow < _nextAttempt)
                return;

            if (await controller.SetAsync(outlets, on, ct))
            {
                Applied = on;
                Log.Write($"{_name} outlets {(on ? "on" : "off")}.");
            }
            else
            {
                _nextAttempt = DateTime.UtcNow + RetryDelay;
                Log.Write($"Retrying {_name} outlets in {RetryDelay.TotalSeconds:0}s.");
            }
        }
    }
}
