using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Install;

/// <param name="Proceed">False only when the user dismissed the mismatch dialog.</param>
/// <param name="Warning">Set when verification itself failed; the install still proceeds.</param>
/// <param name="Notice">Something the run is about to do that nobody was asked about. Nothing is wrong.</param>
public sealed record PreflightResult(bool Proceed, string? Warning, string? Notice = null);

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
/// <para>
/// No dialog on models only. A file that stops early can't load, so it's replaced. Anything else stays
/// and its report row warns, because in a folder the user chose it's usually a different file of that name.
/// </para>
/// <para>
/// Every mismatch's sizes go into the install log, saved with the run on an install or an update. Models
/// only saves no log, so there they're in Copy log. The dialog only shows them for files it has no
/// verdict on, because the verdict is what a person can act on.
/// </para>
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
        // Task.Run only observes ct before the scan starts. A page that went away meanwhile must
        // not touch a module the wizard it left behind (#45) may already be showing elsewhere.
        ct.ThrowIfCancellationRequested();
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

        // The dialog comes back empty for a page that left. This path has no dialog, and the module
        // may already be showing elsewhere (#45), where these entries name files a run may delete.
        ct.ThrowIfCancellationRequested();

        if (mismatches.Count == 0) return new PreflightResult(true, null);

        var notes = mismatches
            .Select(m => $"Existing model {m.FilePath}: {m.Verdict}, {m.ActualBytes} bytes on disk, {m.ExpectedBytes} bytes expected ({m.Url})")
            .ToList();

        if (plan.Selection.Mode == WizardMode.ModelsOnly)
        {
            var incomplete = mismatches.Where(m => m.Verdict == ExistingModelVerdict.Truncated).Select(m => m.Url).ToList();
            module.ApplyVerification(
                incomplete,
                mismatches.Where(m => m.Verdict != ExistingModelVerdict.Truncated).Select(m => m.Url),
                notes,
                JudgedFiles(mismatches, incomplete));
            return new PreflightResult(true, null, RedownloadNotice(mismatches));
        }

        var resolution = await prompt.ResolveAsync(mismatches, ct).ConfigureAwait(false);
        if (resolution is null) return new PreflightResult(false, null);

        module.ApplyVerification(
            resolution.RedownloadUrls, resolution.TrustedUrls, notes, JudgedFiles(mismatches, resolution.RedownloadUrls));
        return new PreflightResult(true, null);
    }

    /// <summary>
    /// The file each replaced link was judged on, the only file the run may delete. A link judged on
    /// two files can't say which one it replaces, so it names neither and both stay.
    /// </summary>
    private static Dictionary<string, string> JudgedFiles(IReadOnlyList<ExistingModelMismatch> mismatches, IEnumerable<string> replaced)
    {
        var urls = replaced.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return mismatches
            .Where(m => urls.Contains(m.Url))
            .GroupBy(m => m.Url, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(m => m.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().FilePath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The Models panel told this person existing files aren't downloaded again, and no dialog corrects that here.</summary>
    private static string? RedownloadNotice(IReadOnlyList<ExistingModelMismatch> mismatches)
    {
        var names = mismatches
            .Where(m => m.Verdict == ExistingModelVerdict.Truncated)
            .Select(m => Path.GetFileName(m.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count switch
        {
            0 => null,
            1 => $"{names[0]} is incomplete, so this run downloads it again.",
            _ => $"These files are incomplete, so this run downloads them again: {string.Join(", ", names)}.",
        };
    }
}
