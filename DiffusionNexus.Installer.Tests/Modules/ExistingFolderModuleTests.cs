using DiffusionNexus.Installer.Core.Existing;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class ExistingFolderModuleTests
{
    private const string Folder = @"E:\AI\ComfyUI";

    private readonly Mock<IExistingComfyUIInspector> _inspector = new();
    private readonly Mock<IUserSettingsRepository> _settings = new();
    private readonly UserSettings _user = new();

    public ExistingFolderModuleTests()
    {
        _settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_user);
        _inspector.Setup(i => i.InspectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string folder, CancellationToken _) => Supported(folder));
    }

    private static ExistingComfyUIProfile Supported(string folder) =>
        new(folder, ExistingInstallSupport.Supported, folder, folder + @"\venv", "3.12.7", new InstalledTorchStack("2.8.0", "12.8", false, false),
            ManagerInstalled: true, UpdateScript: UpdateScriptState.Legacy);

    private static WizardSelection Selection()
    {
        var workload = new InstallationConfiguration { Name = "A", Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI } };
        workload.ModelDownloads.Add(new ModelDownload { Name = "m", Url = "https://h/m.safetensors" });
        var union = ExtendConfigurationMerger.Union([new WorkloadChoice(workload, 0)]);
        var selection = new WizardSelection { Workload = union.Configuration, Mode = WizardMode.UpdateExisting };
        selection.Existing = new ExistingRun(selection, union, [workload], null);
        return selection;
    }

    private async Task<(ExistingFolderModule Module, WizardSelection Selection)> ModuleAsync()
    {
        var module = new ExistingFolderModule(_inspector.Object, _settings.Object);
        var selection = Selection();
        await module.InitializeAsync(selection);
        return (module, selection);
    }

    private static async Task Choose(ExistingFolderModule module, string folder)
    {
        module.Folder = folder;
        await module.WhenAnswered;
    }

    [Fact]
    public void It_exists_only_on_the_update_path()
    {
        var module = new ExistingFolderModule(_inspector.Object, _settings.Object);

        module.UsedIn(WizardMode.UpdateExisting).Should().BeTrue();
        module.UsedIn(WizardMode.FreshInstall).Should().BeFalse();
        module.UsedIn(WizardMode.ModelsOnly).Should().BeFalse();
    }

    [Fact]
    public async Task A_blank_box_asks_for_a_folder_and_inspects_nothing()
    {
        var (module, _) = await ModuleAsync();

        module.Validate().ErrorMessage.Should().Be("Choose your ComfyUI folder.");
        _inspector.Verify(i => i.InspectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_relative_path_is_refused_without_inspecting()
    {
        var (module, _) = await ModuleAsync();

        await Choose(module, "AI");

        module.Validate().IsValid.Should().BeFalse();
        _inspector.Verify(i => i.InspectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_supported_folder_is_valid_and_sets_the_target()
    {
        var (module, selection) = await ModuleAsync();

        await Choose(module, Folder);

        module.Validate().IsValid.Should().BeTrue();
        selection.TargetFolder.Should().Be(Folder);
        selection.Existing!.Profile!.RepositoryPath.Should().Be(Folder);
    }

    [Theory]
    [InlineData(ExistingInstallSupport.NotComfyUI, "This folder does not contain ComfyUI (no main.py).")]
    [InlineData(ExistingInstallSupport.Portable, "Portable ComfyUI is not supported yet. Do a fresh install instead.")]
    [InlineData(ExistingInstallSupport.Desktop, "ComfyUI Desktop (the download from comfy.org) is not supported yet. Do a fresh install instead.")]
    [InlineData(ExistingInstallSupport.NoVenv, "No Python virtual environment found in this folder (conda and similar are not supported yet). Do a fresh install instead.")]
    public async Task An_unsupported_folder_says_why(ExistingInstallSupport support, string message)
    {
        _inspector.Setup(i => i.InspectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExistingComfyUIProfile(Folder, support));
        var (module, selection) = await ModuleAsync();

        await Choose(module, Folder);

        module.Validate().ErrorMessage.Should().Be(message);
        module.ShowsFreshInstallLink.Should().BeTrue();
        selection.TargetFolder.Should().BeEmpty();
    }

    [Fact]
    public async Task Next_stays_off_while_checking()
    {
        var slow = new TaskCompletionSource<ExistingComfyUIProfile>();
        _inspector.Setup(i => i.InspectAsync(Folder, It.IsAny<CancellationToken>())).Returns(slow.Task);
        var (module, _) = await ModuleAsync();

        module.Folder = Folder;

        module.IsChecking.Should().BeTrue();
        module.Validate().IsValid.Should().BeFalse();

        slow.SetResult(Supported(Folder));
        await module.WhenAnswered;

        module.IsChecking.Should().BeFalse();
        module.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Typing_does_not_wait_for_a_slow_folder()
    {
        var never = new TaskCompletionSource<ExistingComfyUIProfile>();
        _inspector.Setup(i => i.InspectAsync(Folder, It.IsAny<CancellationToken>())).Returns(never.Task);
        var (module, _) = await ModuleAsync();

        var set = Task.Run(() => module.Folder = Folder);

        (await Task.WhenAny(set, Task.Delay(TimeSpan.FromSeconds(5)))).Should().Be(set);
    }

    [Fact]
    public async Task A_late_answer_about_a_folder_the_user_has_left_is_ignored()
    {
        var slow = new TaskCompletionSource<ExistingComfyUIProfile>();
        _inspector.Setup(i => i.InspectAsync(@"E:\Slow", It.IsAny<CancellationToken>())).Returns(slow.Task);
        var (module, selection) = await ModuleAsync();

        module.Folder = @"E:\Slow";
        await Choose(module, Folder);
        slow.SetResult(Supported(@"E:\Slow"));
        await Task.Delay(50);

        selection.TargetFolder.Should().Be(Folder);
        selection.Existing!.Profile!.RepositoryPath.Should().Be(Folder);
    }

    [Fact]
    public async Task Another_folder_clears_the_earlier_answer_while_it_is_checked()
    {
        var slow = new TaskCompletionSource<ExistingComfyUIProfile>();
        _inspector.Setup(i => i.InspectAsync(@"E:\Slow", It.IsAny<CancellationToken>())).Returns(slow.Task);
        var (module, selection) = await ModuleAsync();
        await Choose(module, Folder);

        module.Folder = @"E:\Slow";

        selection.Existing!.Profile.Should().BeNull();
        selection.TargetFolder.Should().BeEmpty();
    }

    [Fact]
    public async Task An_inspection_that_throws_is_a_problem_not_a_crash()
    {
        _inspector.Setup(i => i.InspectAsync(Folder, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("gone"));
        var (module, _) = await ModuleAsync();

        await Choose(module, Folder);

        module.Validate().ErrorMessage.Should().Contain("could not be checked");
    }

    [Fact]
    public async Task It_starts_from_the_remembered_folder()
    {
        _user.LastExistingComfyUIFolder = Folder;

        var (module, selection) = await ModuleAsync();
        await module.WhenAnswered;

        module.Folder.Should().Be(Folder);
        selection.TargetFolder.Should().Be(Folder);
    }

    [Fact]
    public async Task A_fresh_installs_folder_is_not_used()
    {
        _user.DefaultTargetInstallFolder = @"E:\Fresh";

        var (module, _) = await ModuleAsync();

        module.Folder.Should().BeEmpty();
    }

    [Fact]
    public async Task Next_remembers_the_folder_and_nothing_else()
    {
        _user.DefaultTargetInstallFolder = @"E:\Fresh";
        var (module, _) = await ModuleAsync();
        await Choose(module, Folder);

        _settings.Verify(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Never);

        await module.PersistAsync();

        _user.LastExistingComfyUIFolder.Should().Be(Folder);
        _user.DefaultTargetInstallFolder.Should().Be(@"E:\Fresh");
        _settings.Verify(s => s.SaveAsync(_user, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_quoted_pasted_path_is_cleaned()
    {
        var (module, selection) = await ModuleAsync();

        await Choose(module, $"\"{Folder}\" ");

        selection.TargetFolder.Should().Be(Folder);
    }

    [Fact]
    public async Task Coming_back_checks_again()
    {
        var (module, _) = await ModuleAsync();
        await Choose(module, Folder);

        await module.RefreshAfterResumeAsync();
        await module.WhenAnswered;

        _inspector.Verify(i => i.InspectAsync(Folder, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task The_summary_says_what_was_found()
    {
        var (module, _) = await ModuleAsync();
        await Choose(module, Folder);

        module.Summary.Should().Contain(("Torch", "2.8.0 with CUDA 12.8"));
        module.Summary.Should().Contain(("Python", "3.12.7"));
        module.Summary.Should().Contain(("Update script", "An old one, which we'll replace"));
        module.Summary.Should().Contain(("ComfyUI-Manager", "Installed"));
    }
}
