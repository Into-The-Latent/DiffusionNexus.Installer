// DiffusionNexus.Installer.Tests/Content/ScannerPipelineAgreementTests.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.SDK.Models.Helpers;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation.Steps.Content;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Xunit;
using PipelineVram = DiffusionNexus.Installer.SDK.Services.Installation.Utilities.VramProfileHelper;

namespace DiffusionNexus.Installer.Tests.Content;

/// <summary>
/// The scanner decides which files the wizard checks and verifies; the pipeline decides which
/// files it downloads. For every real catalog workload and every tier it declares (plus 0), the
/// two must name the same links -- otherwise the dialog verifies files the install never writes.
/// Also checks filenames agree with FileDownloader's own rule, and that a link the scanner drops
/// is dropped only because its filename has no extension (the Content-Disposition case).
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
        var libraryOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["loras"] = "Lora", ["vae"] = "VAEs" };
        const string repo = @"C:\dn-agreement\repo";

        // The handler's ResolvePath is private; calling the real one keeps this an agreement test
        // rather than a third hand-copy of the rule.
        var resolvePath = typeof(ModelDownloadStepHandler).GetMethod("ResolvePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        foreach (var (library, overrides) in new (string?, Dictionary<string, string>)[] { (null, noOverrides), (@"D:\dn-library", libraryOverrides) })
        foreach (var workload in withModels)
        {
            var tiers = VramTiers.Parse(workload.Vram.VramProfiles).Prepend(0).Distinct();

            foreach (var tier in tiers)
            {
                var presence = scanner.Scan(new ModelScanRequest(workload, repo, library, overrides, tier))
                    .GroupBy(p => p.ModelId).ToDictionary(g => g.Key, g => g.First());

                foreach (var model in workload.ModelDownloads.Where(m => m.Enabled))
                {
                    var enabledLinks = model.DownloadLinks.Where(l => l.Enabled).ToList();
                    var modelDestination = ModelDestinationResolver.Resolve(workload, model, repo, library, overrides);

                    // (url, destination directory) exactly as ModelDownloadStepHandler computes them.
                    var expected = enabledLinks.Count == 0
                        ? (string.IsNullOrWhiteSpace(model.Url)
                            || (tier > 0 && !PipelineVram.VramProfileFitsSelection(model.VramProfile, tier))
                            ? [] : new[] { (Url: model.Url, Dir: modelDestination) })
                        : PipelineVram.SelectBestMatchingLinks(enabledLinks, tier, null, model.Name)
                            .Select(l => (Url: l.Url, Dir: string.IsNullOrWhiteSpace(l.Destination)
                                ? modelDestination
                                : (string)resolvePath.Invoke(null, [l.Destination, repo, workload])!))
                            .ToArray();

                    // Links whose downloader-derived name has no extension can never be scanned or
                    // verified before download (the real name only arrives with the server's
                    // Content-Disposition header) -- the scanner correctly drops those, so the
                    // comparison is only meaningful for the rest.
                    var expectedWithExtension = expected
                        .Where(e => FileDownloader.GetFileNameFromUrl(DownloadUrlNormalizer.Normalize(e.Url)).Contains('.'))
                        .ToArray();

                    var actualTargets = presence[model.Id].Targets;

                    actualTargets.Select(t => (Url: t.Url, Dir: t.DestinationDirectory)).Should().Equal(expectedWithExtension,
                        $"'{workload.Name}' / '{model.Name}' at {tier} GB (library: {library ?? "none"}) must scan exactly where the pipeline downloads");

                    presence[model.Id].UnresolvableLinks.Should().Be(expected.Length - expectedWithExtension.Length,
                        $"'{workload.Name}' / '{model.Name}' must count every dropped link");

                    foreach (var target in actualTargets)
                    {
                        target.FileName.Should().Be(
                            FileDownloader.GetFileNameFromUrl(DownloadUrlNormalizer.Normalize(target.Url)),
                            $"'{workload.Name}' / '{model.Name}' must name files exactly as FileDownloader will");
                    }

                    foreach (var dropped in expected.Select(e => e.Url).Except(expectedWithExtension.Select(e => e.Url)))
                    {
                        FileDownloader.GetFileNameFromUrl(DownloadUrlNormalizer.Normalize(dropped)).Should().NotContain(".",
                            $"'{workload.Name}' / '{model.Name}': '{dropped}' must be dropped only for the Content-Disposition case");
                    }
                }
            }
        }
    }
}
