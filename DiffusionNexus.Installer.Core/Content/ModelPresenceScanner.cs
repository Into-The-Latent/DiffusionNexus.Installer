// DiffusionNexus.Installer.Core/Content/ModelPresenceScanner.cs
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Helpers;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using PipelineVram = DiffusionNexus.Installer.SDK.Services.Installation.Utilities.VramProfileHelper;

namespace DiffusionNexus.Installer.Core.Content;

/// <param name="RepositoryPath">Where the main repository will be — see <see cref="RepositoryPaths"/>.</param>
/// <param name="SelectedVramGb">0 means no tier filtering, exactly as the SDK reads it.</param>
public sealed record ModelScanRequest(
    InstallationConfiguration Workload,
    string RepositoryPath,
    string? ModelBaseFolder,
    IReadOnlyDictionary<string, string> FolderPathOverrides,
    int SelectedVramGb);

/// <summary>One file the install would write for a model, and whether it is already there.</summary>
public sealed record ModelFileTarget(
    ModelDownload Model,
    string Url,
    string DestinationDirectory,
    string FileName,
    string? ExistingPath);

/// <param name="AllPartsPresent">True only when every target's file exists — a half-downloaded multi-link model is not "already downloaded".</param>
/// <param name="UnresolvableLinks">
/// Links the pipeline will download whose file name is only known once the server answers
/// (no extension in the URL). They cannot be scanned, so a model with any is never "all present".
/// </param>
public sealed record ModelPresence(
    Guid ModelId,
    bool AllPartsPresent,
    string? ExistingPath,
    IReadOnlyList<ModelFileTarget> Targets,
    int UnresolvableLinks = 0);

public interface IModelPresenceScanner
{
    /// <summary>One entry per enabled model, in catalog order. Never throws on filesystem trouble.</summary>
    IReadOnlyList<ModelPresence> Scan(ModelScanRequest request);
}

/// <summary>
/// Resolves, for each enabled model, the files the pipeline would write at the selected tier and
/// whether they already exist. 1.x carried this logic twice under a "KEEP IN LOCKSTEP" comment —
/// once for the "already downloaded" markers, once for pre-install verification. Both read this.
/// <para>
/// Mirrors ModelDownloadStepHandler exactly: destination via ModelDestinationResolver, link
/// selection via the Services VramProfileHelper (the class the handler itself calls), per-link
/// destination overrides via the same placeholder rules as the handler's ResolvePath.
/// </para>
/// </summary>
public sealed class ModelPresenceScanner : IModelPresenceScanner
{
    public IReadOnlyList<ModelPresence> Scan(ModelScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // TryAdd, not the dictionary constructor: two override keys differing only by case would
        // otherwise throw on construction instead of just letting the first one win.
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.FolderPathOverrides)
            overrides.TryAdd(key, value);

        // One recursive listing per destination directory for this whole scan, not one per missing
        // target -- a large model library and a workload with dozens of links would otherwise walk
        // the same tree over and over on every tier change.
        var directoryCache = new DirectoryListingCache();
        var results = new List<ModelPresence>();

        foreach (var model in request.Workload.ModelDownloads.Where(m => m.Enabled))
        {
            var (targets, unresolvable) = TargetsFor(request, model, overrides, directoryCache);
            // Over ALL links the pipeline will download, not just the scannable ones: a model with
            // one file on disk and one name-unknown link still has a download ahead of it.
            var allPresent = targets.Count > 0 && unresolvable == 0 && targets.All(t => t.ExistingPath is not null);
            results.Add(new ModelPresence(model.Id, allPresent, allPresent ? targets[^1].ExistingPath : null, targets, unresolvable));
        }

