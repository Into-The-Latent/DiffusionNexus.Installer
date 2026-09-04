using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Gallery;

public class GalleryBuilderTests
{
    private sealed class FoldersModule : IWizardModule
    {
        public string Id => "comfy-folders";
        public WizardStage Stage => WizardStage.Location;
        public int Order => 10;
        public WorkloadCapability Satisfies => WorkloadCapability.ComfyFolders;
        public bool AppliesTo(WizardSelection s) => true;
        public Task InitializeAsync(WizardSelection s, CancellationToken ct = default) => Task.CompletedTask;
        public void Contribute(InstallationOptionsDraft d) { }
        public ModuleValidation Validate() => ModuleValidation.Ok();
    }

    private static InstallationConfiguration Workload(string name, RepositoryType type)
    {
        var w = new InstallationConfiguration { Name = name };
        w.Repository.Type = type;
        return w;
    }

    private static GalleryBuilder Builder(params InstallationConfiguration[] workloads)
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(workloads);
        return new GalleryBuilder(source.Object, new WizardModuleRegistry(() => [new FoldersModule()]));
    }

    [Fact]
    public async Task Thin_and_blank_workloads_are_installable()
    {
        var entries = await Builder(
            Workload("Fooocus", RepositoryType.Fooocus),
            Workload("Blank ComfyUI", RepositoryType.ComfyUI)).BuildAsync();

        entries.Should().OnlyContain(e => e.IsInstallable);
    }

    [Fact]
    public async Task A_content_pack_is_listed_but_not_installable()
    {
        var pack = Workload("Krea 2 Turbo", RepositoryType.ComfyUI);
        pack.ModelDownloads.Add(new ModelDownload());
        pack.Vram.VramProfiles = "8,12,16";

        var entries = await Builder(pack).BuildAsync();

        var entry = entries.Should().ContainSingle().Subject;
        entry.IsInstallable.Should().BeFalse();
        entry.MissingCapabilities.Should().HaveFlag(WorkloadCapability.ModelDownloads);
        entry.MissingCapabilities.Should().HaveFlag(WorkloadCapability.VramProfile);
        entry.MissingCapabilities.Should().NotHaveFlag(WorkloadCapability.ComfyFolders);
    }

    [Fact]
    public async Task Installable_entries_sort_before_unavailable_ones()
    {
        var pack = Workload("Krea 2 Turbo", RepositoryType.ComfyUI);
        pack.ModelDownloads.Add(new ModelDownload());

        var entries = await Builder(pack, Workload("Fooocus", RepositoryType.Fooocus)).BuildAsync();

        entries[0].Workload.Name.Should().Be("Fooocus");
    }

    [Fact]
    public async Task Legacy_workloads_sort_last_among_their_peers()
    {
        var legacy = Workload("Old ComfyUI pack", RepositoryType.ComfyUI);
        legacy.IsLegacy = true;

        var entries = await Builder(legacy, Workload("Blank ComfyUI", RepositoryType.ComfyUI)).BuildAsync();

        entries.Last().Workload.Name.Should().Be("Old ComfyUI pack");
    }

    [Fact]
    public async Task Workflow_type_travels_to_the_entry_for_filtering()
    {
        var audio = Workload("ACE-Step", RepositoryType.AceStep);
        audio.WorkflowType = WorkflowType.Audio;

        var entries = await Builder(audio).BuildAsync();

        entries.Should().ContainSingle().Which.Workload.WorkflowType.Should().Be(WorkflowType.Audio);
    }
}
