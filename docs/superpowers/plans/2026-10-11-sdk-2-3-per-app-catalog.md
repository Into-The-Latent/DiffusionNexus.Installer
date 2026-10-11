# Installer on SDK 2.3.0: shared catalog folders, notices only for installer workloads

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Issue #53. The installer pins SDK 2.3.0, drops the three classes that moved into the SDK, announces only changes to the workloads it shows, and lets the SDK fill a channel's folder on a switch.

**Architecture:** `CatalogUpdateCoordinator` (Core) stays the one owner of the check/apply/switch lifecycle. It gets a `CatalogAudience` and filters every check and switch preview with `CatalogUpdateCheck.For(audience)`; the UI (`Home.razor`) keeps rendering `LastCheck`, so the filter reaches the Catalog row, the change list and the switch warning through one place. The 2.3.0 folder layout (`%LocalAppData%\ITL-Shared\format-N\catalog-<channel>\`) and the one-time copy of the old folder are the SDK's; the installer configures nothing for them (`AddDiffusionNexusCatalog` sets `SharedRoot`, and `Program.cs` sets no `InstalledCatalogPath`).

**Tech Stack:** .NET 10, xUnit + FluentAssertions + Moq, bUnit for the page. Build and test with `-p:UseLocalSDK=false` (the shell has `UseLocalSDK=true`; the plan pins packages, not the local source).

**Spec:** SDK repo `docs/superpowers/specs/2026-10-10-catalog-per-app-distribution-design.md`, sections 4, 5 and 7. Catalog README, "How the catalog is distributed".

## Global Constraints

- One issue (#53), one PR, branch `feature/sdk-2-3-per-app-catalog` → `main`. Commit and push after each task.
- Packages, not the local SDK: every build/test command carries `-p:UseLocalSDK=false`.
- SDK pin `2.3.0` in both csprojs (Core and Electron, four packages each). No `--prerelease`.
- The audience is `CatalogAudience.Installer(includeDrafts: WorkloadVisibility.Default.IncludeNonRelease)`, taken from the `WorkloadVisibility` registered in DI (Debug builds show drafts; shipped builds don't).
- The installer's channel stays in `user_settings.json` (`UserSettings.CatalogChannel`); its toggle stays `ApplyCatalogUpdatesAutomatically`. Nothing moves.
- Copy rule: the outcome line for a release with nothing for the installer is exactly `Catalog v{N} has no changes for the installer.`
- Baseline: 1113 tests green on 2.2.0. The three moved test classes leave the suite; everything else stays green.

## Review Focus

Inputs the spec implies but no existing test exercises; each gets its test in the owning task.

1. A check whose `Index` is empty (an older service, a fake): everything counts and the update is announced as before. → Task 2, `A_check_without_targets_counts_for_the_installer`.
2. A release that changes only the shared files (`repositories.json`, `wheels.json`; no workload or workflow rows at all) is announced to the installer. → Task 2, `A_release_that_changes_only_shared_files_is_announced`.
3. The apply receives the filtered check and the SDK still sees the whole release (`Remote`, `Channel`, `Unfiltered`). → Task 2, `The_apply_gets_the_check_with_its_whole_release`.
4. A "same content" switch while an install runs: no apply, the preference is saved, the switch is reported incomplete and Retry (a check) completes it. → Task 3, `A_same_content_switch_during_an_install_holds_the_apply_and_is_incomplete`.
5. After a switch with shared folders, `Installed` describes the folder now read, not the one left. → Task 3, `After_a_switch_installed_describes_the_folder_now_read`.

---

### Task 1: Pin 2.3.0 and delete the classes that moved into the SDK

**Files:**
- Modify: `DiffusionNexus.Installer.Core/DiffusionNexus.Installer.Core.csproj:10-13`
- Modify: `DiffusionNexus.Installer.Electron/DiffusionNexus.Installer.Electron.csproj:96-99`
- Delete: `DiffusionNexus.Installer.Core/Updates/CatalogChangeRows.cs`
- Delete: `DiffusionNexus.Installer.Core/Updates/CatalogChannelResolver.cs`
- Delete: `DiffusionNexus.Installer.Core/Updates/ChannelSwitchWarning.cs` → Create: `DiffusionNexus.Installer.Core/Updates/CatalogChannelSwitch.cs`
- Delete: `DiffusionNexus.Installer.Tests/Updates/CatalogChangeRowsTests.cs`, `CatalogChannelResolverTests.cs`, `ChannelSwitchWarningTests.cs`
- Modify: `DiffusionNexus.Installer.Tests/Support/FakeCatalogUpdateService.cs`

**Interfaces:**
- Consumes: SDK 2.3.0 `DiffusionNexus.Installer.SDK.Catalog.Updates.{CatalogChangeRows, CatalogChangeRow, ChannelSwitchWarning, CatalogChannelResolver, CatalogChannelSource}`; `ICatalogUpdateService.PreviewSwitchAsync(CatalogChannel from, CatalogChannel to, CancellationToken ct)`.
- Produces: `CatalogChannelSwitch` record in `DiffusionNexus.Installer.Core.Updates` (unchanged shape); `FakeCatalogUpdateService.PreviewedSwitches` (`List<(CatalogChannel From, CatalogChannel To)>`) for Task 2 and 3 tests.

- [ ] **Step 1: Bump the pin**

In both csprojs change the four `Version="2.2.0"` to `Version="2.3.0"` (Models, Shared, Services, Catalog).

- [ ] **Step 2: Build to see the ambiguity**

Run: `dotnet build DiffusionNexus.Installer.Tests -p:UseLocalSDK=false --nologo -v q 2>&1 | grep -c CS0104`
Expected: a count greater than 0 (`CatalogChangeRows`, `ChannelSwitchWarning`, `CatalogChannelResolver`, `CatalogChannelSource` exist in both namespaces).

- [ ] **Step 3: Delete the copies, keep the record**

Delete the three Core files and the three test files listed above. Create `DiffusionNexus.Installer.Core/Updates/CatalogChannelSwitch.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>A switch the user has been warned about and not answered yet.</summary>
/// <param name="Preview">The check of the target channel, filtered for the installer; Switch applies exactly this.</param>
public sealed record CatalogChannelSwitch(CatalogUpdateCheck Preview, ChannelSwitchWarning Warning)
{
    public CatalogChannel Target => Preview.Channel;
}
```

- [ ] **Step 4: Teach the fake the switch preview**

In `FakeCatalogUpdateService.cs` add after `PreviewedChannels`:

```csharp
    public List<(CatalogChannel From, CatalogChannel To)> PreviewedSwitches { get; } = [];
```

and after the `CheckAsync(CatalogChannel channel, ...)` method:

```csharp
    public async Task<CatalogUpdateCheck> PreviewSwitchAsync(CatalogChannel from, CatalogChannel to, CancellationToken ct = default)
    {
        PreviewedSwitches.Add((from, to));
        PreviewedChannels.Add(to);
        if (HoldCheck is not null) await HoldCheck.Task;
        return (NextPreview?.Invoke(to) ?? NextCheck() with { Channel = to }) with { IsSwitchPreview = true, FollowedChannel = from };
    }
```

- [ ] **Step 5: Build and run the suite**

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=false --nologo -v q 2>&1 | tail -3`
Expected: `Failed: 0`, Passed below 1113 by exactly the number of tests the three deleted classes held (count them with `grep -c "\[Fact\]\|\[InlineData" ` before deleting), Skipped 0.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore(catalog): pin SDK 2.3.0; CatalogChangeRows, ChannelSwitchWarning and CatalogChannelResolver come from the SDK now (#53)"
git push -u origin feature/sdk-2-3-per-app-catalog
```

---

### Task 2: Checks and switch previews filtered for the installer's workloads

**Files:**
- Modify: `DiffusionNexus.Installer.Core/Updates/CatalogUpdateCoordinator.cs` (constructor, `UpdateAvailable`, `CheckCoreAsync`, `CheckAtStartupAsync`, `StartApply`, `SwitchChannelAsync`, `PreviewAsync`)
- Modify: `DiffusionNexus.Installer.Core/Updates/ICatalogUpdateCoordinator.cs` (doc of `UpdateAvailable`)
- Modify: `DiffusionNexus.Installer.Core/CoreServiceCollectionExtensions.cs:51-59`
- Modify: `DiffusionNexus.Installer.Electron/Components/Pages/Home.razor` (`OutcomeText`)
- Modify: `DiffusionNexus.Installer.Tests/Support/CatalogChecks.cs`, `StubCatalogUpdateCoordinator.cs`
- Test: `DiffusionNexus.Installer.Tests/Updates/CatalogUpdateCoordinatorTests.cs`, `DiffusionNexus.Installer.Tests/Components/UpdatesPageTests.cs`

**Interfaces:**
- Consumes: `CatalogUpdateCheck.For(CatalogAudience)`, `.HasChanges`, `.Index`, `.Unfiltered`; `CatalogAudienceIndex(IReadOnlyDictionary<Guid, WorkloadAudienceInfo>, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>)`; `WorkloadAudienceInfo(WorkloadTargetType? Target, bool IsReleaseConfig)`; `WorkloadVisibility.IncludeNonRelease`; Task 1's fake.
- Produces: `CatalogUpdateCoordinator(..., CatalogAudience audience, ...)` constructor; `LastCheck` is always a filtered check (`Audience` set); test helper `CatalogChecks.Targeting(check, WorkloadTargetType, bool released = true)`.

- [ ] **Step 1: Test helper**

Append to `CatalogChecks`:

```csharp
    /// <summary>The check with every workload it mentions marked as <paramref name="target"/>'s, workflows owned by all of them. What a 2.3.0 manifest carries.</summary>
    public static CatalogUpdateCheck Targeting(this CatalogUpdateCheck check, WorkloadTargetType target, bool released = true)
    {
        IReadOnlyList<Guid> owners = check.Workloads.Select(w => w.Id).ToList();
        return check with
        {
            Index = new CatalogAudienceIndex(
                check.Workloads.ToDictionary(w => w.Id, _ => new WorkloadAudienceInfo(target, released)),
                check.Workflows.ToDictionary(w => w.Id, _ => owners)),
        };
    }
```

(`using DiffusionNexus.Installer.SDK.Models.Enums;` for `WorkloadTargetType`; make the class `static` — it is.)

- [ ] **Step 2: Failing coordinator tests**

Add a `Create` overload and the tests to `CatalogUpdateCoordinatorTests`. Replace the existing `Create()`:

```csharp
    private CatalogUpdateCoordinator Create(bool includeDrafts = false) =>
        new(_service, _catalog.Object, _options, _settings.Object, _session.Object, () => _environment,
            CatalogAudience.Installer(includeDrafts),
            confirmations: new CatalogChannelConfirmations(ConfirmationsPath));
```

Add a region `// ----- per-app notices (#53) -----`:

```csharp
    [Fact]
    public async Task A_release_that_changes_nothing_for_the_installer_is_not_announced()
    {
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable,
            [CatalogChecks.WorkloadAdded("Qwen-Image-2512", "V1.0")], [CatalogChecks.WorkflowAdded("Qwen edit", "V1.0", "Qwen-Image-2512")])
            .Targeting(WorkloadTargetType.DiffusionNexusCore);
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpdatesAvailable, "the folder is behind; the release is still a full snapshot");
        coordinator.LastCheck.HasChanges.Should().BeFalse();
        coordinator.LastCheck.Workloads.Should().BeEmpty();
        coordinator.LastCheck.Workflows.Should().BeEmpty();
        coordinator.UpdateAvailable.Should().BeFalse();
        coordinator.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task A_release_that_changes_nothing_for_the_installer_is_still_applied_automatically()
    {
        using var coordinator = WithUpdateOnStartup(automatic: true);
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-2512", "V1.0")])
            .Targeting(WorkloadTargetType.DiffusionNexusCore);
        _service.OnApply = () => WriteInstalledState(7);

        await coordinator.CheckAtStartupAsync();
        Settle(coordinator);

        _service.ApplyCalls.Should().Be(1, "full snapshots: the folder both apps read stays current");
        coordinator.LastApplyAutomatic.Should().BeTrue();
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
        coordinator.Installed!.HighestCatalogVersion.Should().Be(7);
    }

    [Fact]
    public async Task A_release_for_the_installer_is_announced_with_only_its_rows()
    {
        // Two workloads, one per app, built by hand so the index can differ per row.
        var krea = CatalogChecks.WorkloadUpdated("Krea-2-Turbo", "V1.0", "V1.1");
        var qwen = CatalogChecks.WorkloadAdded("Qwen-Image-2512", "V1.0");
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable, [krea, qwen]) with
        {
            Index = new CatalogAudienceIndex(new Dictionary<Guid, WorkloadAudienceInfo>
            {
                [krea.Id] = new(WorkloadTargetType.Installer, true),
                [qwen.Id] = new(WorkloadTargetType.DiffusionNexusCore, true),
            }, new Dictionary<Guid, IReadOnlyList<Guid>>()),
        };
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.UpdateAvailable.Should().BeTrue();
        coordinator.LastCheck!.Workloads.Should().ContainSingle().Which.Name.Should().Be("Krea-2-Turbo");
    }

    [Fact]
    public async Task A_check_without_targets_counts_for_the_installer()
    {
        _service.NextCheck = () => CatalogChecks.Available(4, CatalogChannel.Stable);   // Index empty: a manifest packed before 2.3.0
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.UpdateAvailable.Should().BeTrue();
        coordinator.LastCheck!.Workloads.Should().ContainSingle();
    }

    [Fact]
    public async Task A_release_that_changes_only_shared_files_is_announced()
    {
        _service.NextCheck = () => CatalogChecks.Available(4, CatalogChannel.Stable, workloads: [], workflows: []);
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.UpdateAvailable.Should().BeTrue("repositories.json and wheels.json belong to every app");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_draft_installer_workload_counts_only_in_a_build_that_shows_drafts(bool includeDrafts)
    {
        _service.NextCheck = () => CatalogChecks.Available(4, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Draft", "V1.0")])
            .Targeting(WorkloadTargetType.Installer, released: false);
        using var coordinator = Create(includeDrafts);

        await coordinator.CheckAsync();

        coordinator.UpdateAvailable.Should().Be(includeDrafts);
    }

    [Fact]
    public async Task The_apply_gets_the_check_with_its_whole_release()
    {
        _service.NextCheck = () => CatalogChecks.Available(4, CatalogChannel.Stable).Targeting(WorkloadTargetType.Installer);
        using var coordinator = Create();
        await coordinator.CheckAsync();

        await coordinator.ApplyAsync();
        Settle(coordinator);

        _service.AppliedCheck.Should().BeSameAs(coordinator.LastCheck);
        _service.AppliedCheck!.Unfiltered.Should().NotBeNull();
        _service.AppliedCheck.Remote!.CatalogVersion.Should().Be(4);
    }

    [Fact]
    public async Task A_switch_warns_only_about_installer_workloads()
    {
        _service.NextPreview = _ => StableRemoves("Qwen-Image-2.1").Targeting(WorkloadTargetType.DiffusionNexusCore);
        using var coordinator = CreateOnPreview();
        _service.OnApply = () => WriteInstalledState(4, CatalogChannel.Stable);

        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);
        Settle(coordinator);

        coordinator.PendingSwitch.Should().BeNull("the removal is the other app's");
        _service.PreviewedSwitches.Should().Equal((CatalogChannel.Preview, CatalogChannel.Stable));
        _service.ApplyCalls.Should().Be(1);
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
    }
```

- [ ] **Step 3: Run them**

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=false --nologo -v q --filter "FullyQualifiedName~CatalogUpdateCoordinatorTests" 2>&1 | tail -3`
Expected: compile error (no constructor takes `CatalogAudience`; `PreviewedSwitches` empty would fail too).

- [ ] **Step 4: The coordinator**

Constructor: add `CatalogAudience audience,` after `Func<string?> readEnvironment,`; field `private readonly CatalogAudience _audience;` assigned `_audience = audience;`.

`UpdateAvailable` and a new `ReleaseAvailable`:

```csharp
    // Not while a switch waits for an answer: the last check describes the channel being left,
    // and its update could not be applied until Switch or Keep (PR #43 review).
    // Announced only when the release changes something the installer shows (#53): a release that
    // changes the other app's workloads alone is applied silently or not at all, never offered.
    public bool UpdateAvailable => ReleaseAvailable && LastCheck!.HasChanges;

    /// <summary>The followed channel's folder is behind its release, whoever the changes are for.</summary>
    private bool ReleaseAvailable =>
        PendingSwitch is null && LastCheck?.Outcome == CatalogUpdateOutcome.UpdatesAvailable && Phase != CatalogUpdatePhase.Applied;
```

`CheckCoreAsync`: `var check = (await _updates.CheckAsync(ct).ConfigureAwait(false)).For(_audience);` and extend the log line with the unfiltered counts:

```csharp
            _logger.LogInformation("Catalog update check: {Outcome} (remote v{Remote}, installed v{Installed}, {Workloads} workload / {Workflows} workflow changes for the installer, {All} in the release){Error}",
                check.Outcome, check.Remote?.CatalogVersion, installed.HighestCatalogVersion, check.Workloads.Count, check.Workflows.Count,
                check.Unfiltered!.Workloads.Count + check.Unfiltered.Workflows.Count,
                check.Error is null ? string.Empty : ": " + check.Error);
```

`CheckAtStartupAsync`: `lock (_gate) wanted = AutoApply && ReleaseAvailable;`

`StartApply`: the refusal reads `if (_switching || !(automatic ? ReleaseAvailable && Phase == CatalogUpdatePhase.Checked && !InstallRunning : CanApply))` — write it as a local:

```csharp
            // A click applies what the page offered (CanApply). The startup check applies the release
            // whether or not it changes anything for the installer: full snapshots, and the folder is
            // shared with the other app (#53).
            var canStart = automatic
                ? ReleaseAvailable && Phase == CatalogUpdatePhase.Checked && !InstallRunning
                : CanApply;
            if (_switching || !canStart)
```

`SwitchChannelAsync`: `var preview = await PreviewAsync(current, target, ct).ConfigureAwait(false);`

`PreviewAsync`:

```csharp
    /// <summary>The switch from <paramref name="from"/> to <paramref name="target"/>, as the SDK diffs it, filtered for the installer. Leaves <see cref="CatalogOptions.Channel"/> alone. Never throws.</summary>
    private async Task<CatalogUpdateCheck> PreviewAsync(CatalogChannel from, CatalogChannel target, CancellationToken ct)
    {
        _logger.LogInformation("Catalog channel switch: previewing {Channel}", target);
        try
        {
            var check = (await _updates.PreviewSwitchAsync(from, target, ct).ConfigureAwait(false)).For(_audience);
            _logger.LogInformation("Catalog channel preview: {Outcome} (remote v{Remote}, {Workloads} workload / {Workflows} workflow changes for the installer){Error}",
                check.Outcome, check.Remote?.CatalogVersion, check.Workloads.Count, check.Workflows.Count,
                check.Error is null ? string.Empty : ": " + check.Error);
            return check;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Catalog channel preview failed unexpectedly");
            return new CatalogUpdateCheck(CatalogUpdateOutcome.Failed, target, null, LocalCatalogState.Load(_options.InstalledCatalogPath), [], [], ex.Message);
        }
    }
```

DI (`CoreServiceCollectionExtensions.cs`), inside the coordinator factory after the environment delegate:

```csharp
            CatalogAudience.Installer(includeDrafts: sp.GetRequiredService<WorkloadVisibility>().IncludeNonRelease),
```

(`using DiffusionNexus.Installer.Core.Catalog;` is already there for `WorkloadVisibility.Default`.)

`ICatalogUpdateCoordinator`: above `bool UpdateAvailable { get; }`:

```csharp
    /// <summary>
    /// The followed channel's release changes something the installer shows (#53). A release that
    /// changes only the other app's workloads is not announced: the startup check applies it when
    /// <see cref="AutoApply"/> is on, and otherwise the folder catches up with the next apply.
    /// </summary>
```

- [ ] **Step 5: Run the coordinator tests**

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=false --nologo -v q --filter "FullyQualifiedName~CatalogUpdateCoordinatorTests" 2>&1 | tail -3`
Expected: all pass, including the eight new ones.

- [ ] **Step 6: Failing page test**

`StubCatalogUpdateCoordinator.UpdateAvailable` mirrors the real rule:

```csharp
    public bool UpdateAvailable => PendingSwitch is null && LastCheck?.HasChanges == true && Phase != CatalogUpdatePhase.Applied;
```

Add to `UpdatesPageTests`:

```csharp
    [Fact]
    public void A_release_with_no_changes_for_the_installer_says_so_and_offers_nothing()
    {
        Register(InstallPhase.Idle);
        _catalog.LastCheck = CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-2512", "V1.0")])
            .Targeting(WorkloadTargetType.DiffusionNexusCore)
            .For(CatalogAudience.Installer(includeDrafts: false));
        _catalog.Phase = CatalogUpdatePhase.Checked;

        var page = Render<UpdatesPage>();

        page.Find(".catalog-outcome").TextContent.Trim().Should().Be("Catalog v7 has no changes for the installer.");
        page.FindAll(".catalog-changes").Should().BeEmpty();
        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == Apply);
    }
```

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=false --nologo -v q --filter "FullyQualifiedName~UpdatesPageTests.A_release_with_no_changes" 2>&1 | tail -3`
Expected: FAIL — the outcome reads "Catalog v7 is available on Stable."

- [ ] **Step 7: The outcome line**

In `Home.razor`'s `OutcomeText` switch, before the `UpdatesAvailable` arm:

```csharp
                { Outcome: CatalogUpdateOutcome.UpdatesAvailable, HasChanges: false } c => $"Catalog v{c.Remote?.CatalogVersion} has no changes for the installer.",
```

Run the page test again. Expected: PASS.

- [ ] **Step 8: Whole suite, commit**

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=false --nologo -v q 2>&1 | tail -3`
Expected: `Failed: 0`.

```bash
git add -A
git commit -m "feat(catalog): announce only changes to installer workloads; a release with nothing for the installer is applied silently or not at all (#53)"
git push
```

---

### Task 3: A switch hands the preview to the SDK, which fills the target folder

**Files:**
- Modify: `DiffusionNexus.Installer.Core/Updates/CatalogUpdateCoordinator.cs` (`ApplyCoreAsync`, `CommitSwitchAsync`)
- Modify: `DiffusionNexus.Installer.Core/Updates/ICatalogUpdateCoordinator.cs` (docs of `SwitchChannelAsync`, `ConfirmSwitchAsync`)
- Test: `DiffusionNexus.Installer.Tests/Updates/CatalogUpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: SDK `ApplyAsync` of a check with `IsSwitchPreview`: re-checks the target channel's own folder; `UpToDate` there → `CatalogApplyResult(None, None, null)` and the channel is followed; `UpdatesAvailable` → the whole release lands in that folder. `CatalogOptions.SharedRoot`, `WriteFolderFor(channel, 1)`, `ReadFolderFor(channel)`.
- Produces: nothing new for later tasks.

Rule: a switch applies the preview when it says **UpdatesAvailable or UpToDate** (the SDK decides against the target folder, which may not exist yet); `Failed`, `RequiresNewerSoftware` and `OverrideActive` keep today's behavior (preference saved, nothing applied). A successful apply that landed nothing ends in **Checked** (nothing was updated), and every successful apply confirms the content current on the applied channel. After the switch `Installed` is read from the folder now followed.

- [ ] **Step 1: Change the two existing tests, add three**

Replace `A_switch_to_the_same_content_only_saves_the_preference`:

```csharp
    // #53: the target channel's folder may not exist yet (SDK 2.3.0 keeps one per channel). The SDK's
    // apply of a switch preview checks that folder itself and fills it when it is missing or behind.
    [Fact]
    public async Task A_switch_to_the_same_content_still_hands_the_preview_to_the_sdk()
    {
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate) with { Channel = channel };
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.None, null);
        using var coordinator = CreateOnPreview();

        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);
        Settle(coordinator);

        coordinator.PendingSwitch.Should().BeNull();
        VerifySaved("Stable");
        _service.ApplyCalls.Should().Be(1);
        _service.AppliedCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpToDate);
        _service.AppliedCheck.IsSwitchPreview.Should().BeTrue();
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked, "nothing landed, so nothing was 'updated'");
        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpToDate);
        coordinator.LastCheck.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.ContentGeneration.Should().Be(0);
        coordinator.SwitchIncomplete.Should().BeFalse();
    }
