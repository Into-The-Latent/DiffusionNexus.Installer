using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>
/// Scriptable stand-in for the SDK's update service. Set HoldCheck / HoldApply to keep a call
/// in flight until the test releases it, which is how the concurrency guards get exercised.
/// </summary>
internal sealed class FakeCatalogUpdateService : ICatalogUpdateService
{
    public Func<CatalogUpdateCheck> NextCheck { get; set; } = () => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate);

    /// <summary>What a check of an explicit channel (a switch preview) answers. Defaults to <see cref="NextCheck"/> for that channel.</summary>
    public Func<CatalogChannel, CatalogUpdateCheck>? NextPreview { get; set; }
    public Func<CatalogApplyResult> NextApply { get; set; } = () => new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);
    public TaskCompletionSource? HoldCheck { get; set; }
    public TaskCompletionSource? HoldApply { get; set; }
    public bool ThrowCancelledOnApply { get; set; }

    public int CheckCalls { get; private set; }
    public List<CatalogChannel> PreviewedChannels { get; } = [];
    public int ApplyCalls { get; private set; }
    public CatalogUpdateCheck? AppliedCheck { get; private set; }

    /// <summary>Runs as an apply starts, before it can be held: lets a test see what had happened by then.</summary>
    public Action? OnApply { get; set; }
    public CatalogSections? AppliedSections { get; private set; }
    public IProgress<CatalogDownloadProgress>? Progress { get; private set; }

    public async Task<CatalogUpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        CheckCalls++;
        if (HoldCheck is not null) await HoldCheck.Task;
        return NextCheck();
    }

    public async Task<CatalogUpdateCheck> CheckAsync(CatalogChannel channel, CancellationToken ct = default)
    {
        PreviewedChannels.Add(channel);
        if (HoldCheck is not null) await HoldCheck.Task;
        return NextPreview?.Invoke(channel) ?? NextCheck() with { Channel = channel };
    }

    public async Task<CatalogApplyResult> ApplyAsync(CatalogUpdateCheck check, CatalogSections sections,
        IProgress<CatalogDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ApplyCalls++;
        AppliedCheck = check;
        AppliedSections = sections;
        OnApply?.Invoke();
        Progress = progress;
        if (HoldApply is not null) await HoldApply.Task.WaitAsync(ct);   // the SDK lets a cancel through
        if (ThrowCancelledOnApply) throw new OperationCanceledException();
        return NextApply();
    }
}
