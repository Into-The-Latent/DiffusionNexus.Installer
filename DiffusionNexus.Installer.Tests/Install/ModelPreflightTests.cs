using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Install;

public class ModelPreflightTests
{
    private static readonly ModelDownload Vae = new() { Name = "VAE", Destination = @"models\vae", Url = "https://h.invalid/ae.safetensors" };

    private static async Task<(WizardPlan Plan, ModelSelectionModule Module)> PlanAsync(bool vaePresent, WizardMode mode = WizardMode.FreshInstall)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        w.ModelDownloads.Add(Vae);

        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns(
        [
            new ModelPresence(Vae.Id, vaePresent, vaePresent ? @"C:\AI\ComfyUI\models\vae\ae.safetensors" : null,
                [new ModelFileTarget(Vae, Vae.Url, @"C:\AI\ComfyUI\models\vae", "ae.safetensors", vaePresent ? @"C:\AI\ComfyUI\models\vae\ae.safetensors" : null)]),
        ]);
        var module = new ModelSelectionModule(scanner.Object, Mock.Of<IDiskSpaceEstimator>()) { ScanWait = TimeSpan.FromSeconds(30) };

        var registry = new WizardModuleRegistry(() => [module]);
        var plan = await registry.BuildPlanAsync(new WizardSelection { Workload = w, TargetFolder = @"C:\AI", Mode = mode });
        return (plan, module);
    }

    private static ExistingModelMismatch Mismatch() =>
        new(Vae, @"C:\AI\ComfyUI\models\vae\ae.safetensors", 10, 20, Vae.Url);

    [Fact]
    public async Task Models_only_keeps_a_file_of_another_size_without_asking_and_marks_it()
    {
        // A same-named file of another size is usually partial on a fresh install. In a folder the
        // user chose it is usually a different build they want to keep, and the prompt's default
        // would overwrite it with one click on Continue. So no prompt: the report row warns instead.
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Mismatch()]);
        var prompt = new Mock<IMismatchedFilePrompt>(MockBehavior.Strict);
        var (plan, _) = await PlanAsync(vaePresent: true, WizardMode.ModelsOnly);

        var result = await new ModelPreflight(verifier.Object, prompt.Object).RunAsync(plan);

        result.Proceed.Should().BeTrue();
        result.Warning.Should().BeNull();
        plan.ToOptions().ForceRedownloadUrls.Should().BeEmpty("nothing may be overwritten");
        plan.ToOptions().TrustedUrls.Should().BeEquivalentTo([Vae.Url]);
    }

    [Fact]
    public async Task Models_only_replaces_a_file_that_cannot_load_and_keeps_the_rest()
    {
        const string Other = "https://h.invalid/other.safetensors";
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Mismatch() with { Verdict = ExistingModelVerdict.Truncated },
                Mismatch() with { Url = Other, Verdict = ExistingModelVerdict.CompleteDifferent },
            ]);
        var prompt = new Mock<IMismatchedFilePrompt>(MockBehavior.Strict);
        var (plan, _) = await PlanAsync(vaePresent: true, WizardMode.ModelsOnly);

        await new ModelPreflight(verifier.Object, prompt.Object).RunAsync(plan);

        plan.ToOptions().ForceRedownloadUrls.Should().BeEquivalentTo([Vae.Url]);
        plan.ToOptions().TrustedUrls.Should().BeEquivalentTo([Other]);
    }

    [Fact]
    public async Task Models_only_says_before_the_run_that_an_incomplete_file_downloads_again()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Mismatch() with { Verdict = ExistingModelVerdict.Truncated },
                Mismatch() with { Url = "https://h.invalid/kept", FilePath = @"C:\AI\kept.safetensors", Verdict = ExistingModelVerdict.CompleteDifferent },
            ]);
        var (plan, _) = await PlanAsync(vaePresent: true, WizardMode.ModelsOnly);

        var result = await new ModelPreflight(verifier.Object, Mock.Of<IMismatchedFilePrompt>()).RunAsync(plan);

        result.Proceed.Should().BeTrue();
        result.Warning.Should().BeNull("nothing went wrong");
        result.Notice.Should().Be("ae.safetensors is incomplete, so this run replaces it.");
    }

    [Fact]
    public async Task Models_only_names_every_incomplete_file_once()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Mismatch() with { Verdict = ExistingModelVerdict.Truncated },
                Mismatch() with { Url = "https://h.invalid/same", Verdict = ExistingModelVerdict.Truncated },
                Mismatch() with { Url = "https://h.invalid/b", FilePath = @"C:\AI\b.gguf", Verdict = ExistingModelVerdict.Truncated },
            ]);
        var (plan, _) = await PlanAsync(vaePresent: true, WizardMode.ModelsOnly);

        var result = await new ModelPreflight(verifier.Object, Mock.Of<IMismatchedFilePrompt>()).RunAsync(plan);

        result.Notice.Should().Be("These files are incomplete, so this run replaces them: ae.safetensors, b.gguf.");
    }

    private static InstallSession Session()
    {
        var orchestrator = new Mock<SDK.Services.IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<SDK.Services.InstallationOptions>(),
                It.IsAny<IProgress<SDK.Services.InstallLogEntry>>(), It.IsAny<IProgress<SDK.Services.InstallationProgress>>(),
                It.IsAny<IProgress<SDK.Services.DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SDK.Services.InstallationResult.Success("done"));
        return new InstallSession(orchestrator.Object);
    }

    // A folder that isn't there: a session writes its log file into a real one.
    private static void NoLogFile(WizardPlan plan) =>
        plan.Selection.TargetFolder = Path.Combine(Path.GetTempPath(), "ModelPreflightTests_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_page_that_left_during_the_check_leaves_nothing_on_the_module()
    {
        using var cts = new CancellationTokenSource();
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                cts.Cancel();
                return [Mismatch() with { Verdict = ExistingModelVerdict.Truncated }];
            });
        var (plan, _) = await PlanAsync(vaePresent: true, WizardMode.ModelsOnly);

        var act = () => new ModelPreflight(verifier.Object, Mock.Of<IMismatchedFilePrompt>()).RunAsync(plan, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        plan.ToOptions().ForceRedownloadUrls.Should().BeEmpty();
        plan.ToOptions().ForceRedownloadFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task The_file_a_run_replaces_is_named_for_the_download_on_models_only()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Mismatch() with { Verdict = ExistingModelVerdict.Truncated },
                Mismatch() with { Url = "https://h.invalid/kept", FilePath = @"C:\AI\kept.safetensors", Verdict = ExistingModelVerdict.CompleteDifferent },
            ]);
        var (plan, _) = await PlanAsync(vaePresent: true, WizardMode.ModelsOnly);

        await new ModelPreflight(verifier.Object, Mock.Of<IMismatchedFilePrompt>()).RunAsync(plan);

        plan.ToOptions().ForceRedownloadFiles.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>(Vae.Url, @"C:\AI\ComfyUI\models\vae\ae.safetensors"));
    }

    [Fact]
    public async Task The_file_a_run_replaces_is_named_for_the_download_from_the_dialog_and_a_kept_one_is_not()
    {
        const string Kept = "https://h.invalid/kept";
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Mismatch(), Mismatch() with { Url = Kept, FilePath = @"C:\AI\kept.safetensors" }]);
        var prompt = new Mock<IMismatchedFilePrompt>();
        prompt.Setup(p => p.ResolveAsync(It.IsAny<IReadOnlyList<ExistingModelMismatch>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MismatchResolution([Vae.Url], [Kept]));
        var (plan, _) = await PlanAsync(vaePresent: true);

        await new ModelPreflight(verifier.Object, prompt.Object).RunAsync(plan);

        plan.ToOptions().ForceRedownloadFiles.Should().ContainSingle().Which.Key.Should().Be(Vae.Url);
    }

    [Fact]
    public async Task A_link_judged_on_two_files_names_neither_so_nothing_is_deleted()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Mismatch() with { Verdict = ExistingModelVerdict.Truncated },
                Mismatch() with { FilePath = @"C:\AI\other\ae.safetensors", Verdict = ExistingModelVerdict.Truncated },
            ]);
        var (plan, _) = await PlanAsync(vaePresent: true, WizardMode.ModelsOnly);

        await new ModelPreflight(verifier.Object, Mock.Of<IMismatchedFilePrompt>()).RunAsync(plan);

        plan.ToOptions().ForceRedownloadUrls.Should().ContainSingle();
        plan.ToOptions().ForceRedownloadFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_check_forgets_the_file_the_first_one_named()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.SetupSequence(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Mismatch() with { Verdict = ExistingModelVerdict.Truncated }])
            .ReturnsAsync([]);
        var (plan, _) = await PlanAsync(vaePresent: true, WizardMode.ModelsOnly);
        var preflight = new ModelPreflight(verifier.Object, Mock.Of<IMismatchedFilePrompt>());

        await preflight.RunAsync(plan);
        await preflight.RunAsync(plan);

        plan.ToOptions().ForceRedownloadFiles.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WizardMode.FreshInstall)]
    [InlineData(WizardMode.ModelsOnly)]
    public async Task The_sizes_reach_the_install_log_on_every_path(WizardMode mode)
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Mismatch() with { Verdict = ExistingModelVerdict.CompleteDifferent }]);
        var prompt = new Mock<IMismatchedFilePrompt>();
        prompt.Setup(p => p.ResolveAsync(It.IsAny<IReadOnlyList<ExistingModelMismatch>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MismatchResolution([], [Vae.Url]));
        var (plan, _) = await PlanAsync(vaePresent: true, mode);
        NoLogFile(plan);
        using var session = Session();

        await new ModelPreflight(verifier.Object, prompt.Object).RunAsync(plan);
        await session.StartAsync(plan);

        session.LogLines.Should().ContainSingle(l =>
            l.Message.Contains("ae.safetensors") && l.Message.Contains("CompleteDifferent")
            && l.Message.Contains("10 bytes") && l.Message.Contains("20 bytes"));
    }

    [Fact]
    public async Task A_second_check_that_finds_nothing_leaves_nothing_in_the_log()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.SetupSequence(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Mismatch()])
            .ReturnsAsync([]);
        var prompt = new Mock<IMismatchedFilePrompt>();
        prompt.Setup(p => p.ResolveAsync(It.IsAny<IReadOnlyList<ExistingModelMismatch>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MismatchResolution([], [Vae.Url]));
        var (plan, _) = await PlanAsync(vaePresent: true);
        NoLogFile(plan);
        using var session = Session();
        var preflight = new ModelPreflight(verifier.Object, prompt.Object);

        await preflight.RunAsync(plan);
        await preflight.RunAsync(plan);
        await session.StartAsync(plan);

        session.LogLines.Should().NotContain(l => l.Message.Contains("ae.safetensors"));
    }

    [Fact]
    public async Task Models_only_with_a_file_of_the_right_size_marks_nothing()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var (plan, _) = await PlanAsync(vaePresent: true, WizardMode.ModelsOnly);

        await new ModelPreflight(verifier.Object, new Mock<IMismatchedFilePrompt>(MockBehavior.Strict).Object).RunAsync(plan);

        plan.ToOptions().TrustedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task No_files_on_disk_means_no_verification_and_no_prompt()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        var prompt = new Mock<IMismatchedFilePrompt>();
        var (plan, _) = await PlanAsync(vaePresent: false);

        var result = await new ModelPreflight(verifier.Object, prompt.Object).RunAsync(plan);

        result.Proceed.Should().BeTrue();
        result.Warning.Should().BeNull();
        verifier.Verify(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()), Times.Never);
        prompt.Verify(p => p.ResolveAsync(It.IsAny<IReadOnlyList<ExistingModelMismatch>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Matching_files_proceed_without_a_prompt()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var prompt = new Mock<IMismatchedFilePrompt>();
        var (plan, _) = await PlanAsync(vaePresent: true);

        var result = await new ModelPreflight(verifier.Object, prompt.Object).RunAsync(plan);

        result.Proceed.Should().BeTrue();
        verifier.Verify(v => v.VerifyAsync(It.Is<IReadOnlyList<ExistingModelCandidate>>(c => c.Single().Url == Vae.Url), It.IsAny<CancellationToken>()), Times.Once);
        prompt.Verify(p => p.ResolveAsync(It.IsAny<IReadOnlyList<ExistingModelMismatch>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Mismatches_prompt_once_and_the_answer_reaches_the_options()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Mismatch()]);
        var prompt = new Mock<IMismatchedFilePrompt>();
        prompt.Setup(p => p.ResolveAsync(It.IsAny<IReadOnlyList<ExistingModelMismatch>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MismatchResolution([Vae.Url], []));
        var (plan, _) = await PlanAsync(vaePresent: true);

        var result = await new ModelPreflight(verifier.Object, prompt.Object).RunAsync(plan);

        result.Proceed.Should().BeTrue();
        plan.ToOptions().ForceRedownloadUrls.Should().BeEquivalentTo([Vae.Url]);
        prompt.Verify(p => p.ResolveAsync(It.Is<IReadOnlyList<ExistingModelMismatch>>(m => m.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_dismissed_dialog_does_not_proceed()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Mismatch()]);
        var prompt = new Mock<IMismatchedFilePrompt>();
        prompt.Setup(p => p.ResolveAsync(It.IsAny<IReadOnlyList<ExistingModelMismatch>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MismatchResolution?)null);
        var (plan, _) = await PlanAsync(vaePresent: true);

        var result = await new ModelPreflight(verifier.Object, prompt.Object).RunAsync(plan);

        result.Proceed.Should().BeFalse();
        plan.ToOptions().ForceRedownloadUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_verification_proceeds_with_a_warning()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        var (plan, _) = await PlanAsync(vaePresent: true);

        var result = await new ModelPreflight(verifier.Object, Mock.Of<IMismatchedFilePrompt>()).RunAsync(plan);

        result.Proceed.Should().BeTrue();
        result.Warning.Should().Contain("offline");
    }

    [Fact]
    public async Task A_plan_without_a_model_module_proceeds_untouched()
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.Fooocus;
        var plan = await new WizardModuleRegistry(() => []).BuildPlanAsync(new WizardSelection { Workload = w });

        var result = await new ModelPreflight(Mock.Of<IExistingModelVerifier>(), Mock.Of<IMismatchedFilePrompt>()).RunAsync(plan);

        result.Proceed.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var (plan, _) = await PlanAsync(vaePresent: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => new ModelPreflight(verifier.Object, Mock.Of<IMismatchedFilePrompt>()).RunAsync(plan, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
