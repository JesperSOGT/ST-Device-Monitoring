using System.Net;

namespace ST_Device_Monitoring.Core;

/// <summary>
/// Validation of what the user types in the "IP address / hostname" fields.
/// Anything that only contains digits and dots is treated as an attempted IPv4 address and is
/// validated strictly (four octets, 0-255, no leading zeros) instead of being silently accepted
/// as a hostname - so a typo like 192.168.1.300 is reported instead of turning into a DNS lookup.
/// </summary>
public static class NetworkValidation
{
    /// <summary>True when the text is meant to be an IPv4 address (only digits and dots).</summary>
    public static bool LooksLikeIPv4(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var c in text.Trim())
            if (!char.IsAsciiDigit(c) && c != '.')
                return false;
        return true;
    }

    /// <summary>Strict IPv4 check: exactly four octets, each 0-255 and without leading zeros.</summary>
    public static bool TryParseIPv4(string text, out IPAddress address, out string? error)
    {
        address = IPAddress.None;
        var value = (text ?? string.Empty).Trim();

        if (value.Length == 0)
        {
            error = "IP address is empty.";
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            error = $"\"{value}\" is not a valid IPv4 address - it must have four parts, e.g. 192.168.1.50.";
            return false;
        }

        var bytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            var part = parts[i];

            if (part.Length == 0)
            {
                error = $"\"{value}\" is not a valid IPv4 address - part {i + 1} is empty.";
                return false;
            }
            if (part.Length > 3)
            {
                error = $"\"{value}\" is not a valid IPv4 address - part {i + 1} has too many digits.";
                return false;
            }
            foreach (var c in part)
            {
                if (!char.IsAsciiDigit(c))
                {
                    error = $"\"{value}\" is not a valid IPv4 address - part {i + 1} contains \"{c}\".";
                    return false;
                }
            }
            if (part.Length > 1 && part[0] == '0')
            {
                error = $"\"{value}\" is not a valid IPv4 address - part {i + 1} must not start with 0.";
                return false;
            }
            if (!int.TryParse(part, out var number) || number > 255)
            {
                error = $"\"{value}\" is not a valid IPv4 address - part {i + 1} must be between 0 and 255.";
                return false;
            }
            bytes[i] = (byte)number;
        }

        address = new IPAddress(bytes);
        error = null;
        return true;
    }

    /// <summary>
    /// Validates an IP address or a hostname. Returns null when it is usable,
    /// otherwise a message that can be shown to the user.
    /// </summary>
    public static string? ValidateHost(string host)
    {
        var value = (host ?? string.Empty).Trim();
        if (value.Length == 0) return "IP address / hostname is required.";

        if (LooksLikeIPv4(value))
            return TryParseIPv4(value, out _, out var error) ? null : error;

        // IPv6 in brackets or with colons is accepted as-is if .NET can parse it.
        if (value.Contains(':'))
            return IPAddress.TryParse(value.Trim('[', ']'), out _)
                ? null
                : $"\"{value}\" is not a valid IP address.";

        if (value.Length > 253) return "The hostname is too long (maximum 253 characters).";

        foreach (var label in value.Split('.'))
        {
            if (label.Length == 0)
                return $"\"{value}\" is not a valid hostname - it contains an empty part.";
            if (label.Length > 63)
                return $"\"{value}\" is not a valid hostname - a part may be at most 63 characters.";
            if (label.StartsWith('-') || label.EndsWith('-'))
                return $"\"{value}\" is not a valid hostname - a part must not start or end with \"-\".";
            foreach (var c in label)
                if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                    return $"\"{value}\" is not a valid hostname - \"{c}\" is not allowed.";
        }

        return null;
    }

    /// <summary>Validates a from/to IPv4 range. Returns null when it is usable.</summary>
    public static string? ValidateRange(string fromText, string toText, int maxAddresses = 4096)
    {
        if (!TryParseIPv4(fromText, out var from, out var fromError)) return "From: " + fromError;
        if (!TryParseIPv4(toText, out var to, out var toError)) return "To: " + toError;

        var start = HostScanner.ToUInt(from);
        var end = HostScanner.ToUInt(to);
        if (end < start) return "The \"to\" address must be higher than the \"from\" address.";

        var count = end - start + 1;
        if (count > maxAddresses)
            return $"The range covers {count:N0} addresses - at most {maxAddresses:N0} can be scanned at a time.";

        return null;
    }
}
