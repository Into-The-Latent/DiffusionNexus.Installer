using DiffusionNexus.Installer.Core.Wizard;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

public class WizardRoutesTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void A_fresh_install_goes_to_the_install_route() =>
        WizardRoutes.Install(Id, WizardMode.FreshInstall).Should().Be($"/install/{Id}");

    [Fact]
    public void Models_only_goes_to_its_own_route() =>
        WizardRoutes.Install(Id, WizardMode.ModelsOnly).Should().Be($"/existing/models/{Id}");
}