        return results;
    }

    private static (List<ModelFileTarget> Targets, int Unresolvable) TargetsFor(
        ModelScanRequest request, ModelDownload model, Dictionary<string, string> overrides, DirectoryListingCache directoryCache)
    {
        var modelDestination = ModelDestinationResolver.Resolve(
            request.Workload, model, request.RepositoryPath, request.ModelBaseFolder, overrides);

        var enabledLinks = model.DownloadLinks.Where(l => l.Enabled).ToList();

        if (enabledLinks.Count == 0)
        {
            // The handler's fallback: the model's own URL, subject to the model-level tier.
            if (string.IsNullOrWhiteSpace(model.Url)) return ([], 0);

            if (request.SelectedVramGb > 0 && !PipelineVram.VramProfileFitsSelection(model.VramProfile, request.SelectedVramGb))
                return ([], 0);

            return Target(model, model.Url, modelDestination, directoryCache) is { } single ? ([single], 0) : ([], 1);
        }

        var links = PipelineVram.SelectBestMatchingLinks(enabledLinks, request.SelectedVramGb, null, model.Name);
        var targets = new List<ModelFileTarget>();
        var unresolvable = 0;

        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Url)) continue;

            var destination = string.IsNullOrWhiteSpace(link.Destination)
                ? modelDestination
                : ResolveLinkDestination(link.Destination, request.RepositoryPath);

            if (Target(model, link.Url, destination, directoryCache) is { } target) targets.Add(target);
            else unresolvable++;
        }

        return (targets, unresolvable);
    }

    /// <summary>Mirrors ModelDownloadStepHandler.ResolvePath: rooted as-is, placeholders, else under the repository.</summary>
    private static string ResolveLinkDestination(string path, string repositoryPath)
    {
        if (Path.IsPathRooted(path)) return path;

        var resolved = path
            .Replace("{RepositoryPath}", repositoryPath)
            .Replace("{Repository}", repositoryPath);

        return Path.IsPathRooted(resolved) ? resolved : Path.Combine(repositoryPath, resolved);
    }

    private static ModelFileTarget? Target(ModelDownload model, string url, string destinationDirectory, DirectoryListingCache directoryCache)
    {
        var fileName = FileNameFromUrl(url);
        if (fileName is null) return null;

        return new ModelFileTarget(model, url, destinationDirectory, fileName, directoryCache.FindFile(destinationDirectory, fileName));
    }

    private static string? FileNameFromUrl(string url)
    {
        // The SDK's own rule, over the SDK's own normalization (HF /blob/ -> /resolve/), so the
        // scanner names files exactly as FileDownloader will. A name with no extension means the
        // real name only arrives with the server's Content-Disposition at download time; such a file
        // cannot be located or verified before the download, so it yields no target and the row
        // honestly carries no marker.
        var name = FileDownloader.GetFileNameFromUrl(DownloadUrlNormalizer.Normalize(url));
        if (string.IsNullOrWhiteSpace(name) || !name.Contains('.')) return null;
        return name;
    }

    /// <summary>
    /// Caches one recursive directory listing per destination directory for the lifetime of a
    /// single <see cref="Scan"/> call. Matching is by exact filename equality (not a search
    /// pattern), so a filename containing '*' or '?' can never act as a wildcard against other
    /// files. Anything the filesystem refuses counts as absent, and the failure itself is cached so
    /// a repeatedly-unreadable directory is not retried for every remaining target.
    /// </summary>
    /// <summary>
    /// The cache key for a destination directory: the full path without a trailing separator,
    /// except for a drive root, which keeps it. Trimming "D:\" to "D:" would make a drive-RELATIVE
    /// path that means "the current directory on D:", not the root.
    /// </summary>
    public static string NormalizeDirectoryKey(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(full);
            if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase)) return full;
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return directory;
        }
    }

    private sealed class DirectoryListingCache
    {
        private readonly Dictionary<string, string[]> _listings = new(StringComparer.OrdinalIgnoreCase);

        public string? FindFile(string directory, string fileName)
        {
            try
            {
                var exact = Path.Combine(directory, fileName);
                if (File.Exists(exact)) return exact;
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                return null;
            }

            var files = ListDirectory(directory);
            return files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        }

        private string[] ListDirectory(string directory)
        {
            // Keyed by the normalized full path: "...\loras" and "...\loras\" are one walk, not two.
            // The KEY only; the listing itself uses the directory as given.
            var key = NormalizeDirectoryKey(directory);
            if (_listings.TryGetValue(key, out var cached)) return cached;

            string[] files;
            try
            {
                files = Directory.Exists(directory)
                    ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                    : [];
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                files = [];
            }

            _listings[key] = files;
            return files;
        }

        private static bool IsFileSystemException(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or System.Security.SecurityException;
    }
}
