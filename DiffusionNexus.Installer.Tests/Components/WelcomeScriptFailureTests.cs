using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// What the welcome screen does when wwwroot/js/jukebox.js will not load.
///
/// Separate from <see cref="WelcomePageTests"/> because that class plans the module for every
/// test, and because bUnit's own JSInterop cannot express this: it refuses to let a handler
/// returning an IJSObjectReference throw, and its strict-mode exception for an unplanned call is a
/// harness artifact rather than what a browser produces. A real failed dynamic import surfaces as
/// <see cref="JSException"/>, so the runtime is stubbed to produce exactly that.
/// </summary>
public class WelcomeScriptFailureTests : BunitContext
{
    /// <summary>An IJSRuntime that fails the way a missing or broken module does.</summary>
    private sealed class BrokenJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new JSException("Failed to fetch dynamically imported module");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new JSException("Failed to fetch dynamically imported module");
    }

    [Fact]
    public void Costs_the_arrows_and_nothing_else()
    {
        // OnAfterRenderAsync is a lifecycle method: an exception escaping it is fatal to the
        // circuit and replaces the app's first and most-travelled screen with the "An unhandled
        // error has occurred" banner. Stale published assets after an in-place update, an asset
        // the packager missed, or a parse error introduced later would all do it -- and the catch
        // originally covered only JSDisconnectedException, which is not what any of those throw.
        Services.AddSingleton(Mock.Of<IFeedbackReportingService>());
        Services.AddSingleton(OfflineCommunityLinks.Cache());
        UpdateSignals.Register(Services);
        Services.AddSingleton<IJSRuntime>(new BrokenJs());

        var workloads = new[]
        {
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Workload(RepositoryType.Fooocus, "Fooocus"),
        };

        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(workloads);
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());
        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));

        var cut = Render<Welcome>();

        // The screen is still a screen: the cards render, and the track is an ordinary scroll
        // container, so a wheel and a trackpad still move it.
        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().HaveCount(2));
        cut.Find(".jukebox-track").Should().NotBeNull();

        // Only the buttons are lost -- and they say so rather than sitting there looking live.
        cut.WaitForAssertion(() =>
            cut.FindAll(".jukebox-arrow").Should().OnlyContain(a => a.HasAttribute("disabled")));
    }

    private static InstallationConfiguration Workload(RepositoryType software, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Repository = new MainRepositorySettings { Type = software }
    };
}
