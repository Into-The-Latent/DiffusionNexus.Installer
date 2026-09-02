using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Content;

public class DiskSpaceEstimatorTests
{
    [Fact]
    public async Task A_workload_without_models_is_estimated_offline_from_the_sdk_constants()
    {
        // No models means no HEAD requests, so this runs without network. The SDK charges a fixed
        // repo + venv + buffer estimate; the point here is the adapter's plumbing, not the numbers.
        var estimator = new SdkDiskSpaceEstimator(new UrlSizeResolver(new HttpClient { Timeout = TimeSpan.FromSeconds(1) }));
        var workload = new InstallationConfiguration();
        workload.Repository.Type = RepositoryType.ComfyUI;

        var estimate = await estimator.EstimateAsync(new DiskSpaceRequest(workload, Path.GetTempPath(), 0, [], []));

        estimate.RequiredBytes.Should().BePositive();
        estimate.AvailableBytes.Should().BePositive("the temp drive exists");
        estimate.UnknownSizeModels.Should().BeEmpty();
        estimate.RequiredText.Should().NotBeNullOrWhiteSpace();
    }
}
