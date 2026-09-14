using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.SDK.Shared.Services;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The community-links source a fixture registers when the footer is incidental to what it
/// tests: answers immediately with the compiled-in defaults, never touches the network.
/// </summary>
public sealed class OfflineCommunityLinks : ICommunityLinksService
{
    public Task<CommunityLinksResult> GetLinksAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CommunityLinksResult.Fallback("offline test double"));

    /// <summary>A cache over this source, ready to drop into a bUnit container.</summary>
    public static CommunityLinksCache Cache() => new(new OfflineCommunityLinks());
}
