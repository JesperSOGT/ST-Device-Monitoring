using System.IO;

namespace ST_Device_Monitoring.Core;

/// <summary>
/// Turns a MAC address into a manufacturer name - the same "Deif_09:eb:a2" resolution Wireshark
/// does. The names come from an IEEE OUI list, which is not shipped with this application:
///
///   1. oui.csv / manuf / oui.txt placed next to the program file - the button in the discovery
///      window downloads the official list from IEEE (standards-oui.ieee.org) when the machine
///      has internet access,
///   2. Wireshark's own "manuf" file, if Wireshark is installed on the machine,
///   3. Nmap's "nmap-mac-prefixes" also works if it is copied in as oui.txt.
///
/// If nothing is found the column simply stays empty - nothing else is affected.
/// </summary>
public static class MacVendorLookup
{
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    /// <summary>The file the names were read from, for display in the user interface.</summary>
    public static string? SourceFile { get; private set; }

    public static int Count
    {
        get { EnsureLoaded(); return Vendors.Count; }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "oui.csv");
        yield return Path.Combine(AppContext.BaseDirectory, "manuf");
        yield return Path.Combine(AppContext.BaseDirectory, "manuf.txt");
        yield return Path.Combine(AppContext.BaseDirectory, "oui.txt");
        yield return @"C:\Program Files\Wireshark\manuf";
        yield return @"C:\Program Files (x86)\Wireshark\manuf";
    }

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        foreach (var path in CandidatePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                Load(path);
                if (Vendors.Count > 0)
                {
                    SourceFile = path;
                    return;
                }
            }
            catch
            {
                // an unreadable file is not worth an error - just try the next one
            }
        }
    }

    /// <summary>Reloads the list, e.g. after the user has dropped a new file next to the program.</summary>
    public static void Reload()
    {
        Vendors.Clear();
        SourceFile = null;
        _loaded = false;
        EnsureLoaded();
    }

    /// <summary>
    /// Reads Wireshark's "manuf" format ("00:26:77 Short Long name") and the IEEE oui.txt format
    /// ("00-26-77   (hex)   Company name").
    /// </summary>
    private static void Load(string path)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // IEEE oui.csv: Registry,Assignment,Organization Name,Organization Address
            if (line.StartsWith("MA-L,", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("MA-M,", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("MA-S,", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("IAB,", StringComparison.OrdinalIgnoreCase))
            {
                var fields = SplitCsvLine(line);
                if (fields.Count < 3) continue;
                var csvPrefix = Normalize(fields[1]);
                var organisation = fields[2].Trim().Trim('"');
                if (csvPrefix.Length == 6 && organisation.Length > 0) Vendors[csvPrefix] = organisation;
                continue;
            }
            if (line.StartsWith("Registry,", StringComparison.OrdinalIgnoreCase)) continue;   // csv header

            // IEEE oui.txt
            var hexIndex = line.IndexOf("(hex)", StringComparison.OrdinalIgnoreCase);
            if (hexIndex > 0)
            {
                var prefix = Normalize(line[..hexIndex].Trim());
                var name = line[(hexIndex + 5)..].Trim();
                if (prefix.Length == 6 && name.Length > 0) Vendors[prefix] = name;
                continue;
            }

            // Wireshark manuf: prefix <tab> short name <tab> long name
            var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var mask = parts[0];
            if (mask.Contains('/')) continue;             // /28 and /36 blocks are skipped

            var key = Normalize(mask);
            if (key.Length != 6) continue;

            // Prefer the long name when there is one.
            var vendor = parts.Length >= 3 ? string.Join(' ', parts.Skip(2)) : parts[1];
            Vendors[key] = vendor.Trim();
        }
    }

    /// <summary>Splits one CSV line, honouring quoted fields (company names contain commas).</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>
    /// Downloads the official IEEE OUI list and saves it as oui.csv next to the program, so the
    /// manufacturer column works without Wireshark. Needs internet access once - after that the
    /// file is read locally.
    /// </summary>
    public static async Task<(bool ok, string message)> DownloadAsync(CancellationToken ct = default)
    {
        const string url = "https://standards-oui.ieee.org/oui/oui.csv";
        var path = Path.Combine(AppContext.BaseDirectory, "oui.csv");

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var csv = await http.GetStringAsync(url, ct).ConfigureAwait(false);
            if (csv.Length < 1000) return (false, "The downloaded list looks empty.");

            await File.WriteAllTextAsync(path, csv, ct).ConfigureAwait(false);
            Reload();

            return Count > 0
                ? (true, $"{Count:N0} manufacturers downloaded to {path}")
                : (false, "The list was downloaded but could not be read.");
        }
        catch (Exception ex)
        {
            return (false, "Download failed: " + ex.GetBaseException().Message +
                           "\nThe file can also be copied in by hand as oui.csv, or taken from a machine " +
                           "with Wireshark (its \"manuf\" file).");
        }
    }

    private static string Normalize(string text)
    {
        Span<char> buffer = stackalloc char[12];
        var count = 0;
        foreach (var c in text)
        {
            if (Uri.IsHexDigit(c))
            {
                if (count == 12) return string.Empty;
                buffer[count++] = char.ToUpperInvariant(c);
            }
        }
        return count >= 6 ? new string(buffer[..6]) : string.Empty;
    }

    /// <summary>Manufacturer for a MAC like "00-26-77-09-EB-A2", or "" when it is unknown.</summary>
    public static string Lookup(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return string.Empty;
        EnsureLoaded();

        var key = Normalize(mac);
        if (key.Length != 6) return string.Empty;

        return Vendors.TryGetValue(key, out var vendor) ? vendor : string.Empty;
    }
}
