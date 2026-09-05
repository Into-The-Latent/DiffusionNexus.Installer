using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Content;

/// <param name="ExistingModelIds">Models already on disk; their downloads are not counted.</param>
/// <param name="ModelBaseFolder">The custom model library, when set: models land there, on its drive, not on the install drive.</param>
public sealed record DiskSpaceRequest(
    InstallationConfiguration Workload,
    string TargetFolder,
    int SelectedVramGb,
    HashSet<Guid> ExcludedModelIds,
    HashSet<Guid> ExistingModelIds,
    string? ModelBaseFolder = null);

/// <param name="AvailableBytes">Free space on the install drive; 0 when <paramref name="AvailableKnown"/> is false.</param>
/// <param name="AvailableKnown">False when the INSTALL drive's free space could not be read (UNC share, disconnected mapped drive). Not a shortfall.</param>
/// <param name="LibraryAvailableBytes">Free space on the model library's own drive, when a library on another drive is set and readable; null when unreadable or when the library shares the install drive.</param>
/// <param name="ModelBytes">The part of <paramref name="RequiredBytes"/> that is model downloads.</param>
/// <param name="HasLibrary">Whether a custom model library is set at all, so the panel can tell "no library" from "library drive unreadable".</param>
public sealed record DiskSpaceEstimate(
    long RequiredBytes,
    long AvailableBytes,
    bool IsSufficient,
    IReadOnlyList<string> UnknownSizeModels,
    bool AvailableKnown = true,
    long? LibraryAvailableBytes = null,
    long ModelBytes = 0,
    bool HasLibrary = false)
{
    public string RequiredText => DiskSpaceRequirement.FormatBytes(RequiredBytes);
    public string AvailableText => DiskSpaceRequirement.FormatBytes(AvailableBytes);
    public string ModelBytesText => DiskSpaceRequirement.FormatBytes(ModelBytes);
    public string LibraryAvailableText => DiskSpaceRequirement.FormatBytes(LibraryAvailableBytes ?? 0);
}

/// <summary>Seam over the SDK's calculator so panels can be tested without HEAD requests.</summary>
public interface IDiskSpaceEstimator
{
    Task<DiskSpaceEstimate> EstimateAsync(DiskSpaceRequest request, CancellationToken ct = default);
}

public sealed class SdkDiskSpaceEstimator(UrlSizeResolver sizeResolver) : IDiskSpaceEstimator
{
    private readonly DiskSpaceCalculator _calculator = new(sizeResolver);

    public async Task<DiskSpaceEstimate> EstimateAsync(DiskSpaceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requirement = await _calculator.CalculateRequiredSpaceAsync(
            request.Workload,
            onlyModelDownload: false,
            request.SelectedVramGb,
            request.ExcludedModelIds,
            progress: null,
            ct,
            request.ExistingModelIds).ConfigureAwait(false);

        var installBytes = requirement.GitRepositoryBytes + requirement.VirtualEnvironmentBytes + requirement.BufferBytes;
        var hasLibrary = !string.IsNullOrWhiteSpace(request.ModelBaseFolder);
        var sameDrive = hasLibrary && SharesDrive(request.TargetFolder, request.ModelBaseFolder!);

        return Judge(
            installBytes,
            requirement.ModelDownloadsBytes,
            TryGetFreeSpace(request.TargetFolder),
            hasLibrary && !sameDrive ? TryGetFreeSpace(request.ModelBaseFolder!) : null,
            hasLibrary,
            requirement.UnknownSizeModels.ToList(),
            sameDrive);
    }

    /// <summary>Whether two folders sit on the same volume, by path root (case-insensitive). Unresolvable paths count as different.</summary>
    public static bool SharesDrive(string a, string b)
    {
        try
        {
            var ra = Path.GetPathRoot(Path.GetFullPath(a));
            var rb = Path.GetPathRoot(Path.GetFullPath(b));
            return !string.IsNullOrEmpty(ra) && string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The verdict, separated from the probing so it can be tested without drives. Without a
    /// library, or with a library on the SAME drive as the install, everything is charged to the
    /// install drive's one free-space figure. With a library on another drive, the install drive
    /// only has to hold the repo + venv and the library drive only the models. A drive whose free
    /// space could not be read is "unknown", which never counts as a shortfall: the install will
    /// find out, and asserting "0 B free" for a share with terabytes of room was worse than saying
    /// nothing. <paramref name="installFree"/> alone decides <see cref="DiskSpaceEstimate.AvailableKnown"/>;
    /// an unreadable library drive is reported on its own, not by hiding the install figure.
    /// </summary>
    public static DiskSpaceEstimate Judge(
        long installBytes, long modelBytes, long? installFree, long? libraryFree,
        bool? hasLibrary = null, IReadOnlyList<string>? unknownSizeModels = null, bool sameDrive = false)
    {
        var library = hasLibrary ?? libraryFree.HasValue;
        var required = installBytes + modelBytes;
        var splitDrives = library && !sameDrive;

        bool sufficient;
        if (!splitDrives)
        {
            sufficient = installFree is null || installFree.Value >= required;
        }
        else
        {
            var installOk = installFree is null || installFree.Value >= installBytes;
            var libraryOk = libraryFree is null || libraryFree.Value >= modelBytes;
            sufficient = installOk && libraryOk;
        }

        return new DiskSpaceEstimate(
            required,
            installFree ?? 0,
            sufficient,
            unknownSizeModels ?? [],
            installFree.HasValue,
            splitDrives ? libraryFree : null,
            modelBytes,
            library);
    }

    /// <summary>Free bytes on the drive holding <paramref name="path"/>, or null when it cannot be read.</summary>
    public static long? TryGetFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