```

In `A_switch_with_nothing_to_download_stays_complete_on_an_offline_restart` add, before `using (var coordinator = CreateOnPreview())`:

```csharp
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.None, null);
```

and `Settle(coordinator);` after the `SwitchChannelAsync` call inside the block.

Add:

```csharp
    [Fact]
    public async Task A_same_content_switch_during_an_install_holds_the_apply_and_is_incomplete()
    {
        var plan = await new WizardModuleRegistry(() => [])
            .BuildPlanAsync(new WizardSelection { Workload = new InstallationConfiguration { Name = "Krea-2-Turbo" } });
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        _session.SetupGet(s => s.Plan).Returns(plan);
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate) with { Channel = channel };
        using var coordinator = CreateOnPreview();

        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        VerifySaved("Stable");
        _service.ApplyCalls.Should().Be(0, "never swap content under a running plan");
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.SwitchIncomplete.Should().BeTrue("the Stable folder was not checked; the content is still what Preview left");
        coordinator.UpdateAvailable.Should().BeFalse();

        // Retry is a check of the followed channel; the state written by the apply ends the incomplete switch.
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        _service.NextCheck = () => CatalogChecks.Available(5, CatalogChannel.Stable);
        _service.OnApply = () => WriteInstalledState(5, CatalogChannel.Stable);
        await coordinator.CheckAsync();
        await coordinator.ApplyAsync();
        Settle(coordinator);
        coordinator.SwitchIncomplete.Should().BeFalse();
    }

    [Fact]
    public async Task After_a_switch_installed_describes_the_folder_now_read()
    {
        // Shared folders (no InstalledCatalogPath): Stable at v4, Preview at v6, the installer on Stable.
        var options = new CatalogOptions { SharedRoot = Path.Combine(_dir, "shared"), Channel = CatalogChannel.Stable };
        State(4, CatalogChannel.Stable).Save(options.WriteFolderFor(CatalogChannel.Stable, 1));
        State(6, CatalogChannel.Preview).Save(options.WriteFolderFor(CatalogChannel.Preview, 1));
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate) with { Channel = channel };
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.None, null);
        using var coordinator = new CatalogUpdateCoordinator(_service, _catalog.Object, options, _settings.Object, _session.Object, () => _environment,
            CatalogAudience.Installer(false), confirmations: new CatalogChannelConfirmations(ConfirmationsPath));
        await coordinator.CheckAsync();
        coordinator.Installed!.HighestCatalogVersion.Should().Be(4);

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        Settle(coordinator);

        options.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.Installed!.HighestCatalogVersion.Should().Be(6, "the Catalog row describes the folder the app reads now");
        coordinator.SwitchIncomplete.Should().BeFalse();
    }

    [Fact]
    public async Task A_switch_whose_apply_landed_nothing_confirms_the_content_current_on_the_target()
    {
        // The Preview folder holds the embedded seed, stamped Stable. The SDK's re-check found it current on Preview.
        _saved.CatalogChannel = "Stable";
        WriteInstalledState(5, CatalogChannel.Stable);
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate) with { Channel = channel };
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.None, null);
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        Settle(coordinator);

        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.SwitchIncomplete.Should().BeFalse("found current on Preview, whatever the state records");
    }
