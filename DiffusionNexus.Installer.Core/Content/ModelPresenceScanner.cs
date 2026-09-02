// DiffusionNexus.Installer.Core/Content/ModelPresenceScanner.cs
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services;
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
public sealed record ModelPresence(
    Guid ModelId,
    bool AllPartsPresent,
    string? ExistingPath,
    IReadOnlyList<ModelFileTarget> Targets);

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

        var overrides = new Dictionary<string, string>(request.FolderPathOverrides, StringComparer.OrdinalIgnoreCase);
        var results = new List<ModelPresence>();

        foreach (var model in request.Workload.ModelDownloads.Where(m => m.Enabled))
        {
            var targets = TargetsFor(request, model, overrides);
            var allPresent = targets.Count > 0 && targets.All(t => t.ExistingPath is not null);
            results.Add(new ModelPresence(model.Id, allPresent, allPresent ? targets[^1].ExistingPath : null, targets));
        }

        return results;
    }

    private static List<ModelFileTarget> TargetsFor(ModelScanRequest request, ModelDownload model, Dictionary<string, string> overrides)
    {
        var modelDestination = ModelDestinationResolver.Resolve(
            request.Workload, model, request.RepositoryPath, request.ModelBaseFolder, overrides);

        var enabledLinks = model.DownloadLinks.Where(l => l.Enabled).ToList();

        if (enabledLinks.Count == 0)
        {
            // The handler's fallback: the model's own URL, subject to the model-level tier.
            if (string.IsNullOrWhiteSpace(model.Url)) return [];

            if (request.SelectedVramGb > 0 && !PipelineVram.VramProfileFitsSelection(model.VramProfile, request.SelectedVramGb))
                return [];

            return Target(model, model.Url, modelDestination) is { } single ? [single] : [];
        }

        var links = PipelineVram.SelectBestMatchingLinks(enabledLinks, request.SelectedVramGb, null, model.Name);
        var targets = new List<ModelFileTarget>();

        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Url)) continue;

            var destination = string.IsNullOrWhiteSpace(link.Destination)
                ? modelDestination
                : ResolveLinkDestination(link.Destination, request.RepositoryPath);

            if (Target(model, link.Url, destination) is { } target) targets.Add(target);
        }

        return targets;
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

    private static ModelFileTarget? Target(ModelDownload model, string url, string destinationDirectory)
    {
        var fileName = FileNameFromUrl(url);
        if (fileName is null) return null;

        return new ModelFileTarget(model, url, destinationDirectory, fileName, FindFile(destinationDirectory, fileName));
    }

    private static string? FileNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>Exact path first, then any subfolder. Anything the filesystem refuses counts as absent.</summary>
    private static string? FindFile(string directory, string fileName)
    {
        try
        {
            var exact = Path.Combine(directory, fileName);
            if (File.Exists(exact)) return exact;
            if (!Directory.Exists(directory)) return null;

            return Directory.GetFiles(directory, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
