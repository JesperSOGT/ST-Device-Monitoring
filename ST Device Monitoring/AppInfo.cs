namespace ST_Device_Monitoring;

/// <summary>
/// Central place for product name, version and build date shown in the About dialog,
/// the help window and the status bar. Update Version and BuildDate on every code change.
/// </summary>
public static class AppInfo
{
    public const string ProductName = "ST Device Monitoring";
    public const string Version = "1.30.0";
    public const string BuildDate = "2026-08-19";
    public const string Author = "Jesper Bødewadt Møller";

    /// <summary>GitHub account the releases are published under - used by the updater.</summary>
    public const string RepositoryOwner = "JesperSOGT";

    /// <summary>GitHub repository name - used by the updater.</summary>
    public const string RepositoryName = "ST-Device-Monitoring";

    public static string RepositoryUrl => $"https://github.com/{RepositoryOwner}/{RepositoryName}";

    public static string ReleasesUrl => $"{RepositoryUrl}/releases";

    /// <summary>e.g. "v1.25.0 · built 2026-08-19"</summary>
    public static string VersionLine => $"v{Version} · built {BuildDate}";

    public static string Copyright => $"Developed by {Author}. All rights reserved.";
}
