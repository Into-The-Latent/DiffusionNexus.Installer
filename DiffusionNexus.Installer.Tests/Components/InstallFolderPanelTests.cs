using DiffusionNexus.Installer.SDK.Services;
using Bunit;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using DiffusionNexus.Installer.SDK.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// Proves the Fix-1 wiring end to end through a real render: Blazor calls StateHasChanged only on
/// the component that owns a handler, so without the panel invoking a parent-supplied Changed
/// callback after every edit, the parent's Next button goes stale and the wizard cannot be
/// completed. This renders the actual InstallFolderPanel from the Electron project, not a stand-in.
/// </summary>
public class InstallFolderPanelTests : BunitContext
{
    private static InstallFolderModule Module()
    {
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings { DefaultTargetInstallFolder = string.Empty });
        return new InstallFolderModule(settings.Object, new PreInstallationService());
    }

    private static async Task<InstallFolderModule> InitializedModule()
    {
        var module = Module();
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        await module.InitializeAsync(new WizardSelection { Workload = w });
        return module;
    }

    [Fact]
    public async Task The_panel_says_software_and_shows_the_folder_that_will_be_created()
    {
        Services.AddSingleton(Mock.Of<IFolderPicker>());
        var module = await InitializedModule();
        var cut = Render<InstallFolderPanel>(p => p.Add(x => x.Module, module));

        cut.Markup.Should().Contain("Where the software gets installed").And.NotContain("workload");
        cut.FindAll(".destination").Should().BeEmpty("nothing is created while the box is empty");

        cut.Find("input").Input(@"E:\Installer\9");

        cut.Find(".destination").TextContent.Should().Contain(@"E:\Installer\9\ComfyUI");
    }

    [Fact]
    public async Task The_validation_message_sits_directly_under_the_hint_line()
    {
        // The page used to list every message under the buttons, far from the box it is about.
        Services.AddSingleton(Mock.Of<IFolderPicker>());
        var module = await InitializedModule();
        var cut = Render<InstallFolderPanel>(p => p.Add(x => x.Module, module));

        var error = cut.Find(".panel .validation-error");
        error.TextContent.Should().Contain("Choose a folder");

        var hintEnd = cut.Markup.IndexOf("Where the software gets installed", StringComparison.Ordinal);
        var errorAt = cut.Markup.IndexOf("Choose a folder", StringComparison.Ordinal);
        var inputAt = cut.Markup.IndexOf("<input", StringComparison.Ordinal);
        errorAt.Should().BeGreaterThan(hintEnd).And.BeLessThan(inputAt);
    }

    [Fact]
    public void Typing_a_folder_raises_Changed_and_updates_the_module()
    {
        Services.AddSingleton(Mock.Of<IFolderPicker>());

        var module = Module();
        module.TargetFolder.Should().BeEmpty();
        var changed = false;

        var cut = Render<InstallFolderPanel>(parameters => parameters
            .Add(p => p.Module, module)
            .Add(p => p.Changed, EventCallback.Factory.Create(this, () => changed = true)));

        cut.Find("input").Input(@"D:\Installs\Fooocus");

        changed.Should().BeTrue("the parent's disabled state can only refresh if it is notified");
        module.TargetFolder.Should().Be(@"D:\Installs\Fooocus");
    }

    [Fact]
    public async Task Dismissing_the_folder_dialog_leaves_the_field_unchanged_and_does_not_raise_Changed()
    {
        var picker = new Mock<IFolderPicker>();
        picker.Setup(p => p.PickFolderAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        Services.AddSingleton(picker.Object);

        var module = Module();
        module.TargetFolder = @"C:\Existing";
        var changed = false;

        var cut = Render<InstallFolderPanel>(parameters => parameters
            .Add(p => p.Module, module)
            .Add(p => p.Changed, EventCallback.Factory.Create(this, () => changed = true)));

        await cut.Find("button").ClickAsync(new MouseEventArgs());

        changed.Should().BeFalse("a dismissed dialog must not be treated as an edit");
        module.TargetFolder.Should().Be(@"C:\Existing");
    }

    // ----- the folder check runs off the page (#60) -----

    private static async Task<InstallFolderModule> ModuleChecking(Func<PreInstallationValidationResult> answer, TimeSpan? checkLimit = null)
    {
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings { DefaultTargetInstallFolder = string.Empty });
        var check = new Mock<IPreInstallationService>();
        check.Setup(c => c.ValidateTargetFolder(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationType>()))
            .Returns(answer);
        var module = new InstallFolderModule(settings.Object, check.Object)
        {
            AnswerWait = TimeSpan.FromMilliseconds(10),
            CheckLimit = checkLimit ?? TimeSpan.FromSeconds(60),
        };
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        await module.InitializeAsync(new WizardSelection { Workload = w });
        return module;
    }

    private static readonly PreInstallationValidationResult Fine = PreInstallationValidationResult.Success(@"E:\x", false, false);

    [Fact]
    public async Task While_the_folder_is_checked_the_panel_says_so_and_then_tells_the_parent_the_answer()
    {
        // The parent owns Next, so the answer has to reach it without the user touching anything.
        Services.AddSingleton(Mock.Of<IFolderPicker>());
        using var gate = new ManualResetEventSlim();
        var module = await ModuleChecking(() => { gate.Wait(TimeSpan.FromSeconds(30)); return Fine; });
        var changes = 0;
        var cut = Render<InstallFolderPanel>(parameters => parameters
            .Add(p => p.Module, module)
            .Add(p => p.Changed, EventCallback.Factory.Create(this, () => changes++)));

        cut.Find("input").Input(@"\\nas\share\AI");

        cut.Find(".panel-hint.folder-check").TextContent.Should().Contain("Checking this folder");
        cut.FindAll(".validation-error").Should().BeEmpty("a check under way is not a problem yet");
        var changesWhileChecking = changes;

        gate.Set();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".folder-check").Should().BeEmpty();
            changes.Should().BeGreaterThan(changesWhileChecking);
        });
    }

    [Fact]
    public async Task A_folder_that_never_answered_can_be_tried_again()
    {
        Services.AddSingleton(Mock.Of<IFolderPicker>());
        using var gate = new ManualResetEventSlim();
        var calls = 0;
        var module = await ModuleChecking(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) gate.Wait(TimeSpan.FromSeconds(30));
            return Fine;
        }, checkLimit: TimeSpan.FromMilliseconds(50));
        var cut = Render<InstallFolderPanel>(p => p.Add(x => x.Module, module));

        cut.FindAll("button").Where(b => b.TextContent.Trim() == "Try again").Should().BeEmpty();
        cut.Find("input").Input(@"\\nas\share\AI");

        cut.WaitForAssertion(() => cut.Find(".validation-error").TextContent.Should().Contain("did not answer"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Try again").Click();
        gate.Set();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".validation-error").Should().BeEmpty();
            cut.FindAll("button").Where(b => b.TextContent.Trim() == "Try again").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task A_panel_that_went_away_no_longer_listens_for_the_answer()
    {
        // A wizard kept for a side trip (#45) outlives its panel; the check it started runs on.
        Services.AddSingleton(Mock.Of<IFolderPicker>());
        using var gate = new ManualResetEventSlim();
        var module = await ModuleChecking(() => { gate.Wait(TimeSpan.FromSeconds(30)); return Fine; });
        var cut = Render<InstallFolderPanel>(p => p.Add(x => x.Module, module));
        cut.Find("input").Input(@"\\nas\share\AI");
        var renders = cut.RenderCount;
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await DisposeComponentsAsync();
        module.FolderCheckAnswered += () => answered.TrySetResult();

        gate.Set();
        await answered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        cut.RenderCount.Should().Be(renders);
    }
}