```

Add the helper next to `WriteInstalledState`:

```csharp
    private static LocalCatalogState State(int version, CatalogChannel channel) => new()
    {
        Workloads = new SectionState(version, "abc", DateTimeOffset.UtcNow) { Channel = channel },
        Workflows = new SectionState(version, "abc", DateTimeOffset.UtcNow) { Channel = channel },
    };
```

(`LocalCatalogState.Save(folder)` creates the folder; check the SDK signature — `Save(string folder)` writes `catalog-state.json` in it. If `WriteFolderFor` does not create the directory, `Directory.CreateDirectory` first.)

- [ ] **Step 2: Run them**

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=false --nologo -v q --filter "FullyQualifiedName~CatalogUpdateCoordinatorTests" 2>&1 | grep -E "Failed |Passed!|Failed!"`
Expected: FAIL — `A_switch_to_the_same_content_still_hands_the_preview_to_the_sdk` (ApplyCalls 0), `After_a_switch_installed_describes_the_folder_now_read` (Installed is 4), `A_switch_whose_apply_landed_nothing_confirms...` (no apply ran, state says Stable → incomplete).

- [ ] **Step 3: The coordinator**

`ApplyCoreAsync`, success path — replace the `lock (_gate)` block's phase line and add the confirmation:

```csharp
            var landedSomething = result.Applied != CatalogSections.None;
            lock (_gate)
            {
                LastApply = result;
                Installed = installed;
                _overrideActive = overrideActive;
                Progress = null;
                if (landedSomething) Interlocked.Increment(ref _contentGeneration);
                // A switch whose target folder was already current applies nothing: nothing was "updated" (#53).
                Phase = succeeded && landedSomething ? CatalogUpdatePhase.Applied : CatalogUpdatePhase.Checked;
                if (succeeded) _notLanded = null;
            }
            // What the folder holds is current on this channel, whatever its sections record (an
            // embedded seed in a channel folder records the embedded manifest's channel).
            if (succeeded) _confirmations.Confirm(check.Channel, installed);
```

