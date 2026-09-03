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

    private static async Task<(WizardPlan Plan, ModelSelectionModule Module)> PlanAsync(bool vaePresent)
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
        var module = new ModelSelectionModule(scanner.Object, Mock.Of<IDiskSpaceEstimator>());

        var registry = new WizardModuleRegistry(() => [module]);
        var plan = await registry.BuildPlanAsync(new WizardSelection { Workload = w, TargetFolder = @"C:\AI" });
        return (plan, module);
    }

    private static ExistingModelMismatch Mismatch() =>
        new(Vae, @"C:\AI\ComfyUI\models\vae\ae.safetensors", 10, 20, Vae.Url);

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
