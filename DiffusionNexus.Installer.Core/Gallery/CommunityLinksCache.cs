using DiffusionNexus.Installer.SDK.Shared.Services;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>
/// The process-wide copy of the "Join the Community" link list. Every screen carries the footer
/// through <c>ScreenShell</c>, so without this each navigation would hit the Gist again; with it
/// the fetch runs once, on the first screen, and later screens read whatever it produced.
///
/// <see cref="Links"/> is never empty and never waits: it holds <see cref="CommunityLink.Defaults"/>
/// until the fetch lands, and forever when the fetch fails. The footer therefore renders on the
/// first paint regardless of the network.
/// </summary>
public sealed class CommunityLinksCache
{
    private readonly ICommunityLinksService _service;
    private readonly Lazy<Task> _load;

    public CommunityLinksCache(ICommunityLinksService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _load = new Lazy<Task>(LoadCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The list to render right now. Defaults until the fetch replaces them.</summary>
    public IReadOnlyList<CommunityLink> Links { get; private set; } = CommunityLink.Defaults;

    /// <summary>What the fetch produced, or null while it is still running / never started.</summary>
    public CommunityLinksResult? Result { get; private set; }

    /// <summary>
    /// Raised once, on the thread that completed the fetch, after <see cref="Links"/> changed.
    /// UI subscribers must marshal to their own dispatcher.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Starts the fetch if nothing has yet, and returns the one shared task. Safe to call from
    /// every footer render: only the first call does anything. The task never faults.
    /// </summary>
    public Task LoadAsync() => _load.Value;

    private async Task LoadCoreAsync()
    {
        CommunityLinksResult result;
        try
        {
            result = await _service.GetLinksAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The SDK service already swallows everything but caller cancellation, and we never
            // cancel. Belt and braces: a footer must not take a screen down.
            result = CommunityLinksResult.Fallback($"Community links fetch threw: {ex.Message}");
        }

        Result = result;
        Links = result.Links;
        Changed?.Invoke();
    }
}
