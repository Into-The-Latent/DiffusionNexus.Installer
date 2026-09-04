using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Content;

public class DiskSpaceEstimatorTests
{
    [Fact]
    public void With_a_model_library_the_install_drive_only_has_to_hold_the_install()
    {
        // Review finding: 60 GB of models going to D: were charged against C: and the panel
        // reported "Not enough space" for an install that fits.
        var verdict = SdkDiskSpaceEstimator.Judge(installBytes: 10, modelBytes: 60, installFree: 40, libraryFree: 4000);

        verdict.IsSufficient.Should().BeTrue();
        verdict.AvailableKnown.Should().BeTrue();
        verdict.RequiredBytes.Should().Be(70);
        verdict.AvailableBytes.Should().Be(40);
        verdict.LibraryAvailableBytes.Should().Be(4000);
        verdict.ModelBytes.Should().Be(60);
    }

    [Fact]
    public void A_library_on_the_same_drive_as_the_install_shares_one_free_space_figure()
    {
        // Review finding: E:\Installer (20 GB) + E:\Models (60 GB) with 70 GB free passed both
        // per-drive checks and ran out mid-install. Same drive is the common layout.
        SdkDiskSpaceEstimator.SharesDrive(@"E:\Installer\9", @"e:\Models").Should().BeTrue();
        SdkDiskSpaceEstimator.SharesDrive(@"E:\Installer\9", @"D:\Models").Should().BeFalse();

        var verdict = SdkDiskSpaceEstimator.Judge(installBytes: 20, modelBytes: 60, installFree: 70, libraryFree: 70, sameDrive: true);

        verdict.IsSufficient.Should().BeFalse("80 GB has to fit in the 70 GB the two folders share");
        verdict.LibraryAvailableBytes.Should().BeNull("there is no second drive to report");
        verdict.HasLibrary.Should().BeTrue();
    }

    [Fact]
    public void An_unreadable_library_drive_does_not_hide_the_install_drive_figure()
    {
        // Review finding: known was an AND of both drives, so one unreadable library drive made the
        // panel claim it could not read free space while holding a good install-drive figure.
        var verdict = SdkDiskSpaceEstimator.Judge(installBytes: 10, modelBytes: 60, installFree: 40, libraryFree: null, hasLibrary: true);

        verdict.AvailableKnown.Should().BeTrue();
        verdict.AvailableBytes.Should().Be(40);
        verdict.HasLibrary.Should().BeTrue();
        verdict.LibraryAvailableBytes.Should().BeNull();
        verdict.IsSufficient.Should().BeTrue("the install drive fits its share; the library side is unknown, not short");
    }

    [Fact]
    public void A_full_model_library_drive_is_a_shortfall_even_when_the_install_drive_is_roomy()
        => SdkDiskSpaceEstimator.Judge(installBytes: 10, modelBytes: 60, installFree: 4000, libraryFree: 5)
            .IsSufficient.Should().BeFalse();

    [Fact]
    public void Without_a_library_everything_is_charged_to_the_install_drive()
        => SdkDiskSpaceEstimator.Judge(installBytes: 10, modelBytes: 60, installFree: 40, libraryFree: null)
            .IsSufficient.Should().BeFalse();

    [Fact]
    public void Unreadable_free_space_is_reported_as_unknown_not_as_a_shortfall()
    {
        // Review finding: a UNC share or a disconnected mapped drive came back as "0 B free" and
        // the panel asserted "Not enough space" for terabytes of room.
        var verdict = SdkDiskSpaceEstimator.Judge(installBytes: 10, modelBytes: 60, installFree: null, libraryFree: null);

        verdict.AvailableKnown.Should().BeFalse();
        verdict.IsSufficient.Should().BeTrue("an unknown must not block or scare; the install will find out");
    }

    [Fact]
    public void A_path_with_no_readable_drive_yields_no_free_space_figure()
        => SdkDiskSpaceEstimator.TryGetFreeSpace(@"\\nas-that-does-not-exist-" + Guid.NewGuid().ToString("N") + @"\ai").Should().BeNull();

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
