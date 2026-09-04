using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Install;

/// <param name="Proceed">False only when the user dismissed the mismatch dialog.</param>
/// <param name="Warning">Set when verification itself failed; the install still proceeds.</param>
public sealed record PreflightResult(bool Proceed, string? Warning);

/// <summary>Runs when the user leaves Confirm, before the install session starts.</summary>
public interface IModelPreflight
{
    Task<PreflightResult> RunAsync(WizardPlan plan, CancellationToken ct = default);
}

/// <summary>
/// 1.x's pre-install verification: every ticked model's files already on disk are size-checked
/// against the server, mismatches go into ONE dialog, and the answers land on the model module so
/// ToOptions carries them as ForceRedownloadUrls / TrustedUrls. Never a prompt per file, never
/// mid-install. Dismissing the dialog refuses to proceed; a failing check warns and proceeds.
/// </summary>
public sealed class ModelPreflight(IExistingModelVerifier verifier, IMismatchedFilePrompt prompt) : IModelPreflight
{
    public async Task<PreflightResult> RunAsync(WizardPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var module = plan.AllModules.OfType<ModelSelectionModule>().FirstOrDefault();
        if (module is null) return new PreflightResult(true, null);

        // The folder may have changed since the Content stage rendered; scan against what Confirm
        // shows. Off the render thread: a large library on a slow disk must not freeze the window
        // before the "Verifying..." hint can even paint.
        await Task.Run(module.RefreshPresence, ct).ConfigureAwait(false);
        module.ApplyVerification([], []);

        var candidates = module.ExistingTargetsForSelectedModels()
            .Select(t => new ExistingModelCandidate(t.Model, t.ExistingPath!, t.Url))
            .ToList();

        if (candidates.Count == 0) return new PreflightResult(true, null);

        IReadOnlyList<ExistingModelMismatch> mismatches;
        try
        {
            mismatches = await verifier.VerifyAsync(candidates, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PreflightResult(true, $"Could not verify existing model files: {ex.Message}");
        }

        if (mismatches.Count == 0) return new PreflightResult(true, null);

        var resolution = await prompt.ResolveAsync(mismatches, ct).ConfigureAwait(false);
        if (resolution is null) return new PreflightResult(false, null);

        module.ApplyVerification(resolution.RedownloadUrls, resolution.TrustedUrls);
        return new PreflightResult(true, null);
    }
}
