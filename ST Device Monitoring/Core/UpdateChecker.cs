using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ST_Device_Monitoring.Core;

/// <summary>One file attached to a GitHub release.</summary>
public sealed class UpdateAsset
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public long Size { get; init; }

    /// <summary>SHA-256 as hex, when GitHub reports one for the asset. Used to verify the download.</summary>
    public string? Sha256 { get; init; }

    public string SizeText => Size >= 1024 * 1024
        ? $"{Size / 1024d / 1024d:0.#} MB"
        : $"{Size / 1024d:0} KB";

    /// <summary>True for the big exe that carries .NET with it.</summary>
    public bool IsSelfContained => Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                   !IsFrameworkDependent;

    /// <summary>True for the small exe that needs the .NET Desktop Runtime installed.</summary>
    public bool IsFrameworkDependent =>
        Name.Contains("netdesktop", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("framework", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("runtime", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the newest release on GitHub looks like.</summary>
public sealed class UpdateInfo
{
    public string Tag { get; init; } = string.Empty;
    public Version? Version { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public DateTimeOffset? Published { get; init; }
    public bool IsPreRelease { get; init; }
    public IReadOnlyList<UpdateAsset> Assets { get; init; } = Array.Empty<UpdateAsset>();

    /// <summary>The asset that matches how this copy of the program was built.</summary>
    public UpdateAsset? Recommended { get; init; }

    /// <summary>True when the release is newer than the running version.</summary>
    public bool IsNewer { get; init; }

    public string VersionText => Version?.ToString() ?? Tag;
}

/// <summary>
/// Looks for a newer release of the program on GitHub and downloads the exe from it.
///
/// It uses the public GitHub REST API - no token, no account and nothing installed. Only the
/// release list is read; the program never sends anything about the machine it runs on beyond the
/// user agent required by GitHub.
/// </summary>
public static class UpdateChecker
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ST-Device-Monitoring", AppInfo.Version));
        return client;
    }

    /// <summary>
    /// Which of the two published exe files this copy was built as. Written into the assembly by
    /// publish.cmd; a build straight from Visual Studio reports "development" and then the
    /// self-contained exe is offered, because that one runs everywhere.
    /// </summary>
    public static string BuildVariant
    {
        get
        {
            try
            {
                var value = Assembly.GetEntryAssembly()?
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => string.Equals(a.Key, "BuildVariant", StringComparison.OrdinalIgnoreCase))?
                    .Value;
                return string.IsNullOrWhiteSpace(value) ? "development" : value;
            }
            catch
            {
                return "development";
            }
        }
    }

    public static bool IsFrameworkDependentBuild =>
        BuildVariant.Contains("framework", StringComparison.OrdinalIgnoreCase) ||
        BuildVariant.Contains("netdesktop", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The running version, taken from AppInfo. It goes through the same parser as the release
    /// tag, so both have four parts - otherwise "1.29.0" would compare as older than the tag
    /// "v1.29.0" (Version treats a missing part as -1) and the program would offer to install
    /// the version it is already running, over and over.
    /// </summary>
    public static Version CurrentVersion => ParseVersion(AppInfo.Version) ?? new Version(0, 0, 0, 0);

    /// <summary>
    /// Reads the newest release. Returns null when nothing could be read - the reason is in
    /// <paramref name="error"/> and is meant to be shown to the user as it is.
    /// </summary>
    public static async Task<(UpdateInfo? info, string? error)> CheckAsync(
        string owner, string repository, bool includePreReleases, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
            return (null, "No GitHub repository is configured under Settings -> Updates.");

        var url = includePreReleases
            ? $"https://api.github.com/repos/{owner}/{repository}/releases?per_page=10"
            : $"https://api.github.com/repos/{owner}/{repository}/releases/latest";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (null, DescribeHttpFailure(response, owner, repository));

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var info = ParseResponse(json, includePreReleases);

            return info == null
                ? (null, "The repository has no releases yet.")
                : (info, null);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
        catch (Exception ex)
        {
            return (null, "Could not reach GitHub: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Turns a GitHub API answer into an <see cref="UpdateInfo"/>. Accepts both the single object
    /// from /releases/latest and the array from /releases. Kept separate from the HTTP call so the
    /// parsing can be checked against a saved response.
    /// </summary>
    public static UpdateInfo? ParseResponse(string json, bool includePreReleases)
    {
        using var document = JsonDocument.Parse(json);

        var release = document.RootElement.ValueKind == JsonValueKind.Array
            ? PickFirstRelease(document.RootElement, includePreReleases)
            : document.RootElement;

        return release.ValueKind == JsonValueKind.Object ? Parse(release) : null;
    }

    private static JsonElement PickFirstRelease(JsonElement array, bool includePreReleases)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!includePreReleases &&
                item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;
            return item;
        }
        return default;
    }

    private static UpdateInfo Parse(JsonElement release)
    {
        var tag = GetString(release, "tag_name");
        var version = ParseVersion(tag);

        var assets = new List<UpdateAsset>();
        if (release.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in list.EnumerateArray())
            {
                var name = GetString(asset, "name");
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                assets.Add(new UpdateAsset
                {
                    Name = name,
                    Url = GetString(asset, "browser_download_url"),
                    Size = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                    Sha256 = ParseDigest(GetString(asset, "digest"))
                });
            }
        }

        var title = GetString(release, "name");
        if (string.IsNullOrWhiteSpace(title)) title = tag;

        return new UpdateInfo
        {
            Tag = tag,
            Version = version,
            Title = title,
            Notes = GetString(release, "body"),
            HtmlUrl = GetString(release, "html_url"),
            IsPreRelease = release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
            Published = DateTimeOffset.TryParse(GetString(release, "published_at"), out var published)
                ? published
                : null,
            Assets = assets,
            Recommended = PickAsset(assets),
            IsNewer = version != null && version > CurrentVersion
        };
    }

    /// <summary>Picks the exe that matches this build - the self-contained one when in doubt.</summary>
    public static UpdateAsset? PickAsset(IReadOnlyList<UpdateAsset> assets)
    {
        if (assets.Count == 0) return null;

        if (IsFrameworkDependentBuild)
        {
            var small = assets.FirstOrDefault(a => a.IsFrameworkDependent);
            if (small != null) return small;
        }

        return assets.FirstOrDefault(a => a.IsSelfContained)
               ?? assets.OrderByDescending(a => a.Size).First();
    }

    /// <summary>"v1.30.0" and "1.30" both become a Version. Returns null for anything else.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var text = tag.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];

        // Strip a suffix like "-beta2" so a pre-release tag still compares.
        var dash = text.IndexOfAny(new[] { '-', '+', ' ' });
        if (dash > 0) text = text[..dash];

        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>Missing parts count as 0, so 1.30 and 1.30.0 are the same version.</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build, v.Revision < 0 ? 0 : v.Revision);

    private static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..].Trim()
            : null;
    }

    private static string GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string DescribeHttpFailure(HttpResponseMessage response, string owner, string repository)
    {
        var code = (int)response.StatusCode;
        return code switch
        {
            404 => $"No releases found for {owner}/{repository}. Check the repository name under " +
                   "Settings -> Updates, and that the repository is public.",
            403 when response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.Contains("0")
                => "GitHub's hourly limit for anonymous requests has been reached. Try again later.",
            403 => "GitHub refused the request (403). If the repository is private, updates cannot be " +
                   "fetched without a token.",
            _ => $"GitHub answered {code} {response.ReasonPhrase}."
        };
    }

    /// <summary>
    /// Downloads an asset to a temporary folder and verifies it. Returns the path to the file.
    /// Throws when the download is incomplete or the checksum does not match - a half-downloaded
    /// exe must never reach the update script.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateAsset asset, IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ST Device Monitoring", "update");
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, asset.Name);
        if (File.Exists(target)) File.Delete(target);

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.Size;
        var written = 0L;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                         81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                if (total > 0) progress?.Report(Math.Min(1.0, (double)written / total));
            }
        }

        if (asset.Size > 0 && written != asset.Size)
        {
            TryDelete(target);
            throw new IOException(
                $"The download stopped early ({written:N0} of {asset.Size:N0} bytes). Nothing was changed.");
        }

        if (!string.IsNullOrEmpty(asset.Sha256))
        {
            var actual = ComputeSha256(target);
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(target);
                throw new IOException(
                    "The downloaded file does not match the checksum GitHub reports for it. " +
                    "Nothing was changed.");
            }
        }

        progress?.Report(1.0);
        return target;
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
