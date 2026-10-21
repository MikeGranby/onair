using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace OnAir;

/// <summary>
/// Notification-area host for the monitor. The icon is red while the outlets are on; the menu
/// switches between Auto (follow the microphone), forced On, and forced Off.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    readonly OnAirService _service;
    readonly CancellationTokenSource _cts = new();
    readonly Task _run;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _statusItem;
    readonly ToolStripMenuItem _autoItem;
    readonly ToolStripMenuItem _onItem;
    readonly ToolStripMenuItem _offItem;
    readonly Icon _onIcon = MakeIcon(Color.FromArgb(220, 30, 30));
    readonly Icon _offIcon = MakeIcon(Color.FromArgb(140, 140, 140));
    readonly EventWaitHandle _stopEvent;
    readonly RegisteredWaitHandle _stopWait;

    public TrayApp(AppConfig config)
    {
        _service = new OnAirService(config);

        _statusItem = new ToolStripMenuItem("Starting") { Enabled = false };
        _autoItem = new ToolStripMenuItem("Follow Mic", null, (_, _) => SetMode(OnAirMode.Auto));
        _onItem = new ToolStripMenuItem("Force On", null, (_, _) => SetMode(OnAirMode.On));
        _offItem = new ToolStripMenuItem("Force Off", null, (_, _) => SetMode(OnAirMode.Off));
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            _autoItem,
            _onItem,
            _offItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open Log", null, (_, _) => OpenLog()),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit()),
        });
        _tray = new NotifyIcon { Icon = _offIcon, Text = "Starting", ContextMenuStrip = menu, Visible = true };
        UpdateModeChecks();

        // The service runs on the thread pool; marshal its updates back to the UI thread.
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _service.StatusChanged += status => ui.Post(_ => ShowStatus(status), null);
        _run = Task.Run(() => _service.RunAsync(_cts.Token));
        _run.ContinueWith(run => ui.Post(_ => OnStopped(run), null));

        // "OnAir --stop" from the command line.
        _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, OnAirService.StopEventName);
        _stopWait = ThreadPool.RegisterWaitForSingleObject(_stopEvent, (_, _) => _cts.Cancel(), null, Timeout.Infinite, executeOnlyOnce: true);

        SystemEvents.SessionEnded += OnSessionEnded;
    }

    void SetMode(OnAirMode mode)
    {
        _service.Mode = mode;
        UpdateModeChecks();
    }

    void UpdateModeChecks()
    {
        _autoItem.Checked = _service.Mode == OnAirMode.Auto;
        _onItem.Checked = _service.Mode == OnAirMode.On;
        _offItem.Checked = _service.Mode == OnAirMode.Off;
    }

    void ShowStatus(OnAirStatus status)
    {
        _tray.Icon = status.OnAir ? _onIcon : _offIcon;
        _statusItem.Text = status.Text;
        var tip = status.Text;
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    static void OpenLog()
    {
        if (File.Exists(Log.FilePath))
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    // The service turns the outlets off as it stops; OnStopped then closes the app.
    void Exit()
    {
        _statusItem.Text = "Stopping";
        _cts.Cancel();
    }

    void OnStopped(Task run)
    {
        if (run.Exception?.GetBaseException() is { } ex)
        {
            Log.Write($"Error: {ex.Message}");
            MessageBox.Show(ex.Message, "On Air", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        ExitThread();
    }

    // Logoff/shutdown: Windows ends the process soon after this returns, so turn the outlets off first.
    void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        _cts.Cancel();
        try
        {
            _run.Wait(TimeSpan.FromSeconds(4));
        }
        catch (AggregateException)
        {
        }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionEnded -= OnSessionEnded;
        _stopWait.Unregister(null);
        _stopEvent.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }

    static Icon MakeIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(brush, 3, 3, 26, 26);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
