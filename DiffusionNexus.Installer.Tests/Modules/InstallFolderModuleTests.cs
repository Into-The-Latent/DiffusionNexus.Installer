using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Settings;
using DiffusionNexus.Installer.SDK.Shared;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class InstallFolderModuleTests
{
    private static WizardSelection Selection(string url = "https://github.com/comfyanonymous/ComfyUI")
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = url;
        return new WizardSelection { Workload = w };
    }

    private static async Task<InstallFolderModule> Module(WizardSelection selection, string remembered = "")
    {
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings { DefaultTargetInstallFolder = remembered });
        var module = new InstallFolderModule(settings.Object, new PreInstallationService()) { AnswerWait = TimeSpan.FromSeconds(30) };
        await module.InitializeAsync(selection);
        return module;
    }

    [Fact]
    public async Task The_destination_is_the_repo_folder_under_the_chosen_folder()
    {
        // The page shows this line so the user sees "E:\Installer\9\ComfyUI" before pressing Next,
        // instead of learning it from the "already exists" error.
        var module = await Module(Selection());

        module.TargetFolder = @"E:\Installer\9";

        module.DestinationFolder.Should().Be(@"E:\Installer\9\ComfyUI");
    }

    [Fact]
    public async Task A_null_install_folder_in_the_settings_file_loads_as_none_chosen()
    {
        // System.Text.Json loads "DefaultTargetInstallFolder": null as null.
        var module = await Module(Selection(), remembered: null!);

        module.TargetFolder.Should().BeEmpty();
        module.DestinationFolder.Should().BeNull();
        module.Validate().ErrorMessage.Should().Be("Choose a folder to install into.");
    }

    [Fact]
    public async Task A_path_pasted_with_Copy_as_path_quotes_is_the_same_folder()
    {
        var selection = Selection();
        var module = await Module(selection);

        module.TargetFolder = @" ""E:\Installer\9\"" ";

        selection.TargetFolder.Should().Be(@"E:\Installer\9");
        module.DestinationFolder.Should().Be(@"E:\Installer\9\ComfyUI");
    }

    [Fact]
    public async Task The_selection_and_the_destination_use_the_trimmed_folder()
    {
        // Review finding: a pasted trailing space made "Will be created" show one folder while
        // the pipeline created another and the presence scan walked a third.
        var selection = Selection();
        var module = await Module(selection);

        module.TargetFolder = @"E:\Installer\9 ";

        module.TargetFolder.Should().Be(@"E:\Installer\9 ", "the text box must not fight the user's keystrokes");
        selection.TargetFolder.Should().Be(@"E:\Installer\9");
        module.DestinationFolder.Should().Be(@"E:\Installer\9\ComfyUI");
    }

    [Fact]
    public async Task Persist_remembers_the_install_folder_for_the_next_run()
    {
        // Review finding: DefaultTargetInstallFolder was read at initialization and written
        // nowhere, so every install started from a stale default.
        var stored = new UserSettings { DefaultTargetInstallFolder = @"C:\Old" };
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>())).ReturnsAsync(stored);
        settings.Setup(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserSettings u, CancellationToken _) => u);
        var module = new InstallFolderModule(settings.Object, new PreInstallationService()) { AnswerWait = TimeSpan.FromSeconds(30) };
        await module.InitializeAsync(Selection());
        module.TargetFolder = @"E:\Installer\9 ";

        await module.PersistAsync();

        settings.Verify(s => s.SaveAsync(It.Is<UserSettings>(u => u.DefaultTargetInstallFolder == @"E:\Installer\9"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task There_is_no_destination_while_no_folder_is_chosen()
    {
        var module = await Module(Selection());

        module.TargetFolder = "   ";

        module.DestinationFolder.Should().BeNull();
    }

    [Theory]
    [InlineData("ComfyUI")]
    [InlineData(@"ComfyUI\")]
    [InlineData(@"ComfyUI\ComfyUI")]
    [InlineData("")]
    public async Task An_existing_install_is_refused_however_the_folder_is_spelled(string tail)
    {
        // The pipeline drops a trailing folder named after the repository (PathNormalizer), so
        // "X\ComfyUI" installs into X\ComfyUI. The pre-flight must look there, not at
        // X\ComfyUI\ComfyUI, or Next is enabled on top of a live installation.
        var root = Path.Combine(Path.GetTempPath(), $"dn-existing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "ComfyUI"));
        File.WriteAllText(Path.Combine(root, "ComfyUI", "main.py"), "");
        try
        {
            var module = await Module(Selection());

            module.TargetFolder = Path.Combine(root, tail);

            module.DestinationFolder.Should().Be(Path.Combine(root, "ComfyUI"));
            module.Validate().IsValid.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("AI")]
    [InlineData(@"""E:\x")]
    [InlineData(@"\\nas")]
    [InlineData(@"\\?\E:\AI")]
    public async Task An_install_folder_that_is_not_a_full_path_is_refused(string folder)
    {
        // A relative folder installed under the app's working directory.
        var module = await Module(Selection());

        module.TargetFolder = folder;

        module.Validate().ErrorMessage.Should().Contain("full path");
    }

    [Theory]
    [InlineData(@"E:\AI!new")]
    [InlineData(@"E:\AI 100%")]
    [InlineData(@"E:\pct%x")]
    [InlineData(@"E:\A^B")]
    [InlineData(@"D:\R&D")]
    public async Task An_install_folder_the_start_script_cannot_carry_is_refused(string folder)
    {
        // Run in real cmd: ComfyUI's run_nvidia.bat (enabledelayedexpansion, then
        // call "%~dp0venv\...") never activates the venv in a folder with ! % or ^, and the
        // A1111/Forge webui-user.bat's set "PYTHON="%~dp0venv\..."" leaves %~dp0 unquoted, so an
        // & splits that line.
        var module = await Module(Selection());

        module.TargetFolder = folder;

        module.Validate().ErrorMessage.Should().Contain("& ! % or ^");
        module.DestinationFolder.Should().BeNull();
    }

    [Theory]
    [InlineData("https://github.com/comfyanonymous/ComfyUI", RepositoryType.ComfyUI, @"E:\AI\ComfyUI\ComfyUI", @"E:\AI\ComfyUI")]
    [InlineData("https://github.com/ostris/ai-toolkit", RepositoryType.AIToolkit, @"E:\AI\AI-Toolkit\AI-Toolkit", @"E:\AI\ai-toolkit")]
    public async Task A_folder_named_after_the_repository_twice_is_refused_naming_the_real_one(
        string url, RepositoryType type, string chosen, string install)
    {
        // The SDK strips the name twice, so the install lands one level up and the chosen folder
        // is never created: no log file, a finished-screen button into nothing, and for
        // AI-Toolkit an embedded Python written where the clone then refuses to go.
        var selection = Selection(url);
        selection.Workload.Repository.Type = type;
        var module = await Module(selection);

        module.TargetFolder = chosen;

        module.Validate().ErrorMessage.Should().Contain("one level up").And.Contain(install);
    }

    [Theory]
    [InlineData(@"E:\My AI")]
    [InlineData(@"E:\AI-#2 (new)")]
    public async Task An_install_folder_with_spaces_and_ordinary_punctuation_is_fine(string folder)
    {
        var module = await Module(Selection());

        module.TargetFolder = folder;

        module.Validate().IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("AI")]
    [InlineData(@"\\?\E:\AI")]
    [InlineData(@"C:\a<b")]
    public async Task A_refused_folder_shows_no_will_be_created_line(string folder)
    {
        // "Will be created: AI\ComfyUI" right under "Enter the full path…" contradicted it.
        var module = await Module(Selection());

        module.TargetFolder = folder;

        module.Validate().IsValid.Should().BeFalse();
        module.DestinationFolder.Should().BeNull();
    }

    [Fact]
    public async Task An_install_folder_Windows_cannot_name_is_refused()
    {
        var module = await Module(Selection());

        module.TargetFolder = @"C:\a<b";

        module.Validate().ErrorMessage.Should().Be(FolderInput.InvalidNameMessage);
    }

    [Fact]
    public async Task The_destination_follows_the_remembered_folder_right_after_initialization()
    {
        var module = await Module(Selection(), remembered: @"C:\AI");

        module.DestinationFolder.Should().Be(@"C:\AI\ComfyUI");
    }

    // ----- the folder check runs off the page (#60) -----

    private const string Nas = @"\\nas\share\AI";
    private const string OtherNas = @"\\other\share\AI";

    private static readonly PreInstallationValidationResult Fine = PreInstallationValidationResult.Success(@"E:\x", false, false);

    private static Mock<IPreInstallationService> FolderCheck(Func<string, PreInstallationValidationResult> answer)
    {
        var check = new Mock<IPreInstallationService>();
        check.Setup(c => c.ValidateTargetFolder(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationType>()))
            .Returns((InstallationConfiguration _, string folder, InstallationType _) => answer(folder));
        return check;
    }

    private static async Task<InstallFolderModule> ModuleChecking(
        Mock<IPreInstallationService> check, TimeSpan? answerWait = null, TimeSpan? checkLimit = null)
    {
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings { DefaultTargetInstallFolder = string.Empty });
        var module = new InstallFolderModule(settings.Object, check.Object)
        {
            AnswerWait = answerWait ?? TimeSpan.FromMilliseconds(10),
            CheckLimit = checkLimit ?? TimeSpan.FromSeconds(60),
        };
        await module.InitializeAsync(Selection());
        return module;
    }

    /// <summary>What the page hears when an answer it was not given straight away arrives.</summary>
    private static Task Answered(InstallFolderModule module)
    {
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        module.FolderCheckAnswered += () => answered.TrySetResult();
        return answered.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_folder_slow_to_answer_does_not_hold_up_the_page()
    {
        // The reported freeze: an unreachable network path takes about 21 seconds to answer, and the
        // page could do nothing else until it did. The gate outlasts the time allowed below, so
        // with the check back on the caller's thread this fails rather than waits.
        using var gate = new ManualResetEventSlim();
        var module = await ModuleChecking(FolderCheck(_ => { gate.Wait(TimeSpan.FromSeconds(10)); return Fine; }));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        module.TargetFolder = Nas;
        var validation = module.Validate();
        var checking = module.IsCheckingFolder;
        watch.Stop();
        gate.Set();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        validation.IsValid.Should().BeFalse("Next must not let a folder through before it has an answer");
        validation.ErrorMessage.Should().NotBeNullOrEmpty("WizardRun lists the message of everything invalid");
        checking.Should().BeTrue();
    }

    [Fact]
    public async Task The_answer_is_announced_when_it_arrives()
    {
        using var gate = new ManualResetEventSlim();
        var module = await ModuleChecking(FolderCheck(_ => { gate.Wait(TimeSpan.FromSeconds(30)); return Fine; }));
        var answered = Answered(module);
        module.TargetFolder = Nas;
        module.IsCheckingFolder.Should().BeTrue();

        gate.Set();
        await answered;

        module.IsCheckingFolder.Should().BeFalse();
        module.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_refusal_that_arrives_late_still_refuses()
    {
        using var gate = new ManualResetEventSlim();
        var module = await ModuleChecking(FolderCheck(_ =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return Fine with { CanProceed = false, ErrorMessage = "Already a live install." };
        }));
        var answered = Answered(module);
        module.TargetFolder = Nas;

        gate.Set();
        await answered;

        module.Validate().ErrorMessage.Should().Be("Already a live install.");
    }

    [Fact]
    public async Task While_a_folder_has_no_answer_the_next_change_does_not_wait_again()
    {
        // Typing under a server that does not answer is one new path per key. Waiting on each held
        // up every event behind it: renders fell behind the typing and clicks queued for seconds.
        using var gate = new ManualResetEventSlim();
        var module = await ModuleChecking(
            FolderCheck(_ => { gate.Wait(TimeSpan.FromSeconds(30)); return Fine; }),
            answerWait: TimeSpan.FromSeconds(2));
        module.TargetFolder = Nas;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        module.TargetFolder = Nas + "2";
        watch.Stop();
        gate.Set();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Coming_back_from_a_side_trip_checks_the_folder_again()
    {
        // The refusal said the folder is not empty; the user emptied it in Explorer during a trip to
        // Licences (#45). Nothing else would ask again, because the box has not changed.
        var calls = 0;
        var module = await ModuleChecking(
            FolderCheck(_ => Interlocked.Increment(ref calls) == 1
                ? Fine with { CanProceed = false, ErrorMessage = "Not empty." }
                : Fine),
            answerWait: TimeSpan.FromSeconds(30));
        module.TargetFolder = @"E:\AI\Test2";
        module.Validate().ErrorMessage.Should().Be("Not empty.");

        await ((IWizardModule)module).RefreshAfterResumeAsync();

        module.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_side_trip_check_that_gets_no_answer_keeps_the_earlier_one()
    {
        // The folder passed, on a NAS that sleeps during the trip. No answer says nothing new, and
        // IWizardModule asks that a probe that is not sure keeps what it had.
        using var gate = new ManualResetEventSlim();
        var calls = 0;
        var module = await ModuleChecking(
            FolderCheck(_ =>
            {
                if (Interlocked.Increment(ref calls) > 1) gate.Wait(TimeSpan.FromSeconds(30));
                return Fine;
            }),
            answerWait: TimeSpan.FromSeconds(30),
            checkLimit: TimeSpan.FromSeconds(2));
        module.TargetFolder = @"\\nas\share\AI";
        module.Validate().IsValid.Should().BeTrue();

        await ((IWizardModule)module).RefreshAfterResumeAsync();
        var validation = module.Validate();
        var timedOut = module.FolderCheckTimedOut;
        gate.Set();

        validation.IsValid.Should().BeTrue();
        timedOut.Should().BeFalse();
    }

    [Fact]
    public async Task An_answer_for_a_folder_the_user_has_left_is_ignored()
    {
        using var gate = new ManualResetEventSlim();
        var oldAnswerGiven = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = await ModuleChecking(FolderCheck(folder =>
        {
            if (!folder.StartsWith(Nas, StringComparison.Ordinal)) return Fine;
            gate.Wait(TimeSpan.FromSeconds(30));
            oldAnswerGiven.SetResult();
            return Fine with { CanProceed = false, ErrorMessage = "The folder the user left." };
        }), answerWait: TimeSpan.FromSeconds(1));
        var announcements = 0;
        var answered = Answered(module);

        module.TargetFolder = Nas;
        module.TargetFolder = OtherNas;
        await answered;
        module.Validate().IsValid.Should().BeTrue("the folder in the box has its answer");
        module.FolderCheckAnswered += () => Interlocked.Increment(ref announcements);

        gate.Set();
        await oldAnswerGiven.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(100);

        module.Validate().IsValid.Should().BeTrue();
        announcements.Should().Be(0);
    }

    [Fact]
    public async Task A_folder_that_never_answers_is_a_problem_and_the_page_says_so()
    {
        using var gate = new ManualResetEventSlim();
        var module = await ModuleChecking(
            FolderCheck(_ => { gate.Wait(TimeSpan.FromSeconds(30)); return Fine; }),
            checkLimit: TimeSpan.FromMilliseconds(50));
        var answered = Answered(module);

        module.TargetFolder = Nas;
        await answered;

        module.IsCheckingFolder.Should().BeFalse();
        module.FolderCheckTimedOut.Should().BeTrue();
        module.Validate().IsValid.Should().BeFalse();
        module.Validate().ErrorMessage.Should().Contain("did not answer");

        // Windows gives up on a dead computer and answers "no such folder", which is what a folder
        // still to be created answers too: a late answer must not turn this into a passed check.
        gate.Set();
        await Task.Delay(100);
        module.Validate().ErrorMessage.Should().Contain("did not answer");
    }

    [Fact]
    public async Task Trying_again_checks_the_same_folder_again()
    {
        using var gate = new ManualResetEventSlim();
        var calls = 0;
        var module = await ModuleChecking(
            FolderCheck(_ =>
            {
                if (Interlocked.Increment(ref calls) == 1) gate.Wait(TimeSpan.FromSeconds(30));
                return Fine;
            }),
            answerWait: TimeSpan.FromSeconds(10),
            checkLimit: TimeSpan.FromSeconds(2));
        module.TargetFolder = Nas;
        module.FolderCheckTimedOut.Should().BeTrue();

        module.CheckFolderAgain();
        gate.Set();

        module.FolderCheckTimedOut.Should().BeFalse();
        module.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_folder_that_answers_at_once_has_its_answer_before_the_next_render()
    {
        // No "checking" state to flicker for an ordinary folder, and nothing to announce.
        var module = await ModuleChecking(FolderCheck(_ => Fine), answerWait: TimeSpan.FromSeconds(30));
        var announcements = 0;
        module.FolderCheckAnswered += () => Interlocked.Increment(ref announcements);

        module.TargetFolder = @"E:\AI\Test2";

        module.IsCheckingFolder.Should().BeFalse();
        module.Validate().IsValid.Should().BeTrue();
        await Task.Delay(50);
        announcements.Should().Be(0);
    }

    [Fact]
    public async Task A_check_that_breaks_refuses_the_folder_instead_of_taking_the_page_down()
    {
        var module = await ModuleChecking(
            FolderCheck(_ => throw new InvalidOperationException("Something unexpected.")),
            answerWait: TimeSpan.FromSeconds(30));

        module.TargetFolder = @"E:\AI\Test2";

        module.Validate().ErrorMessage.Should().Contain("Something unexpected.");
    }

    [Fact]
    public async Task Spacing_around_the_same_folder_does_not_check_it_again()
    {
        var check = FolderCheck(_ => Fine);
        var module = await ModuleChecking(check, answerWait: TimeSpan.FromSeconds(30));

        module.TargetFolder = @"E:\AI\Test2";
        module.TargetFolder = @"E:\AI\Test2 ";
        module.Validate();
        module.Validate();

        check.Verify(c => c.ValidateTargetFolder(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationType>()), Times.Once);
    }

    [Theory]
    [InlineData("AI")]
    [InlineData(@"\\nas")]
    [InlineData(@"E:\AI\ComfyUI\ComfyUI")]
    public async Task A_folder_refused_from_its_text_alone_never_goes_to_the_disk(string folder)
    {
        var check = FolderCheck(_ => Fine);
        var module = await ModuleChecking(check, answerWait: TimeSpan.FromSeconds(30));

        module.TargetFolder = folder;

        module.IsCheckingFolder.Should().BeFalse();
        module.Validate().IsValid.Should().BeFalse();
        check.Verify(c => c.ValidateTargetFolder(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationType>()), Times.Never);
    }
}
