using System.Text.Json;

namespace OnAir;

sealed class ConfigException(string message) : Exception(message);

sealed class OutletConfig
{
    public string? Name { get; set; }
    public string? Sku { get; set; }
    public string? Device { get; set; }
    public string Instance { get; set; } = "powerSwitch";

    // Govee capability type; filled in from the device list, or defaulted from Instance.
    public string? Type { get; set; }

    public string Label => !string.IsNullOrWhiteSpace(Name) ? Name : $"{Sku} {Device}";
}

sealed class AppConfig
{
    public const string FileName = "appsettings.json";

    public string? GoveeApiKey { get; set; }
    public List<OutletConfig> Outlets { get; set; } = [];
    public double OnDelaySeconds { get; set; } = 1;
    public double OffDelaySeconds { get; set; } = 5;
    public double PollIntervalSeconds { get; set; } = 5;
    public List<string> ExcludeApps { get; set; } = [];
    public bool TurnOffOnExit { get; set; } = true;

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options) ?? new()
            : new();

    public string RequireApiKey()
    {
        var key = !string.IsNullOrWhiteSpace(GoveeApiKey) ? GoveeApiKey : Environment.GetEnvironmentVariable("GOVEE_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new ConfigException(
                $"No Govee API key. Set \"GoveeApiKey\" in {FileName} (next to OnAir.exe) or the GOVEE_API_KEY environment variable.\n" +
                "Request a key in the Govee Home app: Profile > Settings > Apply for API Key.");
        return key.Trim();
    }

    public void RequireOutlets()
    {
        if (Outlets.Count == 0)
            throw new ConfigException($"No outlets configured. Add them to \"Outlets\" in {FileName}.");
        foreach (var o in Outlets)
            if (string.IsNullOrWhiteSpace(o.Name) && (string.IsNullOrWhiteSpace(o.Sku) || string.IsNullOrWhiteSpace(o.Device)))
                throw new ConfigException($"Each outlet in {FileName} needs a \"Name\", or both \"Sku\" and \"Device\".");
    }

    public bool IsExcluded(string app) =>
        ExcludeApps.Any(e => !string.IsNullOrWhiteSpace(e) && app.Contains(e.Trim(), StringComparison.OrdinalIgnoreCase));
}
