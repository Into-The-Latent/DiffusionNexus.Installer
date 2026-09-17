using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>Reads the app's GitHub releases feed. A seam so the checker's tests need no network.</summary>
public interface IAppReleaseFeed
{
    Task<string> ReadAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// releases.atom rather than the REST API: it is the document electron-updater itself reads, it
/// lists pre-releases and never drafts, and it has no 60-an-hour anonymous rate limit to run
/// out of behind a shared address.
/// </summary>
public sealed class GitHubAppReleaseFeed : IAppReleaseFeed, IDisposable
{
    // Its own client, never the container's: AddInstallationServices registers one with an
    // infinite timeout for model downloads, and this sits in front of every Preview check.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public GitHubAppReleaseFeed() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("DiffusionNexus-Installer/3");

    public Task<string> ReadAsync(string url, CancellationToken ct = default) => _http.GetStringAsync(url, ct);

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// The two tags that matter in a releases feed. <see cref="First"/> is the most recently created
/// release, which is the one electron-updater's Preview path takes; <see cref="Highest"/> is the
/// one Preview is supposed to mean. They differ when a Stable hotfix is cut after a pre-release.
/// </summary>
public sealed partial record AppReleaseTags(string? First, string? Highest)
{
    public static AppReleaseTags Parse(string atomXml)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        string? first = null, highest = null;
        Version? highestVersion = null;

        foreach (var entry in XDocument.Parse(atomXml).Root?.Elements(atom + "entry") ?? [])
        {
            var href = entry.Elements(atom + "link").Select(l => (string?)l.Attribute("href")).FirstOrDefault(h => h is not null);
            var match = href is null ? Match.Empty : TagInHref().Match(href);
            if (!match.Success) continue;

            var tag = Uri.UnescapeDataString(match.Groups[1].Value);
            first ??= tag;

            // Plain X.Y.Z only. App versions never carry a suffix (see AppUpdateChecker), so a tag
            // that does is not one of ours to offer.
            if (PlainVersion().IsMatch(tag) && Version.TryParse(tag.TrimStart('v'), out var version)
                && (highestVersion is null || version > highestVersion))
            {
                highestVersion = version;
                highest = tag;
            }
        }
        return new AppReleaseTags(first, highest);
    }

    [GeneratedRegex(@"/releases/tag/([^/?#]+)$")]
    private static partial Regex TagInHref();

    [GeneratedRegex(@"^v?\d+\.\d+\.\d+$")]
    private static partial Regex PlainVersion();
}

/// <summary>
/// What the checker needs from electron-builder's <c>app-update.yml</c>: where the releases live
/// and the download cache folder. Read from the shipped file rather than repeated as constants,
/// so a repository rename cannot leave this pointing at the old one.
/// </summary>
public sealed record AppUpdateConfig(string Owner, string Repo, string? CacheDirName)
{
    public string FeedUrl => $"https://github.com/{Owner}/{Repo}/releases.atom";

    /// <summary>Null unless this is a GitHub config naming both owner and repo. The file is flat key: value pairs.</summary>
    public static AppUpdateConfig? TryParse(string yaml)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in yaml.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            values[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('\'', '"');
        }

        if (values.GetValueOrDefault("provider") != "github") return null;
        if (!values.TryGetValue("owner", out var owner) || owner.Length == 0) return null;
        if (!values.TryGetValue("repo", out var repo) || repo.Length == 0) return null;
        return new AppUpdateConfig(owner, repo, values.GetValueOrDefault("updaterCacheDirName"));
    }

    /// <summary>
    /// A config that shows the updater exactly one release: the generic provider reads
    /// <c>latest.yml</c> from the URL and resolves the installer beside it, which is what a
    /// GitHub release's download folder is.
    /// </summary>
    public string PinnedTo(string tag)
    {
        var yaml = $"provider: generic\nurl: https://github.com/{Owner}/{Repo}/releases/download/{Uri.EscapeDataString(tag)}\n";
        if (!string.IsNullOrEmpty(CacheDirName)) yaml += $"updaterCacheDirName: '{CacheDirName.Replace("'", "''")}'\n";
        return yaml;
    }
}
