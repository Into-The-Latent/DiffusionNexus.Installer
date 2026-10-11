using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

public static class LocalCatalogStateExtensions
{
    /// <summary>
    /// Whether the state describes an installed catalog at all: a section was recorded. False for the
    /// empty state <see cref="LocalCatalogState.Load"/> answers for a folder that does not exist yet
    /// (with SDK 2.3.0 shared folders, a channel never followed before) -- which describes nothing.
    /// </summary>
    public static bool RecordsAnything(this LocalCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Workloads is not null || state.Workflows is not null;
    }
}
