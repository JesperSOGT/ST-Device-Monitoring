using System.Globalization;
using System.IO;
using System.Text;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Core;

/// <summary>Imports/exports the device list as a semicolon separated CSV file.</summary>
public static class DeviceImportExport
{
    private const string Separator = ";";

    private static readonly string[] Header =
    {
        "Name", "Host", "Group", "Mode", "Port", "IntervalMs", "DownIntervalMs", "TimeoutMs",
        "FailThreshold", "MaxLoggedFailures", "Enabled", "Community", "Description", "GroupMaster", "MacAddress"
    };

    public static void Export(string path, IEnumerable<DeviceConfig> devices)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"sep={Separator}");
        sb.AppendLine(string.Join(Separator, Header));

        foreach (var d in devices)
        {
            sb.AppendLine(string.Join(Separator,
                Escape(d.Name),
                Escape(d.Host),
                Escape(d.Group),
                d.Mode switch { CheckMode.TcpPort => "TCP", CheckMode.Snmp => "SNMP", _ => "ICMP" },
                d.Port.ToString(CultureInfo.InvariantCulture),
                d.IntervalMs.ToString(CultureInfo.InvariantCulture),
                d.DownIntervalMs.ToString(CultureInfo.InvariantCulture),
                d.TimeoutMs.ToString(CultureInfo.InvariantCulture),
                d.FailThreshold.ToString(CultureInfo.InvariantCulture),
                d.MaxLoggedFailures.ToString(CultureInfo.InvariantCulture),
                d.Enabled ? "1" : "0",
                Escape(d.Community),
                Escape(d.Description),
                d.IsGroupMaster ? "1" : "0",
                Escape(d.MacAddress)));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public sealed class ImportResult
    {
        public List<DeviceConfig> Devices { get; } = new();
        public List<string> Errors { get; } = new();
        public int SkippedDuplicates { get; set; }
    }

    /// <summary>
    /// Reads a CSV file. Accepts ; or , as separator and ignores a leading "sep=" line.
    /// Devices whose endpoint already exists in <paramref name="existing"/> are skipped.
    /// </summary>
    public static ImportResult Import(string path, IEnumerable<DeviceConfig> existing)
    {
        var result = new ImportResult();
        var known = new HashSet<string>(existing.Select(d => d.Endpoint), StringComparer.OrdinalIgnoreCase);

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var separator = Separator;
        var headerSeen = false;
        var lineNumber = 0;

        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("sep=", StringComparison.OrdinalIgnoreCase))
            {
                separator = line.Substring(4);
                continue;
            }

            if (!headerSeen)
            {
                if (!line.Contains(';') && line.Contains(',')) separator = ",";
                if (line.StartsWith("Name", StringComparison.OrdinalIgnoreCase))
                {
                    headerSeen = true;
                    continue;
                }
                headerSeen = true; // no header - treat this line as data
            }

            var parts = SplitCsv(line, separator);
            if (parts.Count < 2)
            {
                result.Errors.Add($"Line {lineNumber}: too few fields.");
                continue;
            }

            var device = new DeviceConfig
            {
                Name = parts[0].Trim(),
                Host = parts[1].Trim(),
                Group = Get(parts, 2),
                Mode = Get(parts, 3).ToUpperInvariant() switch
                {
                    "TCP" => CheckMode.TcpPort,
                    "SNMP" => CheckMode.Snmp,
                    _ => CheckMode.Icmp
                },
                Port = GetInt(parts, 4, 502),
                IntervalMs = GetInt(parts, 5, 1000),
                DownIntervalMs = GetInt(parts, 6, 1000),
                TimeoutMs = GetInt(parts, 7, 1000),
                FailThreshold = GetInt(parts, 8, 1),
                MaxLoggedFailures = GetInt(parts, 9, 5),
                Enabled = GetBool(parts, 10, true),
                Community = Get(parts, 11) is { Length: > 0 } community ? community : "public",
                Description = Get(parts, 12),
                IsGroupMaster = GetBool(parts, 13, false),
                MacAddress = Get(parts, 14)
            };

            if (device.Mode == CheckMode.Snmp && device.Port == 502) device.Port = 161;
            if (string.IsNullOrWhiteSpace(device.Name)) device.Name = device.Host;

            var error = device.Validate();
            if (error != null)
            {
                result.Errors.Add($"Line {lineNumber} ({device.Host}): {error}");
                continue;
            }

            if (!known.Add(device.Endpoint))
            {
                result.SkippedDuplicates++;
                continue;
            }

            result.Devices.Add(device);
        }

        return result;
    }

    private static string Get(IReadOnlyList<string> parts, int index)
        => index < parts.Count ? parts[index].Trim() : string.Empty;

    private static int GetInt(IReadOnlyList<string> parts, int index, int fallback)
        => index < parts.Count && int.TryParse(parts[index].Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool GetBool(IReadOnlyList<string> parts, int index, bool fallback)
    {
        var text = Get(parts, index);
        if (text.Length == 0) return fallback;
        if (bool.TryParse(text, out var b)) return b;
        return text is "1" or "yes" or "ja" or "x" or "X";
    }

    private static List<string> SplitCsv(string line, string separator)
    {
        var sep = string.IsNullOrEmpty(separator) ? ';' : separator[0];
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == sep) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var needsQuotes = value.Contains(Separator, StringComparison.Ordinal)
                          || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
