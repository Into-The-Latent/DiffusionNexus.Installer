using System.Collections.Immutable;
using System.Text.Json;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>
/// Which installed content a check found current, per channel, in a file of the installer's own
/// next to user_settings.json. catalog-state.json records the channel an apply came from; an
/// up-to-date check applies nothing and records nothing, so content two channels share stays
/// stamped with the channel it first came from, and a seed with the pack's (PR #43 review). A
/// confirmation holds the sections as they were, so any apply since outdates it by itself.
/// Never throws: a file that cannot be read is no confirmations (a "still from" line the next
/// current check takes back), one that cannot be written holds for this run only.
/// </summary>
public sealed class CatalogChannelConfirmations
{
    public const string FileName = "catalog_channel_confirmations.json";

    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly Lock _write = new();
    private ImmutableDictionary<CatalogChannel, ConfirmedSections> _confirmed;

    /// <param name="path">The file; null keeps confirmations for this run only.</param>
    public CatalogChannelConfirmations(string? path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
        _confirmed = Load();
    }

    /// <summary>A snapshot; never changes under the caller.</summary>
    public IReadOnlyDictionary<CatalogChannel, ConfirmedSections> Current => Volatile.Read(ref _confirmed);

    /// <summary>A check on <paramref name="channel"/> found <paramref name="installed"/> current.</summary>
    public void Confirm(CatalogChannel channel, LocalCatalogState installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        if (installed.Workloads is null && installed.Workflows is null) return;

        var sections = new ConfirmedSections(installed.Workloads, installed.Workflows);
        lock (_write)
        {
            var current = _confirmed;
            if (current.TryGetValue(channel, out var known) && known == sections) return;

            // Another channel's confirmation of content that is gone describes nothing any more.
            var next = current
                .Where(kv => kv.Key != channel && (kv.Value.Workloads == sections.Workloads || kv.Value.Workflows == sections.Workflows))
                .ToImmutableDictionary()
                .SetItem(channel, sections);
            Volatile.Write(ref _confirmed, next);
            Save(next);
        }
        _logger.LogInformation("Catalog content v{Version} confirmed current on {Channel}", installed.HighestCatalogVersion, channel);
    }

    private ImmutableDictionary<CatalogChannel, ConfirmedSections> Load()
    {
        if (_path is null || !File.Exists(_path)) return ImmutableDictionary<CatalogChannel, ConfirmedSections>.Empty;
        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<CatalogChannel, ConfirmedSections>>(File.ReadAllText(_path), CatalogSchema.Json);
            return read?.ToImmutableDictionary() ?? ImmutableDictionary<CatalogChannel, ConfirmedSections>.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "{File} could not be read; no catalog content is confirmed current", _path);
            return ImmutableDictionary<CatalogChannel, ConfirmedSections>.Empty;
        }
    }

    /// <summary>Caller holds <see cref="_write"/>.</summary>
    private void Save(ImmutableDictionary<CatalogChannel, ConfirmedSections> confirmed)
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(confirmed, CatalogSchema.Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "{File} could not be written; the confirmation holds for this run only", _path);
        }
    }
}

/// <summary>The installed sections as they were when a check found them current on a channel.</summary>
public sealed record ConfirmedSections(SectionState? Workloads, SectionState? Workflows);
