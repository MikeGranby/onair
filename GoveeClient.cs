using System.Net.Http.Json;
using System.Text.Json;

namespace OnAir;

sealed record GoveeCapability(string Type, string Instance);

sealed record GoveeDevice(string Sku, string Device, string Name, string Type, IReadOnlyList<GoveeCapability> Capabilities);

sealed class GoveeException(string message) : Exception(message);

/// <summary>Minimal client for the Govee Developer Platform API (openapi.api.govee.com).</summary>
sealed class GoveeClient : IDisposable
{
    public const string OnOffType = "devices.capabilities.on_off";
    public const string ToggleType = "devices.capabilities.toggle";

    readonly HttpClient _http;

    public GoveeClient(string apiKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://openapi.api.govee.com/router/api/v1/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Add("Govee-API-Key", apiKey);
    }

    public async Task<IReadOnlyList<GoveeDevice>> GetDevicesAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "user/devices"), ct);
        var devices = new List<GoveeDevice>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in data.EnumerateArray())
            {
                var capabilities = new List<GoveeCapability>();
                if (d.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                    foreach (var c in caps.EnumerateArray())
                        capabilities.Add(new(Str(c, "type"), Str(c, "instance")));
                devices.Add(new(Str(d, "sku"), Str(d, "device"), Str(d, "deviceName"), Str(d, "type"), capabilities));
            }
        }
        return devices;
    }

    public async Task SetPowerAsync(OutletConfig outlet, bool on, CancellationToken ct)
    {
        var body = new
        {
            requestId = Guid.NewGuid().ToString(),
            payload = new
            {
                sku = outlet.Sku,
                device = outlet.Device,
                capability = new { type = outlet.Type, instance = outlet.Instance, value = on ? 1 : 0 },
            },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "device/control") { Content = JsonContent.Create(body) };
        using var _ = await SendAsync(request, ct);
    }

    async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await _http.SendAsync(request, ct))
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(text); } catch (JsonException) { }

            // Govee reports errors both as HTTP status and as a "code" field in the body.
            int code = doc != null && doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : (int)response.StatusCode;
            if (response.IsSuccessStatusCode && code == 200 && doc != null)
                return doc;

            string message = text;
            if (doc != null && doc.RootElement.ValueKind == JsonValueKind.Object)
                message = Str(doc.RootElement, "message") is { Length: > 0 } m ? m
                    : Str(doc.RootElement, "msg") is { Length: > 0 } m2 ? m2
                    : text;
            if (string.IsNullOrWhiteSpace(message))
                message = response.ReasonPhrase ?? "";
            doc?.Dispose();
            throw new GoveeException($"Govee API error {code}: {message}");
        }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    public void Dispose() => _http.Dispose();
}
