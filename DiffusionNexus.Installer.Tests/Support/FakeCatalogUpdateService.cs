using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>
/// Scriptable stand-in for the SDK's update service. Set HoldCheck / HoldApply to keep a call
/// in flight until the test releases it, which is how the concurrency guards get exercised.
/// </summary>
internal sealed class FakeCatalogUpdateService : ICatalogUpdateService
{
    public Func<CatalogUpdateCheck> NextCheck { get; set; } = () => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate);
    public Func<CatalogApplyResult> NextApply { get; set; } = () => new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);
    public TaskCompletionSource? HoldCheck { get; set; }
    public TaskCompletionSource? HoldApply { get; set; }
    public bool ThrowCancelledOnApply { get; set; }

    public int CheckCalls { get; private set; }
    public int ApplyCalls { get; private set; }
    public CatalogSections? AppliedSections { get; private set; }
    public IProgress<CatalogDownloadProgress>? Progress { get; private set; }

    public async Task<CatalogUpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        CheckCalls++;
        if (HoldCheck is not null) await HoldCheck.Task;
        return NextCheck();
    }

    public async Task<CatalogApplyResult> ApplyAsync(CatalogUpdateCheck check, CatalogSections sections,
        IProgress<CatalogDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ApplyCalls++;
        AppliedSections = sections;
        Progress = progress;
        if (HoldApply is not null) await HoldApply.Task;
        if (ThrowCancelledOnApply) throw new OperationCanceledException();
        return NextApply();
    }
}
