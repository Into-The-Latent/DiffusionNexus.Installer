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
