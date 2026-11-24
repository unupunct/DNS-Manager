using System.Text.Json;
using DNSManagerPro.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Infrastructure;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. Writes are atomic; a corrupt file is set aside and defaults are used.</summary>
public sealed class SettingsService(string path, ILogger<SettingsService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();

    public event Action<AppSettings>? Changed;

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                Current = new AppSettings();
                return;
            }

            try
            {
                Current = (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings()).Sanitized();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Settings file unreadable; using defaults");
                try
                {
                    File.Move(path, path + ".bad", overwrite: true);
                }
                catch (IOException)
                {
                    // best effort
                }

                Current = new AppSettings();
            }
        }
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings next;
        lock (_gate)
        {
            next = change(Current).Sanitized();
            Current = next;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(next, Json));
            File.Move(tmp, path, overwrite: true);
        }

        Changed?.Invoke(next);
    }
}