`CommitSwitchAsync` — after `SavePreferenceAsync`, replace from `// Saved before the download starts` down to the log line with:

```csharp
        // Saved before the download starts: a stalled or failed apply must not lose the choice
        // (rule 1), and the next check then offers the same diff with Apply.
        var overrideActive = InstalledCatalogDescription.OverrideActive(_options);
        var applying = false;
        LocalCatalogState installed;
        lock (_gate)
        {
            var left = Channel;
            ApplyResolution(_readEnvironment(), saved);
            _channelResolved = true;
            // After the resolution: with shared folders the path now names the target channel's folder (#53).
            installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
            Installed = installed;
            _overrideActive = overrideActive;
            LastApply = null;
            _applyAutomatic = false;
            Progress = null;
            if (Channel != target)
            {
                // The variable is read live, so it can appear between the preview and here. It
                // decides, exactly as in SwitchChannelAsync.
                LastCheck = null;
                Phase = CatalogUpdatePhase.Idle;
            }
            else
            {
                LastCheck = preview;
                _notLanded = preview.Outcome == CatalogUpdateOutcome.OverrideActive ? null
                    // The content is still from where the first switch that did not land left it;
                    // switching back there is complete, whatever this preview answered.
                    : _notLanded is { From: var from } ? (from == target ? null : (from, target))
                    : (left, target);
                // UpToDate too (#53): the preview compared the target's release with the folder being
                // left. The SDK's apply checks the target channel's own folder, fills it when it is
                // missing or behind, and applies nothing when it is current -- then nothing landed and
                // the switch is complete (ApplyCoreAsync).
                if (preview.Outcome is CatalogUpdateOutcome.UpdatesAvailable or CatalogUpdateOutcome.UpToDate && !InstallRunning)
                {
                    // Not under the caller's token: the switch has returned by the time the download
                    // runs, and a caller that cancels afterwards (a page going away) does not own it.
                    Phase = CatalogUpdatePhase.Applying;
                    _inFlight = Task.Run(() => ApplyCoreAsync(preview, CancellationToken.None), CancellationToken.None);
                    applying = true;
                }
                else
                {
                    // Failed, needs newer software, override: the installed content stays and the
                    // page says so. During an install the apply (or the retry) waits for it.
                    Phase = CatalogUpdatePhase.Checked;
                }
            }
        }
        _logger.LogInformation("Catalog channel switched to {Channel}: {Next}", target,
            applying ? (preview.Outcome == CatalogUpdateOutcome.UpToDate ? "checking its folder" : "applying v" + preview.Remote?.CatalogVersion)
            : preview.Outcome is CatalogUpdateOutcome.UpdatesAvailable or CatalogUpdateOutcome.UpToDate ? "the apply waits for the running install"
            : preview.Outcome.ToString());
```

