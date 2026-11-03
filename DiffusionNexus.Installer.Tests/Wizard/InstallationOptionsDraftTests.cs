using DiffusionNexus.Installer.Core.Wizard;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

public class InstallationOptionsDraftTests
{
    [Fact]
    public void An_untouched_draft_produces_the_sdk_defaults()
    {
        var options = new InstallationOptionsDraft().ToOptions();

        options.OnlyModelDownload.Should().BeFalse();
        options.DownloadModelsOnly.Should().BeFalse();
        options.SortModelsIntoSubfolders.Should().BeTrue();
        options.CreateDesktopShortcut.Should().BeTrue();
        options.CreateStartMenuShortcut.Should().BeTrue();
        options.SkipVcRuntimeProvisioning.Should().BeFalse();
        options.ExcludedModelIds.Should().BeEmpty();
    }

    [Fact]
    public void Models_only_values_survive_conversion()
    {
        var options = new InstallationOptionsDraft { DownloadModelsOnly = true, SortModelsIntoSubfolders = false }.ToOptions();

        options.DownloadModelsOnly.Should().BeTrue();
        options.SortModelsIntoSubfolders.Should().BeFalse();
    }

    [Fact]
    public void Contributed_values_survive_conversion()
    {
        var draft = new InstallationOptionsDraft
        {
            ModelBaseFolder = @"D:\Models",
            OutputFolder = @"D:\Output",
            GenerateExtraModelPaths = true,
            CreateDesktopShortcut = false,
            CpuTorch = true,
            SelectedVramProfile = 16,
        };
        draft.ExcludedModelIds.Add(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        var options = draft.ToOptions();

        options.ModelBaseFolder.Should().Be(@"D:\Models");
        options.OutputFolder.Should().Be(@"D:\Output");
        options.GenerateExtraModelPaths.Should().BeTrue();
        options.CreateDesktopShortcut.Should().BeFalse();
        options.CpuTorch.Should().BeTrue();
        options.SelectedVramProfile.Should().Be(16);
        options.ExcludedModelIds.Should().ContainSingle();
    }

    [Fact]
    public void Url_sets_are_case_insensitive_like_the_sdk_defaults()
    {
        var draft = new InstallationOptionsDraft();
        draft.ForceRedownloadUrls.Add("https://Example.com/A.safetensors");

        draft.ToOptions().ForceRedownloadUrls
            .Should().Contain("https://example.com/a.safetensors");
    }
}
