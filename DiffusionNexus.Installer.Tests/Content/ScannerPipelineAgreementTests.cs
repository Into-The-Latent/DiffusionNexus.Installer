// DiffusionNexus.Installer.Tests/Content/ScannerPipelineAgreementTests.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Xunit;
using PipelineVram = DiffusionNexus.Installer.SDK.Services.Installation.Utilities.VramProfileHelper;

namespace DiffusionNexus.Installer.Tests.Content;

/// <summary>
/// The scanner decides which files the wizard checks and verifies; the pipeline decides which
/// files it downloads. For every real catalog workload and every tier it declares (plus 0), the
/// two must name the same links -- otherwise the dialog verifies files the install never writes.
/// </summary>
public sealed class ScannerPipelineAgreementTests : IAsyncLifetime
{
    private string _dir = string.Empty;
    private IReadOnlyList<DiffusionNexus.Installer.SDK.Models.Configuration.InstallationConfiguration> _workloads = [];

    public async Task InitializeAsync() => (_dir, _workloads) = await EmbeddedCatalog.LoadAsync();

    public Task DisposeAsync()
    {
        EmbeddedCatalog.Delete(_dir);
        return Task.CompletedTask;
    }

    [Fact]
    public void Scanner_targets_equal_the_pipelines_link_selection_for_every_workload_and_tier()
    {
        var withModels = _workloads
            .Where(w => w.WorkloadTarget == WorkloadTargetType.Installer && w.ModelDownloads.Count > 0)
            .ToList();
        withModels.Should().NotBeEmpty("the real catalog has tiered packs; an empty set means a broken read");

        var scanner = new ModelPresenceScanner();
        var noOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workload in withModels)
        {
            var tiers = VramTiers.Parse(workload.Vram.VramProfiles).Prepend(0).Distinct();

            foreach (var tier in tiers)
            {
                var presence = scanner.Scan(new ModelScanRequest(workload, @"C:\dn-agreement\repo", null, noOverrides, tier))
                    .ToDictionary(p => p.ModelId);

                foreach (var model in workload.ModelDownloads.Where(m => m.Enabled))
                {
                    var enabledLinks = model.DownloadLinks.Where(l => l.Enabled).ToList();
                    var expected = enabledLinks.Count == 0
                        ? (string.IsNullOrWhiteSpace(model.Url)
                            || (tier > 0 && !PipelineVram.VramProfileFitsSelection(model.VramProfile, tier))
                            ? [] : new[] { model.Url })
                        : PipelineVram.SelectBestMatchingLinks(enabledLinks, tier, null, model.Name).Select(l => l.Url).ToArray();

                    presence[model.Id].Targets.Select(t => t.Url).Should().Equal(expected,
                        $"'{workload.Name}' / '{model.Name}' at {tier} GB must scan exactly what the pipeline downloads");
                }
            }
        }
    }
}
