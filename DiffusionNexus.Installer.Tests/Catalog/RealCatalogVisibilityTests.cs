using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Catalog;

/// <summary>
/// The synthetic tests next door prove the rule; this proves it against the catalog.zip the
/// Electron project actually embeds and ships. The bug it guards was invisible to synthetic
/// fixtures: every unit test passed while the shipped ComfyUI card listed sixteen entries.
/// </summary>
public sealed class RealCatalogVisibilityTests : IAsyncLifetime
{
    private string _catalogDir = string.Empty;
    private IReadOnlyList<InstallationConfiguration> _workloads = [];

    public async Task InitializeAsync() => (_catalogDir, _workloads) = await EmbeddedCatalog.LoadAsync();

    public Task DisposeAsync()
    {
        EmbeddedCatalog.Delete(_catalogDir);
        return Task.CompletedTask;
    }

    private IReadOnlyList<InstallationConfiguration> Offered(WorkloadVisibility visibility) =>
        _workloads.Where(visibility.IsOffered).ToList();

    [Fact]
    public void Nothing_a_user_is_offered_is_core_targeted_or_unreleased()
    {
        var offered = Offered(WorkloadVisibility.ReleaseOnly);

        offered.Should().NotBeEmpty("a broken extraction must fail loudly, not vacuously pass below");
        offered.Should().OnlyContain(w =>
            w.WorkloadTarget == WorkloadTargetType.Installer && w.IsReleaseConfig);
    }

    [Fact]
    public void The_embedded_catalog_still_contains_entries_the_rule_has_to_hide()
    {
        // Without this the test above passes just as well against a catalog with nothing to hide,
        // which is precisely the state the shipped build was wrongly in.
        _workloads.Should().Contain(w => w.WorkloadTarget == WorkloadTargetType.Installer && !w.IsReleaseConfig);
    }

    [Fact]
    public void A_shipped_build_drops_only_the_unreleased_comfyui_packs()
    {
        var comfy = _workloads
            .Where(w => w.Repository.Type == RepositoryType.ComfyUI
                && w.WorkloadTarget == WorkloadTargetType.Installer)
            .ToList();

        var offered = Offered(WorkloadVisibility.ReleaseOnly)
            .Where(w => w.Repository.Type == RepositoryType.ComfyUI)
            .Select(w => w.Name)
            .ToList();

        // Named rather than counted: if a later catalog revision un-flags one of these, the
        // failure should say which pack came back rather than just "expected 12, found 13".
        comfy.Select(w => w.Name).Except(offered).Should().BeEquivalentTo(
            "Blanck-ComfyUI",
            "ComfyUI Llama Cpp test",
            "Config535",
            "Qwen-Image-Edit-2511 - Deprecated");
    }

    [Fact]
    public void The_released_legacy_packs_are_what_the_switch_reveals()
    {
        // Offered, but kept off the default workload screen: these are exactly the cards the
        // "Show legacy workloads" switch adds. Without any, the switch would never render at all.
        Offered(WorkloadVisibility.ReleaseOnly)
            .Where(w => w.IsLegacy)
            .Select(w => w.Name)
            .Should().BeEquivalentTo("LTX-2-3-GGUF", "LTX2 - GGUF - Legacy");
    }

    [Fact]
    public void Developer_builds_see_the_unreleased_packs()
    {
        var local = Offered(WorkloadVisibility.IncludingNonRelease);

        local.Should().Contain(w => !w.IsReleaseConfig, "non-release entries exist to be testable locally");
        local.Should().NotContain(w => w.WorkloadTarget != WorkloadTargetType.Installer);
    }
}
