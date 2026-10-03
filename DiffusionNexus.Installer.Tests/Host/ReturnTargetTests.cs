using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Host;

public class ReturnTargetTests
{
    [Fact]
    public void Leads_to_the_welcome_screen_until_a_flow_screen_has_been_seen()
        => new ReturnTarget(new StubCatalogUpdateCoordinator()).Path.Should().Be("/");

    [Theory]
    [InlineData("", "/")]
    [InlineData("software/ComfyUI", "/software/ComfyUI")]
    [InlineData("/install/abc", "/install/abc")]
    [InlineData("install/abc?x=1", "/install/abc")]
    [InlineData("install/abc#log", "/install/abc")]
    public void Remembers_a_rooted_path_and_nothing_else(string baseRelative, string expected)
    {
        var target = new ReturnTarget(new StubCatalogUpdateCoordinator());

        target.Remember(baseRelative);

        target.Path.Should().Be(expected);
    }

    [Fact]
    public void Knows_whether_a_page_is_being_returned_to_or_arrived_at()
    {
        var target = new ReturnTarget(new StubCatalogUpdateCoordinator());
        target.Remember("install/ABC");

        target.IsAt("install/abc?x=1").Should().BeTrue("route matching is case-insensitive and ignores the query");
        target.IsAt("software/ComfyUI").Should().BeFalse();
    }

    [Fact]
    public void Leads_home_once_an_apply_has_changed_the_catalog_since_the_screen_was_seen()
    {
        // Decided on read, not on an event a page has to witness: a page built after the apply
        // landed (a reconnect, a reload) gets the same answer (#32, PR #44 review).
        var catalog = new StubCatalogUpdateCoordinator();
        var target = new ReturnTarget(catalog);
        target.Remember("install/ABC");

        catalog.ContentGeneration++;

        target.Path.Should().Be("/");
        target.IsAt("install/ABC").Should().BeFalse("returning would rebuild that screen from the new catalog");

        target.Remember("software/ComfyUI");
        target.Path.Should().Be("/software/ComfyUI", "a screen seen after the apply was built from it");
    }

    [Fact]
    public void Says_when_an_apply_moved_it_and_only_then()
    {
        var catalog = new StubCatalogUpdateCoordinator();
        var target = new ReturnTarget(catalog);
        var raised = 0;
        target.Changed += () => raised++;

        catalog.Phase = Core.Updates.CatalogUpdatePhase.Checking;
        catalog.RaiseChanged();
        raised.Should().Be(0);

        catalog.ContentGeneration++;
        catalog.RaiseChanged();
        catalog.RaiseChanged();
        raised.Should().Be(1);
    }

    [Fact]
    public void A_screen_read_before_an_apply_is_stamped_with_what_it_read()
    {
        // A page that reads the catalog before its shell renders passes the generation it read
        // under; stamped after the read, an apply in between would pass stale content as current.
        var catalog = new StubCatalogUpdateCoordinator();
        var target = new ReturnTarget(catalog);
        var readUnder = catalog.ContentGeneration;
        catalog.ContentGeneration++;

        target.Remember("software/ComfyUI", readUnder);

        target.Path.Should().Be("/");
    }

    [Fact]
    public async Task A_kept_wizard_is_released_when_another_flow_screen_is_remembered()
    {
        // Left by the top bar's home link: never restored again, so not held for the session.
        var workload = new DiffusionNexus.Installer.SDK.Models.Configuration.InstallationConfiguration { Name = "Fooocus" };
        var plan = await new Core.Wizard.WizardModuleRegistry(() => [])
            .BuildPlanAsync(new Core.Wizard.WizardSelection { Workload = workload });
        var target = new ReturnTarget(new StubCatalogUpdateCoordinator())
        {
            WizardInProgress = new KeptWizard(new Core.Wizard.WizardRun(plan), false, 0),
        };

        target.Remember($"install/{workload.Id}");
        target.WizardInProgress.Should().NotBeNull("the wizard's own screen keeps it");

        target.Remember("");
        target.WizardInProgress.Should().BeNull();
    }

    [Fact]
    public void Listens_to_the_catalog_only_while_someone_listens_to_it()
    {
        var catalog = new StubCatalogUpdateCoordinator();
        var target = new ReturnTarget(catalog);
        catalog.Subscribers.Should().Be(0);

        Action handler = () => { };
        target.Changed += handler;
        catalog.Subscribers.Should().Be(1);

        target.Changed -= handler;
        catalog.Subscribers.Should().Be(0);
    }
}
