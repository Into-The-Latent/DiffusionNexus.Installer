using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests;

public class SeamTests
{
    [Fact]
    public void Sdk_model_types_are_reachable_from_core()
    {
        var config = new InstallationConfiguration { Name = "probe" };

        config.WorkloadTarget.Should().Be(WorkloadTargetType.Installer);
        config.Repository.Type.Should().Be(RepositoryType.ComfyUI);
    }

    [Fact]
    public void Catalog_package_is_referenced()
    {
        typeof(ICatalog).Assembly.GetName().Name
            .Should().Be("DiffusionNexus.Installer.SDK.Catalog");
    }
}
