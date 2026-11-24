using System.Text.Json;
using System.Text.Json.Serialization;
using DNSManagerPro.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Infrastructure;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. Writes are atomic; a corrupt file is set aside and defaults are used.</summary>
public sealed class SettingsService(string path, ILogger<SettingsService> logger)
{
    /// <summary>Settings files are tiny; anything larger is not a settings file and is refused before parsing.</summary>
    public const long MaxFileBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

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
                Current = ReadFile(path);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
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
            Write(next);
        }

        Changed?.Invoke(next);
    }

    /// <summary>Copies the current settings to a file the user chose.</summary>
    public void ExportTo(string destination)
    {
        AppSettings snapshot;
        lock (_gate)
        {
            snapshot = Current;
        }

        File.WriteAllText(destination, JsonSerializer.Serialize(snapshot, Json));
    }

    /// <summary>Validates and applies a settings file. Throws (and changes nothing) when the file is not valid settings JSON.</summary>
    public void ImportFrom(string source)
    {
        var imported = ReadFile(source);
        Update(_ => imported);
    }

    public void ResetToDefaults() => Update(_ => new AppSettings());

    private static AppSettings ReadFile(string file)
    {
        var info = new FileInfo(file);
        if (info.Length > MaxFileBytes)
        {
            throw new InvalidDataException("The file is too large to be a settings file.");
        }

        var parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("The file is empty.");
        return parsed.Sanitized();
    }

    private void Write(AppSettings value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
