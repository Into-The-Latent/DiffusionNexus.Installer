using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Host;

/// <summary>The user's per-file answers, keyed by download URL — the file's identity, not the model's.</summary>
public sealed record MismatchResolution(HashSet<string> RedownloadUrls, HashSet<string> TrustedUrls);

/// <summary>
/// One dialog listing every already-present file whose size differs from the server's, with a
/// redownload-or-keep choice per file. Shown before an install starts, never mid-install.
/// </summary>
public interface IMismatchedFilePrompt
{
    /// <summary>Null means the user dismissed the dialog, which cancels the install.</summary>
    Task<MismatchResolution?> ResolveAsync(IReadOnlyList<ExistingModelMismatch> mismatches, CancellationToken ct = default);
}
