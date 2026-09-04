using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Content;

/// <param name="ExistingModelIds">Models already on disk; their downloads are not counted.</param>
public sealed record DiskSpaceRequest(
    InstallationConfiguration Workload,
    string TargetFolder,
    int SelectedVramGb,
    HashSet<Guid> ExcludedModelIds,
    HashSet<Guid> ExistingModelIds);

public sealed record DiskSpaceEstimate(
    long RequiredBytes,
    long AvailableBytes,
    bool IsSufficient,
    IReadOnlyList<string> UnknownSizeModels)
{
    public string RequiredText => DiskSpaceRequirement.FormatBytes(RequiredBytes);
    public string AvailableText => DiskSpaceRequirement.FormatBytes(AvailableBytes);
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

        var validation = DiskSpaceCalculator.ValidateDiskSpace(request.TargetFolder, requirement);

        return new DiskSpaceEstimate(
            validation.RequiredBytes,
            validation.AvailableBytes,
            validation.HasSufficientSpace,
            requirement.UnknownSizeModels.ToList());
    }
}
