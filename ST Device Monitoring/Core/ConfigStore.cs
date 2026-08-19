using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Core;

/// <summary>Reads/writes devices.json next to the executable.</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        // Enums are written as text ("TcpPort", "RingBuffer") so devices.json stays readable
        // and hand-editable.
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ConfigPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "devices.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new AppConfig();

            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
            cfg.Devices ??= new List<DeviceConfig>();

            foreach (var d in cfg.Devices)
            {
                if (d.Id == Guid.Empty) d.Id = Guid.NewGuid();
                if (d.IntervalMs < 20) d.IntervalMs = 20;
                if (d.TimeoutMs < 20) d.TimeoutMs = 1000;
                if (d.FailThreshold < 1) d.FailThreshold = 1;
                if (d.MaxLoggedFailures < 0) d.MaxLoggedFailures = 0;
            }
            if (cfg.UiRefreshMs < 100) cfg.UiRefreshMs = 250;
            if (string.IsNullOrEmpty(cfg.CsvSeparator)) cfg.CsvSeparator = ";";
            return cfg;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not read the configuration ({ConfigPath}): {ex.Message}", ex);
        }
    }

    public static void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, Options);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Copy(tmp, ConfigPath, overwrite: true);
        File.Delete(tmp);
    }

    /// <summary>Absolute path to the log folder.</summary>
    public static string ResolveLogDirectory(AppConfig config)
    {
        var dir = string.IsNullOrWhiteSpace(config.LogDirectory) ? "Logs" : config.LogDirectory;
        return Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
    }
}
