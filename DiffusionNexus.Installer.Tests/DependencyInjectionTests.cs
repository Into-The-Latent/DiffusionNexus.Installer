using DiffusionNexus.Installer.Core;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DiffusionNexus.Installer.Tests;

public class DependencyInjectionTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<IPythonService, PythonService>();
        services.AddInstallationServices();
        services.AddSingleton<IInstallationOrchestrator, InstallationOrchestrator>();
        services.AddDiffusionNexusUserSettings(Path.Combine(Path.GetTempPath(), $"dn-{Guid.NewGuid():N}.json"));
        services.AddDiffusionNexusCatalog(o =>
            o.InstalledCatalogPath = Path.Combine(Path.GetTempPath(), $"dn-catalog-{Guid.NewGuid():N}"));
        services.AddInstallerCore();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void All_slice_one_modules_resolve()
    {
        using var provider = Build();

        var registry = provider.GetRequiredService<WizardModuleRegistry>();

        registry.SatisfiedCapabilities.Should().Be(WorkloadCapability.ComfyFolders);
    }

    [Fact]
    public void The_install_session_is_a_singleton()
    {
        using var provider = Build();

        var first = provider.GetRequiredService<IInstallSession>();
        var second = provider.GetRequiredService<IInstallSession>();

        first.Should().BeSameAs(second);
    }
}
