using System.Runtime.InteropServices;
using System.Text.Json;

namespace OnAir;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        bool tray = args.Length == 0;
        // This is a windowed app with no console of its own; borrow the terminal's for command output.
        if (!tray)
            AttachConsole(AttachParentProcess);

        var configPath = Path.Combine(AppContext.BaseDirectory, AppConfig.FileName);
        AppConfig config;
        try
        {
            config = AppConfig.Load(configPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return Fail($"Could not read {configPath}: {ex.Message}", tray);
        }

        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                null => RunTray(config),
                "--stop" => Stop(),
                "--status" => Status(config),
                "--list-devices" => ListDevicesAsync(config).GetAwaiter().GetResult(),
                "--on" => SetOnceAsync(config, true).GetAwaiter().GetResult(),
                "--off" => SetOnceAsync(config, false).GetAwaiter().GetResult(),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is ConfigException or GoveeException or HttpRequestException)
        {
            return Fail(ex.Message, tray);
        }
    }

    static int Fail(string message, bool tray)
    {
        if (tray)
            MessageBox.Show(message, "On Air", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else
            Console.Error.WriteLine(message);
        return 1;
    }

    static int Usage()
    {
        Console.WriteLine("""
            OnAir - turns Govee outlets on while the microphone or camera is in use.

            Usage:
              OnAir                 Run in the notification area (right-click the icon to force a level)
              OnAir --stop          Stop the running app, turning the outlets off
              OnAir --status        Show which apps are using the microphone and camera right now
              OnAir --list-devices  List the Govee devices on your account
              OnAir --on | --off    Switch all configured outlets once, to test

            Settings are read from appsettings.json next to OnAir.exe; the log is OnAir.log.
            """);
        return 0;
    }

    static int RunTray(AppConfig config)
    {
        config.RequireApiKey();
        config.RequireOutlets();

        using var instance = new Mutex(false, OnAirService.InstanceName, out bool firstInstance);
        if (!firstInstance)
            return Fail("On Air is already running. Look for its icon in the notification area.", tray: true);

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(config));
        return 0;
    }

    static int Stop()
    {
        if (!OnAirService.SignalStop())
        {
            Console.WriteLine("OnAir is not running.");
            return 1;
        }
        Console.WriteLine("Stop signal sent.");
        return 0;
    }

    static int Status(AppConfig config)
    {
        using var devices = new DeviceMonitor();
        foreach (var device in Enum.GetValues<CaptureDevice>())
        {
            var apps = devices.GetInUseApps(device);
            if (apps.Count == 0)
            {
                Console.WriteLine($"{device}: not in use.");
                continue;
            }
            Console.WriteLine($"{device} in use by:");
            foreach (var app in apps)
                Console.WriteLine($"  {app}{(config.IsExcluded(app) ? "  (excluded)" : "")}");
        }
        return 0;
    }

    static async Task<int> ListDevicesAsync(AppConfig config)
    {
        using var govee = new GoveeClient(config.RequireApiKey());
        var devices = await govee.GetDevicesAsync(CancellationToken.None);
        if (devices.Count == 0)
        {
            Console.WriteLine("No devices found on this Govee account.");
            return 0;
        }
        foreach (var d in devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var power = d.Capabilities
                .Where(c => c.Type is GoveeClient.OnOffType or GoveeClient.ToggleType)
                .Select(c => c.Instance);
            Console.WriteLine($"\"{d.Name}\"  Sku: {d.Sku}  Device: {d.Device}");
            Console.WriteLine($"    type: {d.Type}  on/off instances: {string.Join(", ", power)}");
        }
        return 0;
    }

    static async Task<int> SetOnceAsync(AppConfig config, bool on)
    {
        config.RequireOutlets();
        using var govee = new GoveeClient(config.RequireApiKey());
        var outlets = new OutletController(govee, config.Outlets);
        await outlets.ResolveAsync(CancellationToken.None);
        bool ok = await outlets.SetAllAsync(on, CancellationToken.None);
        Console.WriteLine(ok ? $"Outlets turned {(on ? "on" : "off")}." : "One or more outlets failed.");
        return ok ? 0 : 1;
    }

    const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
