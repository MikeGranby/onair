namespace OnAir;

enum OnAirMode { Auto, On, Off }

/// <summary>What the tray shows: whether the outlets are on, and a one-line description.</summary>
sealed record OnAirStatus(bool OnAir, string Text);

/// <summary>Watches the microphone and keeps the outlets on while it is in use, unless forced on or off.</summary>
sealed class OnAirService(AppConfig config)
{
    public const string InstanceName = @"Local\OnAir.Monitor";
    public const string StopEventName = @"Local\OnAir.Stop";

    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    readonly AutoResetEvent _wake = new(false);
    volatile OnAirMode _mode = OnAirMode.Auto;
    OnAirStatus? _status;

    /// <summary>Raised on a background thread whenever the status changes.</summary>
    public event Action<OnAirStatus>? StatusChanged;

    public OnAirMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;
            _mode = value;
            Log.Write($"Mode set to {value}.");
            _wake.Set();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var apiKey = config.RequireApiKey();
        config.RequireOutlets();

        using var mic = new MicrophoneMonitor();
        using var govee = new GoveeClient(apiKey);
        var outlets = new OutletController(govee, config.Outlets);

        Log.Write($"On Air monitor started for {config.Outlets.Count} outlet(s).");
        try
        {
            await ResolveWithRetryAsync(outlets, ct);
            await MonitorAsync(mic, outlets, ct);
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
                    if (await outlets.SetAsync(false, exitCts.Token))
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

    void Report(bool onAir, string text)
    {
        var status = new OnAirStatus(onAir, text);
        if (status == _status)
            return;
        _status = status;
        StatusChanged?.Invoke(status);
    }

    async Task ResolveWithRetryAsync(OutletController outlets, CancellationToken ct)
    {
        Report(false, "Connecting to Govee");
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
                Report(false, "Can't Reach Govee - Retrying");
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    async Task MonitorAsync(MicrophoneMonitor mic, OutletController outlets, CancellationToken ct)
    {
        var onDelay = TimeSpan.FromSeconds(Math.Max(0, config.OnDelaySeconds));
        var offDelay = TimeSpan.FromSeconds(Math.Max(0, config.OffDelaySeconds));
        var poll = TimeSpan.FromSeconds(Math.Max(1, config.PollIntervalSeconds));

        bool? applied = null;                // state last sent successfully; null = unknown
        bool? lastDesired = null;
        List<string>? lastApps = null;
        var changedAt = DateTime.MinValue;   // when the mic last switched between in use and idle
        var nextAttempt = DateTime.MinValue;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow;
            var mode = _mode;
            var apps = mic.GetInUseApps()
                .Where(a => !config.IsExcluded(a))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool micOn = apps.Count > 0;

            if (lastApps == null || !apps.SequenceEqual(lastApps, StringComparer.OrdinalIgnoreCase))
            {
                Log.Write(micOn
                    ? $"Microphone in use by: {string.Join(", ", apps.Select(MicrophoneMonitor.FriendlyName))}"
                    : "Microphone idle.");
                if (lastApps != null && micOn != (lastApps.Count > 0))
                    changedAt = now;
                lastApps = apps;
            }

            // Debounce: only switch once the mic has held its new state for the configured delay.
            var elapsed = now - changedAt;
            bool desired = mode switch
            {
                OnAirMode.On => true,
                OnAirMode.Off => false,
                _ => micOn
                    ? applied == true || elapsed >= onDelay
                    : applied == true && elapsed < offDelay,
            };

            // A new target state is tried right away; only repeats of a failed one wait for the retry delay.
            if (desired != lastDesired)
                nextAttempt = DateTime.MinValue;
            lastDesired = desired;

            if (desired != applied && now >= nextAttempt)
            {
                if (await outlets.SetAsync(desired, ct))
                {
                    applied = desired;
                    Log.Write(desired ? "ON AIR - outlets on." : "Off air - outlets off.");
                }
                else
                {
                    nextAttempt = DateTime.UtcNow + RetryDelay;
                    Log.Write($"Retrying in {RetryDelay.TotalSeconds:0}s.");
                }
            }

            var text = applied switch
            {
                true when mode == OnAirMode.On => "On Air (Forced On)",
                true when micOn => $"On Air - {string.Join(", ", apps.Select(MicrophoneMonitor.FriendlyName))}",
                true => "On Air",
                false when mode == OnAirMode.Off => "Off Air (Forced Off)",
                false => "Off Air",
                null => "Starting",
            };
            if (desired != applied)
                text += " - Govee Error, Retrying";
            Report(applied == true, text);

            // Wake early if a pending on/off delay expires before the next poll.
            var wait = poll;
            if (micOn && applied != true && onDelay - elapsed < wait)
                wait = onDelay - elapsed;
            else if (!micOn && applied == true && offDelay - elapsed < wait)
                wait = offDelay - elapsed;
            if (wait < TimeSpan.FromMilliseconds(100))
                wait = TimeSpan.FromMilliseconds(100);

            mic.WaitForChange(wait, ct.WaitHandle, _wake);
        }
    }
}
