using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Catalog;

public class CatalogWorkloadSourceTests
{
    private static InstallationConfiguration Workload(string name, WorkloadTargetType target) =>
        new() { Name = name, WorkloadTarget = target };

    [Fact]
    public async Task Only_installer_targeted_workloads_are_returned()
    {
        var catalog = new Mock<ICatalog>();
        catalog.Setup(c => c.GetWorkloadsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstallationConfiguration>
            {
                Workload("Blank ComfyUI", WorkloadTargetType.Installer),
                Workload("Inpainting Qwen", WorkloadTargetType.DiffusionNexusCore),
            });

        var source = new CatalogWorkloadSource(catalog.Object);

        var result = await source.GetInstallerWorkloadsAsync();

        result.Should().ContainSingle().Which.Name.Should().Be("Blank ComfyUI");
    }

    [Fact]
    public async Task A_single_workload_is_fetched_by_id_not_by_cloning_the_whole_catalog()
    {
        // ICatalog.GetWorkloadsAsync deep-copies EVERY catalogued workload, nested model-download
        // lists included. The thumbnail endpoint only ever asks about one id, so it gets a member
        // that asks about one id. MockBehavior.Strict: a fall back to the list member throws
        // rather than quietly working and quietly costing 25 clones a call.
        var id = Guid.NewGuid();
        var workload = Workload("Krea-2-Turbo", WorkloadTargetType.Installer);
        workload.Id = id;

        var catalog = new Mock<ICatalog>(MockBehavior.Strict);
        catalog.Setup(c => c.GetWorkloadAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(workload);

        var result = await new CatalogWorkloadSource(catalog.Object).GetInstallerWorkloadAsync(id);

        result!.Name.Should().Be("Krea-2-Turbo");
        catalog.Verify(c => c.GetWorkloadsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_workload_this_installer_never_offers_is_not_found_by_id_either()
    {
        // Same WorkloadTarget filter as the list member -- otherwise the by-id lookup would be a
        // hole straight through the thumbnail endpoint's authorization gate.
        var id = Guid.NewGuid();
        var workload = Workload("Inpainting Qwen", WorkloadTargetType.DiffusionNexusCore);
        workload.Id = id;

        var catalog = new Mock<ICatalog>(MockBehavior.Strict);
        catalog.Setup(c => c.GetWorkloadAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(workload);

        var result = await new CatalogWorkloadSource(catalog.Object).GetInstallerWorkloadAsync(id);

        result.Should().BeNull();
    }

    [Fact]
    public async Task An_id_the_catalog_does_not_have_is_null_rather_than_an_exception()
    {
        var id = Guid.NewGuid();
        var catalog = new Mock<ICatalog>(MockBehavior.Strict);
        catalog.Setup(c => c.GetWorkloadAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstallationConfiguration?)null);

        var result = await new CatalogWorkloadSource(catalog.Object).GetInstallerWorkloadAsync(id);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Legacy_workloads_are_returned_but_flagged()
    {
        var legacy = Workload("Old pack", WorkloadTargetType.Installer);
        legacy.IsLegacy = true;

        var catalog = new Mock<ICatalog>();
        catalog.Setup(c => c.GetWorkloadsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstallationConfiguration> { legacy });

        var source = new CatalogWorkloadSource(catalog.Object);

        var result = await source.GetInstallerWorkloadsAsync();

        result.Should().ContainSingle().Which.IsLegacy.Should().BeTrue();
    }
}
