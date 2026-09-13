namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>One row in the "Join the Community" footer.</summary>
public sealed record CommunityLink(string Name, string Url)
{
    /// <summary>
    /// The links the 2.x installer ships, carried over verbatim. Hardcoded on purpose: the
    /// Linktree cannot be framed (x-frame-options: SAMEORIGIN) and its page JSON mixes five real
    /// rows with twenty-six sponsored placements. Issue #6 covers making these editable.
    /// </summary>
    public static IReadOnlyList<CommunityLink> Default { get; } =
    [
        new("YouTube", "https://www.youtube.com/@IntoTheLatent"),
        new("Patreon", "https://patreon.com/AIKnowledgeCentral"),
        new("Civitai", "https://civitai.com/user/AIknowlege2go")
    ];
}
