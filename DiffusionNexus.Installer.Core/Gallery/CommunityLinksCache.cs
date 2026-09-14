using DiffusionNexus.Installer.SDK.Shared.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger<CommunityLinksCache> _logger;
    private readonly Lazy<Task> _load;

    // One volatile reference published once, so a footer that initialises AFTER the fetch landed
    // (on whatever thread completed it) sees a consistent Links/Result pair, never one of each.
    private volatile CommunityLinksResult? _result;

    public CommunityLinksCache(ICommunityLinksService service, ILogger<CommunityLinksCache>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _logger = logger ?? NullLogger<CommunityLinksCache>.Instance;
        _load = new Lazy<Task>(LoadCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The list to render right now. Defaults until the fetch replaces them.</summary>
    public IReadOnlyList<CommunityLink> Links => _result?.Links ?? CommunityLink.Defaults;

    /// <summary>What the fetch produced, or null while it is still running / never started.</summary>
    public CommunityLinksResult? Result => _result;

    /// <summary>
    /// Raised once, on the thread that completed the fetch, after <see cref="Links"/> changed.
    /// UI subscribers must marshal to their own dispatcher. A subscriber that throws is logged
    /// and skipped; it neither faults the load nor starves the subscribers after it.
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

        // The only place the outcome is recorded. Without this, a renamed Gist file means every
        // installer silently shows the compiled-in six and nothing anywhere says why.
        if (result.IsFallback)
        {
            _logger.LogWarning("Community links: using the compiled-in defaults. {Reason}", result.ErrorMessage);
        }
        else
        {
            _logger.LogDebug("Community links: {Count} loaded from the remote document.", result.Links.Count);
        }

        _result = result;

        // Per handler, so one throwing footer cannot fault the cached task or hide the
        // notification from the footers after it in the invocation list.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A community-links subscriber threw and was skipped.");
            }
        }
    }
}
