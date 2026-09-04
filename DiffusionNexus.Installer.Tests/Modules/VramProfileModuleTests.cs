using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class VramProfileModuleTests
{
    private static WizardSelection Selection(string profiles)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Vram.VramProfiles = profiles;
        return new WizardSelection { Workload = w };
    }

    [Fact]
    public async Task Offers_exactly_the_declared_tiers_with_the_lowest_preselected()
    {
        // Decision 4: ideogram-4-0 declares 24,32 -- the dropdown must not pad in 8/12/16.
        var module = new VramProfileModule();
        var selection = Selection("24,32");

        await module.InitializeAsync(selection);

        module.Tiers.Should().Equal(24, 32);
        module.SelectedTier.Should().Be(24);
        selection.SelectedVramProfile.Should().Be(24, "the selection is what ModelSelection reads");
    }

    [Fact]
    public async Task Changing_the_tier_updates_the_selection_and_the_draft()
    {
        var module = new VramProfileModule();
        var selection = Selection("8,12,16,24,32");
        await module.InitializeAsync(selection);

        module.SelectedTier = 16;

        selection.SelectedVramProfile.Should().Be(16);
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.SelectedVramProfile.Should().Be(16);
    }

    [Fact]
    public void Applies_only_when_the_workload_parses_to_at_least_one_tier()
    {
        // Stateless on purpose: the agreement test calls AppliesTo without InitializeAsync.
        var module = new VramProfileModule();

        module.AppliesTo(Selection("8,12")).Should().BeTrue();
        module.AppliesTo(Selection("")).Should().BeFalse();
        module.AppliesTo(Selection("abc")).Should().BeFalse("Detect uses the same parser and says no tier");
    }

    [Fact]
    public async Task A_workload_without_tiers_contributes_zero_which_the_sdk_treats_as_no_filtering()
    {
        // Decision 5: no tiers means every declared model downloads.
        var module = new VramProfileModule();
        var selection = Selection("");
        await module.InitializeAsync(selection);

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        module.Tiers.Should().BeEmpty();
        draft.SelectedVramProfile.Should().Be(0);
        selection.SelectedVramProfile.Should().Be(0);
    }

    [Fact]
    public async Task Validation_never_blocks()
    {
        var module = new VramProfileModule();
        await module.InitializeAsync(Selection("8,12"));

        module.Validate().IsValid.Should().BeTrue("a preselected tier cannot be unanswered");
    }

    [Fact]
    public async Task Reinitializing_for_another_workload_reselects_that_workloads_lowest_tier()
    {
        var module = new VramProfileModule();
        await module.InitializeAsync(Selection("8,12,16"));
        module.SelectedTier = 16;

        var next = Selection("24,32");
        await module.InitializeAsync(next);

        module.SelectedTier.Should().Be(24);
        next.SelectedVramProfile.Should().Be(24);
    }
}
