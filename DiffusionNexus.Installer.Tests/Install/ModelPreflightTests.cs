using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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

    private sealed class RecordingLogger : ILogger<ModelPreflight>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    [Theory]
    [InlineData(WizardMode.FreshInstall)]
    [InlineData(WizardMode.ModelsOnly)]
    public async Task The_sizes_go_to_the_log_on_every_path(WizardMode mode)
    {
        var verifier = new Mock<IExistingModelVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IReadOnlyList<ExistingModelCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Mismatch() with { Verdict = ExistingModelVerdict.CompleteDifferent }]);
        var prompt = new Mock<IMismatchedFilePrompt>();
        var logger = new RecordingLogger();
        var (plan, _) = await PlanAsync(vaePresent: true, mode);

        await new ModelPreflight(verifier.Object, prompt.Object, logger).RunAsync(plan);

        logger.Lines.Should().ContainSingle(l =>
            l.Contains("ae.safetensors") && l.Contains("CompleteDifferent") && l.Contains("10") && l.Contains("20"));
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
