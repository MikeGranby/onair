namespace OnAir;

/// <summary>Switches the configured Govee outlets.</summary>
sealed class OutletController(GoveeClient govee, IReadOnlyList<OutletConfig> outlets)
{
    public bool IsResolved { get; private set; }

    /// <summary>Fills in Sku/Device/Type for outlets configured by name, using the account's device list.</summary>
    public async Task ResolveAsync(CancellationToken ct)
    {
        if (IsResolved)
            return;

        if (outlets.Any(o => string.IsNullOrWhiteSpace(o.Sku) || string.IsNullOrWhiteSpace(o.Device)))
        {
            var devices = await govee.GetDevicesAsync(ct);
            foreach (var o in outlets)
            {
                bool byName = string.IsNullOrWhiteSpace(o.Sku) || string.IsNullOrWhiteSpace(o.Device);
                var match = byName
                    ? devices.FirstOrDefault(d => d.Name.Equals(o.Name?.Trim(), StringComparison.OrdinalIgnoreCase))
                    : devices.FirstOrDefault(d => d.Device.Equals(o.Device, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    if (!byName)
                        continue;
                    throw new ConfigException(
                        $"No Govee device named \"{o.Name}\". Devices on this account: " +
                        string.Join(", ", devices.Select(d => $"\"{d.Name}\"")));
                }
                o.Sku = match.Sku;
                o.Device = match.Device;
                o.Type ??= match.Capabilities.FirstOrDefault(c => c.Instance == o.Instance)?.Type;
            }
        }

        foreach (var o in outlets)
            o.Type ??= o.Instance == "powerSwitch" ? GoveeClient.OnOffType : GoveeClient.ToggleType;

        IsResolved = true;
    }

    /// <summary>Turns every outlet on or off. Returns false if any outlet failed.</summary>
    public Task<bool> SetAllAsync(bool on, CancellationToken ct) => SetAsync(outlets, on, ct);

    /// <summary>Turns the given outlets on or off. Returns false if any outlet failed.</summary>
    public async Task<bool> SetAsync(IEnumerable<OutletConfig> targets, bool on, CancellationToken ct)
    {
        var results = await Task.WhenAll(targets.Select(async o =>
        {
            try
            {
                await govee.SetPowerAsync(o, on, ct);
                return true;
            }
            catch (Exception ex) when (ex is GoveeException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                Log.Write($"  {o.Label}: failed to turn {(on ? "on" : "off")}: {ex.Message}");
                return false;
            }
        }));
        return results.All(ok => ok);
    }
}