(The `current` local and its `_confirmations.Confirm(target, installed)` line go away: the apply confirms.)

`ICatalogUpdateCoordinator` docs: in `SwitchChannelAsync`'s summary replace "otherwise the switch goes ahead at once" with "otherwise the switch goes ahead at once, and the SDK fills the target channel's folder (SDK 2.3.0 keeps one per channel) when it is missing or behind"; in `ConfirmSwitchAsync`'s summary, after "then starts the apply of the previewed check", add "(the SDK re-checks the target channel's own folder and applies nothing when it is current)".

- [ ] **Step 4: Run the coordinator tests**

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=false --nologo -v q --filter "FullyQualifiedName~CatalogUpdateCoordinatorTests" 2>&1 | tail -3`
Expected: all pass. If `Switching_saves_the_preference_then_applies_the_previewed_check` or `A_failed_apply_still_saves_the_preference_and_can_be_retried` fail on `Installed`, the state those tests write is in `_options.InstalledCatalogPath` (single folder) and is read after the resolution now — the version they assert is what `OnApply` wrote; adjust only if the assertion was about the pre-apply state.

- [ ] **Step 5: Whole suite, commit**

Run: `dotnet test DiffusionNexus.Installer.Tests -p:UseLocalSDK=false --nologo -v q 2>&1 | tail -3`
Expected: `Failed: 0`.

```bash
git add -A
git commit -m "feat(catalog): a channel switch hands the preview to the SDK, which fills the target channel's folder; a switch that landed nothing ends in Checked (#53)"
git push
```

---

## After the tasks

1. Whole-branch review (fresh reviewer, most capable model), fix Critical/Important test-first, ledger the rest.
2. Open the PR to `main` (one PR for #53), then the owner's usual `/code-review` rounds: 3, plus 2 more while bugs remain.
3. **Smoke test on the BenQ** (manual, before merge), as issue #53 describes: start with `%LocalAppData%\DiffusionNexus\catalog` present and no `ITL-Shared` → the copy appears under `ITL-Shared\format-1\catalog-stable\`; switch to Preview (v6, app workloads only) → no warning, `catalog-preview\` appears, Catalog row "v6 (Preview)"; switch back → `catalog-stable\` read again, no download. Also "Check for updates" on Stable with Preview's v6 content shows "has no changes for the installer" only if Stable is at v6 with nothing for the installer — otherwise skip that step and note it.
