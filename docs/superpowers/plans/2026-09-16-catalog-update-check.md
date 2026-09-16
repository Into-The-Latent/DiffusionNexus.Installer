# Catalog Update Check + Preview Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Electron installer check the catalog channel it follows, show what changed, and apply the update, so a tagged catalog Release reaches Stable users and a Preview push reaches machines that opted into Preview.

**Architecture:** One SDK property (`UserSettings.CatalogChannel`) stores the preference; an env var overrides it per process. In the installer, a Core-side `CatalogUpdateCoordinator` singleton owns the check/apply state machine around the SDK's `ICatalogUpdateService`, resolves the channel into the SDK's `CatalogOptions`, and raises a plain `Changed` event. A hosted service fires one check at startup. The top bar, the Welcome page, the `/updates` page and the Debug-only Developer tools page render the coordinator's state.

**Tech Stack:** .NET 10, Blazor Server inside ElectronNET.Core 0.5.2, SDK 2.0.0-preview.8 (`DiffusionNexus.Installer.SDK.Catalog` / `.Services` / `.Models`), xunit 2.9 + FluentAssertions 7 + Moq 4.20 + bUnit 2.8.

**Spec:** `docs/superpowers/specs/2026-09-16-catalog-update-check-design.md` (installer repo, branch `feature/catalog-update-check`).

## Global Constraints

- Two repos. SDK work happens in `E:\Repos\DiffusionNexus.Installer.SDK` on branch `feature/catalog-channel-setting` (off `develop`). Installer work happens in `E:\Repos\DiffusionNexus.Installer` on the existing branch `feature/catalog-update-check` (off `main`). Never commit to `develop` or `main`.
- The installer's `Directory.Build.targets` auto-switches to ProjectReferences on the local SDK checkout whenever `E:\Repos\DiffusionNexus.Installer.SDK` exists. **Keep that SDK checkout on `feature/catalog-channel-setting`** (or on `develop` once Task 1's PR is merged) while building the installer, or `UserSettings.CatalogChannel` will not compile.
- Installer CI stays red until tag `v2.0.0-preview.8` exists on the SDK (Task 12). Expected; the PR body says so.
- Env var name, exactly: `DIFFUSIONNEXUS_CATALOG_CHANNEL`. Accepted values, case-insensitive: `stable`, `preview`. Anything else is ignored.
- UI words for the catalog: **Stable** and **Preview**. For the app: "latest release". Never `latest` / `beta`.
- `LocalCatalogState.Channel` (the SDK's `catalog-state.json`) is provenance. The installer never writes it as a preference.
- Apply always sends `CatalogSections.All`. No per-kind toggles.
- Core has no Electron dependency. Anything Electron-specific lives in `DiffusionNexus.Installer.Electron`.
- Loggers are optional constructor parameters defaulting to `NullLogger<T>.Instance` (pattern: `Core/Gallery/CommunityLinksCache.cs`). `DependencyInjectionTests.Build()` registers no logging.
- Every commit message ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Every PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- `gh` accounts: the SDK repo (`Little-God1983/DiffusionNexus.Installer.SDK`) needs `gh auth switch --user Little-God1983`; the installer repo (`Into-The-Latent/DiffusionNexus.Installer`) needs `gh auth switch --user Into-The-Latent`. Git pushes 403 on the wrong account.
- Before each push run `git diff --numstat origin/<base>...HEAD` and `git diff -w --numstat origin/<base>...HEAD`; a file whose counts collapse under `-w` has flipped line endings and must be restored before pushing.
- Test commands (run from each repo root):
  - SDK: `dotnet test DiffusionNexus.Installer.SDK.Tests/DiffusionNexus.Installer.SDK.Tests.csproj --filter "FullyQualifiedName~<Class>"`
  - Installer: `dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~<Class>"`
  - Full installer suite before every commit: `dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj` (536 tests green at the start of this plan).

## File Structure

**SDK repo**
- Modify `DiffusionNexus.Installer.SDK.Models/Installation/UserSettings.cs` — add `CatalogChannel`.
- Modify `DiffusionNexus.Installer.SDK.Tests/CoreServices/Settings/JsonUserSettingsRepositoryTests.cs` — round-trip tests.

**Installer repo, Core (`DiffusionNexus.Installer.Core/Updates/`)** — one folder, one responsibility each:
- `CatalogChannelResolver.cs` — pure precedence rule env → setting → Stable; `CatalogChannelSource` enum.
- `CatalogChangeRows.cs` — flattens a `CatalogUpdateCheck` into display rows; `CatalogChangeRow` record.
- `ICatalogUpdateCoordinator.cs` — the contract the UI binds to; `CatalogUpdatePhase` enum.
- `CatalogUpdateCoordinator.cs` — the state machine around `ICatalogUpdateService`.
- Modify `CoreServiceCollectionExtensions.cs` — register the coordinator.

**Installer repo, Electron**
- `Services/CatalogUpdateStartupCheck.cs` — `IHostedService` firing the startup check.
- Modify `Services/HostServiceCollectionExtensions.cs` — `AddHostedService`.
- Modify `Components/Shared/TopBar.razor` — attention dot.
- Modify `Components/Pages/Welcome.razor` — notice.
- Modify `Components/Pages/Home.razor` — "Content catalog" section, combined check button.
- Modify `Components/Pages/DebugTools.razor` — channel panel.
- Modify `wwwroot/app.css` — dot, notice, change list, error styles.

**Installer repo, Tests**
- `Support/CatalogChecks.cs` — factories for `CatalogUpdateCheck` / `CatalogManifest`.
- `Support/FakeCatalogUpdateService.cs` — holdable fake of the SDK service.
- `Support/StubCatalogUpdateCoordinator.cs` + `Support/UpdateSignals.cs` — settable stub for bUnit fixtures.
- `Updates/CatalogChannelResolverTests.cs`, `Updates/CatalogChangeRowsTests.cs`, `Updates/CatalogUpdateCoordinatorTests.cs`.
- `Services/CatalogUpdateStartupCheckTests.cs`.
- `Components/DebugToolsPageTests.cs` (whole file `#if DEBUG`).
- Modify `Components/TopBarTests.cs`, `ScreenShellTests.cs`, `WelcomePageTests.cs`, `WelcomeScriptFailureTests.cs`, `SoftwareWorkloadsPageTests.cs`, `InstallPageTests.cs`, `UpdatesPageTests.cs`, `DependencyInjectionTests.cs`.
- Modify `docs/manual-smoke.md` — new §7.

---

### Task 1: SDK — `UserSettings.CatalogChannel`

**Files:**
- Modify: `DiffusionNexus.Installer.SDK.Models/Installation/UserSettings.cs` (after `SkippedVersion`, line ~53)
- Test: `DiffusionNexus.Installer.SDK.Tests/CoreServices/Settings/JsonUserSettingsRepositoryTests.cs`

**Interfaces:**
- Produces: `public string? CatalogChannel { get; set; }` on `DiffusionNexus.Installer.SDK.Models.Installation.UserSettings`. Values `"Stable"` / `"Preview"` / null. Consumed by Task 4.

- [ ] **Step 1: Branch off develop in the SDK repo**

```powershell
cd E:\Repos\DiffusionNexus.Installer.SDK
git status --porcelain          # must be empty
git checkout develop
git pull --ff-only
git checkout -b feature/catalog-channel-setting
```

- [ ] **Step 2: Write the failing tests**

Append inside the class in `JsonUserSettingsRepositoryTests.cs`, before the final closing brace of the class (after the last existing `#endregion`):

```csharp
    #region CatalogChannel

    [Fact]
    public async Task WhenSaveWithCatalogChannelThenRoundTrips()
    {
        // Arrange
        var repo = CreateRepository(GetUniqueFilePath());
        var settings = new UserSettings { UserId = Guid.NewGuid(), UserName = "TestUser", CatalogChannel = "Preview" };

        // Act
        await repo.SaveAsync(settings);
        var result = await repo.GetByUserNameAsync("TestUser");

        // Assert
        result.Should().NotBeNull();
        result!.CatalogChannel.Should().Be("Preview");
    }

    [Fact]
    public async Task WhenCatalogChannelIsUnsetThenItIsNullAndAbsentFromTheFile()
    {
        // A host that never touched the channel must read back "no preference", and a settings file
        // written by an older SDK must not grow a key on every save (WhenWritingDefault drops nulls).
        var filePath = GetUniqueFilePath();
        var repo = CreateRepository(filePath);

        await repo.SaveAsync(new UserSettings { UserId = Guid.NewGuid(), UserName = "TestUser" });
        var result = await repo.GetByUserNameAsync("TestUser");

        result!.CatalogChannel.Should().BeNull();
        File.ReadAllText(filePath).Should().NotContain("catalogChannel");
    }

    #endregion
```

- [ ] **Step 3: Run the tests to see them fail**

```powershell
dotnet test DiffusionNexus.Installer.SDK.Tests/DiffusionNexus.Installer.SDK.Tests.csproj --filter "FullyQualifiedName~JsonUserSettingsRepositoryTests"
```
Expected: build error `'UserSettings' does not contain a definition for 'CatalogChannel'`.

- [ ] **Step 4: Add the property**

In `UserSettings.cs`, after the `SkippedVersion` property and before the class's closing brace:

```csharp
        /// <summary>
        /// Catalog update channel the host follows: "Stable" or "Preview". Null means Stable.
        /// A string rather than the Catalog package's <c>CatalogChannel</c> enum because Models
        /// cannot reference Catalog. Hosts parse it case-insensitively and ignore anything else.
        /// </summary>
        public string? CatalogChannel { get; set; }
```

- [ ] **Step 5: Run the tests to see them pass**

Same command as Step 3. Expected: all `JsonUserSettingsRepositoryTests` pass, including the two new ones.

- [ ] **Step 6: Run the whole SDK suite**

```powershell
dotnet test DiffusionNexus.Installer.SDK.sln
```
Expected: green (2010+ tests).

- [ ] **Step 7: Commit, push, open the PR**

```powershell
git add DiffusionNexus.Installer.SDK.Models/Installation/UserSettings.cs DiffusionNexus.Installer.SDK.Tests/CoreServices/Settings/JsonUserSettingsRepositoryTests.cs
git commit -m "feat(models): UserSettings.CatalogChannel -- the host's Stable/Preview catalog preference" -m "Consumed by the 3.x installer's catalog update check. String, not the Catalog enum: Models cannot reference Catalog. Null = Stable; WhenWritingDefault keeps it out of files that never set it." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
gh auth switch --user Little-God1983
git push -u origin feature/catalog-channel-setting
gh pr create --repo Little-God1983/DiffusionNexus.Installer.SDK --base develop --title "feat(models): UserSettings.CatalogChannel for the installer's catalog channel" --body "One nullable string on UserSettings so a host can persist which catalog channel (Stable/Preview) it follows. Needed by Into-The-Latent/DiffusionNexus.Installer's catalog update check (spec in that repo, docs/superpowers/specs/2026-09-16-catalog-update-check-design.md). Ships as 2.0.0-preview.8.`n`n🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

Leave the SDK checkout on this branch so the installer's local-SDK build sees the property.

---

### Task 2: `CatalogChannelResolver`

**Files:**
- Create: `DiffusionNexus.Installer.Core/Updates/CatalogChannelResolver.cs`
- Test: `DiffusionNexus.Installer.Tests/Updates/CatalogChannelResolverTests.cs`

**Interfaces:**
- Produces:
  - `public enum CatalogChannelSource { Default, Setting, Environment }`
  - `public static class CatalogChannelResolver { public const string EnvironmentVariable = "DIFFUSIONNEXUS_CATALOG_CHANNEL"; public static (CatalogChannel Channel, CatalogChannelSource Source) Resolve(string? environmentValue, string? savedValue); public static bool TryParse(string? value, out CatalogChannel channel); }`
  - Namespace `DiffusionNexus.Installer.Core.Updates`. `CatalogChannel` is `DiffusionNexus.Installer.SDK.Catalog.Packaging.CatalogChannel`.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Installer.Tests/Updates/CatalogChannelResolverTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Updates;

/// <summary>
/// The one rule that decides which release the installer reads: the environment wins (a tester
/// flips it for one run without touching saved state), then the saved setting, then Stable.
/// </summary>
public class CatalogChannelResolverTests
{
    [Fact]
    public void Nothing_set_means_stable_by_default()
    {
        CatalogChannelResolver.Resolve(null, null)
            .Should().Be((CatalogChannel.Stable, CatalogChannelSource.Default));
    }

    [Theory]
    [InlineData("Preview", CatalogChannel.Preview)]
    [InlineData("preview", CatalogChannel.Preview)]
    [InlineData("  STABLE ", CatalogChannel.Stable)]
    public void The_saved_setting_is_read_case_insensitively(string saved, CatalogChannel expected)
    {
        CatalogChannelResolver.Resolve(null, saved)
            .Should().Be((expected, CatalogChannelSource.Setting));
    }

    [Fact]
    public void The_environment_beats_the_saved_setting()
    {
        CatalogChannelResolver.Resolve("stable", "Preview")
            .Should().Be((CatalogChannel.Stable, CatalogChannelSource.Environment));
    }

    [Fact]
    public void An_unrecognised_environment_value_falls_through_to_the_setting()
    {
        CatalogChannelResolver.Resolve("nightly", "Preview")
            .Should().Be((CatalogChannel.Preview, CatalogChannelSource.Setting));
    }

    [Fact]
    public void An_unrecognised_setting_falls_through_to_stable()
    {
        CatalogChannelResolver.Resolve(null, "1")
            .Should().Be((CatalogChannel.Stable, CatalogChannelSource.Default),
                "Enum.TryParse would accept the digit; a channel is a word");
    }

    [Fact]
    public void The_variable_name_is_the_documented_one()
    {
        CatalogChannelResolver.EnvironmentVariable.Should().Be("DIFFUSIONNEXUS_CATALOG_CHANNEL");
    }
}
```

- [ ] **Step 2: Run to see them fail**

```powershell
cd E:\Repos\DiffusionNexus.Installer
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~CatalogChannelResolverTests"
```
Expected: build error, `CatalogChannelResolver` not found.

- [ ] **Step 3: Implement**

Create `DiffusionNexus.Installer.Core/Updates/CatalogChannelResolver.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog.Packaging;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>Where the channel the installer follows came from. Shown on /updates and Developer tools.</summary>
public enum CatalogChannelSource { Default, Setting, Environment }

/// <summary>
/// Environment wins over the saved setting, which wins over Stable. The environment value is never
/// written back: a tester sets it for one run and is back on the saved preference when it is gone.
/// </summary>
public static class CatalogChannelResolver
{
    public const string EnvironmentVariable = "DIFFUSIONNEXUS_CATALOG_CHANNEL";

    public static (CatalogChannel Channel, CatalogChannelSource Source) Resolve(string? environmentValue, string? savedValue)
    {
        if (TryParse(environmentValue, out var fromEnvironment)) return (fromEnvironment, CatalogChannelSource.Environment);
        if (TryParse(savedValue, out var fromSetting)) return (fromSetting, CatalogChannelSource.Setting);
        return (CatalogChannel.Stable, CatalogChannelSource.Default);
    }

    /// <summary>
    /// Word match only. Enum.TryParse(ignoreCase) also accepts "0" and "1", and a stray digit in a
    /// settings file must not silently pick a channel.
    /// </summary>
    public static bool TryParse(string? value, out CatalogChannel channel)
    {
        var word = value?.Trim();
        if (string.Equals(word, nameof(CatalogChannel.Stable), StringComparison.OrdinalIgnoreCase)) { channel = CatalogChannel.Stable; return true; }
        if (string.Equals(word, nameof(CatalogChannel.Preview), StringComparison.OrdinalIgnoreCase)) { channel = CatalogChannel.Preview; return true; }
        channel = CatalogChannel.Stable;
        return false;
    }
}
```

- [ ] **Step 4: Run to see them pass**

Same command. Expected: 8 tests pass.

- [ ] **Step 5: Commit**

```powershell
git add DiffusionNexus.Installer.Core/Updates/CatalogChannelResolver.cs DiffusionNexus.Installer.Tests/Updates/CatalogChannelResolverTests.cs
git commit -m "feat(core): CatalogChannelResolver -- env var, then saved setting, then Stable" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 3: `CatalogChangeRows` + test factories

**Files:**
- Create: `DiffusionNexus.Installer.Core/Updates/CatalogChangeRows.cs`
- Create: `DiffusionNexus.Installer.Tests/Support/CatalogChecks.cs`
- Test: `DiffusionNexus.Installer.Tests/Updates/CatalogChangeRowsTests.cs`

**Interfaces:**
- Consumes (SDK, namespace `DiffusionNexus.Installer.SDK.Catalog.Updates`): `CatalogUpdateCheck(Outcome, Channel, Remote, Local, Workloads, Workflows, Error)`, `WorkloadChange(Guid Id, string Name, ChangeKind Kind, string? FromVersion, string? ToVersion)`, `WorkflowChange(Guid Id, string Name, IReadOnlyList<string> WorkloadNames, ChangeKind Kind, string? FromVersion, string? ToVersion)`, `enum ChangeKind { Added, Updated, Removed }`.
- Produces:
  - `public sealed record CatalogChangeRow(string Kind, string Name, ChangeKind Change, string VersionText);`
  - `public static class CatalogChangeRows { public static IReadOnlyList<CatalogChangeRow> Build(CatalogUpdateCheck check); }` — rows ordered Added, Updated, Removed; stable within a group; workloads before workflows within a group.
  - Test factory `CatalogChecks` (internal, `DiffusionNexus.Installer.Tests.Support`): `Remote(int version, CatalogChannel channel)`, `Available(int version = 4, CatalogChannel channel = Stable, IReadOnlyList<WorkloadChange>? workloads = null, IReadOnlyList<WorkflowChange>? workflows = null)`, `Outcome(CatalogUpdateOutcome outcome, string? error = null)`, `WorkloadUpdated(string name, string from, string to)`, `WorkloadAdded(string name, string version)`, `WorkflowAdded(string name, string version, params string[] workloadNames)`.

- [ ] **Step 1: Write the test factory**

Create `DiffusionNexus.Installer.Tests/Support/CatalogChecks.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>Hand-built SDK check results. The SDK's own tests cover CheckAsync; these describe its output.</summary>
internal static class CatalogChecks
{
    public static CatalogManifest Remote(int version, CatalogChannel channel) => new()
    {
        CatalogVersion = version,
        Channel = channel,
        Commit = "0123456",
        GeneratedAt = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero),
        Archive = new CatalogArchiveInfo("catalog.zip", new string('0', 64), 1024),
    };

    public static CatalogUpdateCheck Available(
        int version = 4,
        CatalogChannel channel = CatalogChannel.Stable,
        IReadOnlyList<WorkloadChange>? workloads = null,
        IReadOnlyList<WorkflowChange>? workflows = null) =>
        new(CatalogUpdateOutcome.UpdatesAvailable, channel, Remote(version, channel), new LocalCatalogState(),
            workloads ?? [WorkloadUpdated("Krea-2-Turbo", "V1.0", "V1.1")], workflows ?? [], null);

    public static CatalogUpdateCheck Outcome(CatalogUpdateOutcome outcome, string? error = null) =>
        new(outcome, CatalogChannel.Stable, null, new LocalCatalogState(), [], [], error);

    public static WorkloadChange WorkloadUpdated(string name, string from, string to) =>
        new(Guid.NewGuid(), name, ChangeKind.Updated, from, to);

    public static WorkloadChange WorkloadAdded(string name, string version) =>
        new(Guid.NewGuid(), name, ChangeKind.Added, null, version);

    public static WorkloadChange WorkloadRemoved(string name, string version) =>
        new(Guid.NewGuid(), name, ChangeKind.Removed, version, null);

    public static WorkflowChange WorkflowAdded(string name, string version, params string[] workloadNames) =>
        new(Guid.NewGuid(), name, ChangeKind.Added, workloadNames, null, version);

    public static WorkflowChange WorkflowUpdated(string name, string from, string to, params string[] workloadNames) =>
        new(Guid.NewGuid(), name, ChangeKind.Updated, workloadNames, from, to);
}
```

- [ ] **Step 2: Write the failing tests**

Create `DiffusionNexus.Installer.Tests/Updates/CatalogChangeRowsTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Updates;

/// <summary>
/// The rows /updates shows are the rows the editor's Release dialog showed the author: same
/// grouping, same "from → to" text. Anything else and "this is what users will see" is a lie.
/// </summary>
public class CatalogChangeRowsTests
{
    [Fact]
    public void Groups_added_then_updated_then_removed_keeping_input_order_within_a_group()
    {
        var check = CatalogChecks.Available(
            workloads:
            [
                CatalogChecks.WorkloadRemoved("Old", "V1.0"),
                CatalogChecks.WorkloadUpdated("Krea-2-Turbo", "V1.0", "V1.1"),
                CatalogChecks.WorkloadAdded("Ernie-Image-Turbo", "V1.0"),
                CatalogChecks.WorkloadUpdated("Wan 2.2", "V2.0", "V2.1"),
            ]);

        var rows = CatalogChangeRows.Build(check);

        rows.Select(r => (r.Change, r.Name)).Should().Equal(
            (ChangeKind.Added, "Ernie-Image-Turbo"),
            (ChangeKind.Updated, "Krea-2-Turbo"),
            (ChangeKind.Updated, "Wan 2.2"),
            (ChangeKind.Removed, "Old"));
    }

    [Fact]
    public void Version_text_depends_on_the_kind_of_change()
    {
        var check = CatalogChecks.Available(
            workloads:
            [
                CatalogChecks.WorkloadAdded("A", "V1.0"),
                CatalogChecks.WorkloadUpdated("B", "V1.0", "V1.1"),
                CatalogChecks.WorkloadRemoved("C", "V3.0"),
            ]);

        CatalogChangeRows.Build(check).Select(r => r.VersionText).Should().Equal("V1.0", "V1.0 → V1.1", "V3.0");
    }

    [Fact]
    public void Workflows_are_named_after_their_workload_and_follow_workloads_in_a_group()
    {
        var check = CatalogChecks.Available(
            workloads: [CatalogChecks.WorkloadAdded("Wan 2.2", "V1.0")],
            workflows: [CatalogChecks.WorkflowAdded("Text to Video", "V1.0", "Wan 2.2")]);

        var rows = CatalogChangeRows.Build(check);

        rows.Should().HaveCount(2);
        rows[0].Kind.Should().Be("Workload");
        rows[1].Should().Be(new CatalogChangeRow("Workflow", "Wan 2.2 – Text to Video", ChangeKind.Added, "V1.0"));
    }

    [Fact]
    public void A_workflow_with_no_workload_keeps_its_own_name()
    {
        var check = CatalogChecks.Available(workloads: [], workflows: [CatalogChecks.WorkflowAdded("Orphan", "V1.0")]);

        CatalogChangeRows.Build(check).Single().Name.Should().Be("Orphan");
    }

    [Fact]
    public void A_workflow_shared_by_several_workloads_lists_them_all()
    {
        var check = CatalogChecks.Available(workloads: [],
            workflows: [CatalogChecks.WorkflowUpdated("Upscale", "V1.0", "V1.1", "Wan 2.2", "LTX-2")]);

        CatalogChangeRows.Build(check).Single().Name.Should().Be("Wan 2.2, LTX-2 – Upscale");
    }

    [Fact]
    public void No_changes_means_no_rows()
    {
        CatalogChangeRows.Build(CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate)).Should().BeEmpty();
    }
}
```

- [ ] **Step 3: Run to see them fail**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~CatalogChangeRowsTests"
```
Expected: build error, `CatalogChangeRows` not found.

- [ ] **Step 4: Implement**

Create `DiffusionNexus.Installer.Core/Updates/CatalogChangeRows.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>One line of the "what changed" list. Kind is "Workload" or "Workflow".</summary>
public sealed record CatalogChangeRow(string Kind, string Name, ChangeKind Change, string VersionText);

/// <summary>
/// Flattens a check into the rows /updates renders, in the shape the catalog editor's Release
/// dialog shows the author: grouped Added, Updated, Removed; "from → to" for updates; workflows
/// named "Workload – Workflow". What the author approved is what the user reads.
/// </summary>
public static class CatalogChangeRows
{
    public static IReadOnlyList<CatalogChangeRow> Build(CatalogUpdateCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        var rows = new List<CatalogChangeRow>(check.Workloads.Count + check.Workflows.Count);
        foreach (var w in check.Workloads)
            rows.Add(new CatalogChangeRow("Workload", w.Name, w.Kind, VersionText(w.Kind, w.FromVersion, w.ToVersion)));
        foreach (var w in check.Workflows)
            rows.Add(new CatalogChangeRow("Workflow", WorkflowName(w), w.Kind, VersionText(w.Kind, w.FromVersion, w.ToVersion)));

        // OrderBy is stable, so input order (workloads first, then workflows) survives inside each group.
        return rows.OrderBy(r => GroupOrder(r.Change)).ToList();
    }

    private static int GroupOrder(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => 0,
        ChangeKind.Updated => 1,
        _ => 2,
    };

    private static string WorkflowName(WorkflowChange w) =>
        w.WorkloadNames.Count == 0 ? w.Name : $"{string.Join(", ", w.WorkloadNames)} – {w.Name}";

    private static string VersionText(ChangeKind kind, string? from, string? to) => kind switch
    {
        ChangeKind.Added => to ?? string.Empty,
        ChangeKind.Removed => from ?? string.Empty,
        _ => $"{from} → {to}",
    };
}
```

- [ ] **Step 5: Run to see them pass**

Same command. Expected: 6 tests pass.

- [ ] **Step 6: Commit**

```powershell
git add DiffusionNexus.Installer.Core/Updates/CatalogChangeRows.cs DiffusionNexus.Installer.Tests/Support/CatalogChecks.cs DiffusionNexus.Installer.Tests/Updates/CatalogChangeRowsTests.cs
git commit -m "feat(core): CatalogChangeRows -- the editor's change list, rendered for the user" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 4: `ICatalogUpdateCoordinator` — contract, channel resolution, check, channel switch, session link

**Files:**
- Create: `DiffusionNexus.Installer.Core/Updates/ICatalogUpdateCoordinator.cs`
- Create: `DiffusionNexus.Installer.Core/Updates/CatalogUpdateCoordinator.cs`
- Create: `DiffusionNexus.Installer.Tests/Support/FakeCatalogUpdateService.cs`
- Test: `DiffusionNexus.Installer.Tests/Updates/CatalogUpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: `CatalogChannelResolver` (Task 2); SDK `ICatalogUpdateService { Task<CatalogUpdateCheck> CheckAsync(CancellationToken); Task<CatalogApplyResult> ApplyAsync(CatalogUpdateCheck, CatalogSections, IProgress<CatalogDownloadProgress>?, CancellationToken); }`; SDK `CatalogOptions { CatalogChannel Channel; string InstalledCatalogPath; string? LocalOverridePath; }`; SDK `LocalCatalogState.Load(string dir)`; SDK `IUserSettingsRepository { Task<UserSettings> GetOrCreateForCurrentUserAsync(CancellationToken); Task<UserSettings> SaveAsync(UserSettings, CancellationToken); }`; Core `IInstallSession { InstallPhase Phase; WizardPlan? Plan; event Action? Changed; }`.
- Produces (namespace `DiffusionNexus.Installer.Core.Updates`):

```csharp
public enum CatalogUpdatePhase { Idle, Checking, Checked, Applying, Applied }

public interface ICatalogUpdateCoordinator
{
    CatalogChannel Channel { get; }
    CatalogChannelSource ChannelSource { get; }
    string? OverridePath { get; }
    CatalogUpdatePhase Phase { get; }
    CatalogUpdateCheck? LastCheck { get; }
    LocalCatalogState? Installed { get; }
    CatalogDownloadProgress? Progress { get; }
    CatalogApplyResult? LastApply { get; }
    bool UpdateAvailable { get; }      // LastCheck is UpdatesAvailable and Phase != Applied
    bool CanApply { get; }             // UpdateAvailable && Phase == Checked && no install running
    string? ApplyBlockedReason { get; } // non-null only while UpdateAvailable and an install runs
    event Action? Changed;
    Task CheckAsync(CancellationToken ct = default);
    Task ApplyAsync(CancellationToken ct = default);
    Task SetChannelAsync(CatalogChannel channel, CancellationToken ct = default);
}
```
  - `CatalogUpdateCoordinator(ICatalogUpdateService updates, CatalogOptions options, IUserSettingsRepository settings, IInstallSession session, Func<string?> readEnvironment, ILogger<CatalogUpdateCoordinator>? logger = null)` — also `IDisposable`.
  - In this task `ApplyAsync` only implements its refusal path (returns without calling the SDK when `!CanApply`). Task 5 adds the download.

- [ ] **Step 1: Write the fake SDK service**

Create `DiffusionNexus.Installer.Tests/Support/FakeCatalogUpdateService.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>
/// Scriptable stand-in for the SDK's update service. Set HoldCheck / HoldApply to keep a call
/// in flight until the test releases it, which is how the concurrency guards get exercised.
/// </summary>
internal sealed class FakeCatalogUpdateService : ICatalogUpdateService
{
    public Func<CatalogUpdateCheck> NextCheck { get; set; } = () => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate);
    public Func<CatalogApplyResult> NextApply { get; set; } = () => new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);
    public TaskCompletionSource? HoldCheck { get; set; }
    public TaskCompletionSource? HoldApply { get; set; }
    public bool ThrowCancelledOnApply { get; set; }

    public int CheckCalls { get; private set; }
    public int ApplyCalls { get; private set; }
    public CatalogSections? AppliedSections { get; private set; }
    public IProgress<CatalogDownloadProgress>? Progress { get; private set; }

    public async Task<CatalogUpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        CheckCalls++;
        if (HoldCheck is not null) await HoldCheck.Task;
        return NextCheck();
    }

    public async Task<CatalogApplyResult> ApplyAsync(CatalogUpdateCheck check, CatalogSections sections,
        IProgress<CatalogDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ApplyCalls++;
        AppliedSections = sections;
        Progress = progress;
        if (HoldApply is not null) await HoldApply.Task;
        if (ThrowCancelledOnApply) throw new OperationCanceledException();
        return NextApply();
    }
}
```

- [ ] **Step 2: Write the failing tests**

Create `DiffusionNexus.Installer.Tests/Updates/CatalogUpdateCoordinatorTests.cs`:

```csharp
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Updates;

public sealed class CatalogUpdateCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dn-coordinator-{Guid.NewGuid():N}");
    private readonly FakeCatalogUpdateService _service = new();
    private readonly CatalogOptions _options;
    private readonly Mock<IUserSettingsRepository> _settings = new();
    private readonly Mock<IInstallSession> _session = new();
    private readonly UserSettings _saved = new() { UserName = "tester" };
    private string? _environment;

    public CatalogUpdateCoordinatorTests()
    {
        Directory.CreateDirectory(_dir);
        _options = new CatalogOptions { InstalledCatalogPath = Path.Combine(_dir, "catalog") };
        _settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_saved);
        _settings.Setup(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((UserSettings s, CancellationToken _) => s);
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Idle);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private CatalogUpdateCoordinator Create() =>
        new(_service, _options, _settings.Object, _session.Object, () => _environment);

    private void WriteInstalledState(int version) =>
        new LocalCatalogState { Workloads = new SectionState(version, "abc", DateTimeOffset.UtcNow) }
            .Save(_options.InstalledCatalogPath);

    private static int Count(ICatalogUpdateCoordinator c) { var n = 0; c.Changed += () => n++; return n; }

    // ----- channel -----

    [Fact]
    public async Task The_first_check_resolves_the_saved_channel_into_the_sdk_options()
    {
        _saved.CatalogChannel = "Preview";
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Setting);
        _options.Channel.Should().Be(CatalogChannel.Preview, "CheckAsync in the SDK reads this and nothing else");
    }

    [Fact]
    public async Task The_environment_variable_wins_over_the_saved_channel()
    {
        _saved.CatalogChannel = "Preview";
        _environment = "stable";
        using var coordinator = Create();

        await coordinator.CheckAsync();

        (coordinator.Channel, coordinator.ChannelSource).Should().Be((CatalogChannel.Stable, CatalogChannelSource.Environment));
    }

    [Fact]
    public async Task An_unreadable_settings_file_falls_back_to_stable_and_still_checks()
    {
        _settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("locked"));
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Default);
        _service.CheckCalls.Should().Be(1);
    }

    [Fact]
    public void The_override_path_is_the_sdk_options_override_path()
    {
        _options.LocalOverridePath = @"E:\Repos\DiffusionNexus.Catalog";
        using var coordinator = Create();
        coordinator.OverridePath.Should().Be(@"E:\Repos\DiffusionNexus.Catalog");
    }

    // ----- check -----

    [Fact]
    public async Task A_check_stores_the_outcome_reloads_the_installed_state_and_ends_checked()
    {
        WriteInstalledState(3);
        _service.NextCheck = () => CatalogChecks.Available(4);
        using var coordinator = Create();
        var raised = 0;
        coordinator.Changed += () => raised++;

        await coordinator.CheckAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpdatesAvailable);
        coordinator.Installed!.HighestCatalogVersion.Should().Be(3);
        coordinator.UpdateAvailable.Should().BeTrue();
        coordinator.CanApply.Should().BeTrue();
        raised.Should().BeGreaterThanOrEqualTo(2, "once entering Checking, once leaving it");
    }

    [Fact]
    public async Task An_up_to_date_check_offers_nothing()
    {
        using var coordinator = Create();
        await coordinator.CheckAsync();

        coordinator.UpdateAvailable.Should().BeFalse();
        coordinator.CanApply.Should().BeFalse();
        coordinator.ApplyBlockedReason.Should().BeNull();
    }

    [Fact]
    public async Task A_second_check_while_one_is_running_joins_it_instead_of_calling_the_sdk_again()
    {
        _service.HoldCheck = new TaskCompletionSource();
        using var coordinator = Create();

        var first = coordinator.CheckAsync();
        var second = coordinator.CheckAsync();
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checking);

        _service.HoldCheck.SetResult();
        await Task.WhenAll(first, second);

        _service.CheckCalls.Should().Be(1);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
    }

    // ----- apply refusals (the download itself is Task 5) -----

    [Fact]
    public async Task Apply_is_refused_before_a_check_found_an_update()
    {
        using var coordinator = Create();

        await coordinator.ApplyAsync();
        await coordinator.CheckAsync();          // up to date
        await coordinator.ApplyAsync();

        _service.ApplyCalls.Should().Be(0);
    }

    [Fact]
    public async Task Apply_is_refused_while_an_install_is_running_and_says_which_one()
    {
        var plan = await new WizardModuleRegistry(() => [])
            .BuildPlanAsync(new WizardSelection { Workload = new InstallationConfiguration { Name = "Krea-2-Turbo" } });
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        _session.SetupGet(s => s.Plan).Returns(plan);
        _service.NextCheck = () => CatalogChecks.Available();
        using var coordinator = Create();
        await coordinator.CheckAsync();

        coordinator.UpdateAvailable.Should().BeTrue();
        coordinator.CanApply.Should().BeFalse();
        coordinator.ApplyBlockedReason.Should().Be("It can be applied once Krea-2-Turbo has finished.");

        await coordinator.ApplyAsync();
        _service.ApplyCalls.Should().Be(0);
    }

    // ----- channel switch -----

    [Fact]
    public async Task Setting_the_channel_saves_it_repoints_the_sdk_and_forgets_the_last_check()
    {
        _service.NextCheck = () => CatalogChecks.Available();
        using var coordinator = Create();
        await coordinator.CheckAsync();

        await coordinator.SetChannelAsync(CatalogChannel.Preview);

        _settings.Verify(s => s.SaveAsync(It.Is<UserSettings>(u => u.CatalogChannel == "Preview"), It.IsAny<CancellationToken>()), Times.Once);
        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Setting);
        _options.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.LastCheck.Should().BeNull();
        coordinator.UpdateAvailable.Should().BeFalse();
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Idle);
    }

    [Fact]
    public async Task Setting_the_channel_under_an_environment_override_saves_but_keeps_following_the_environment()
    {
        _environment = "stable";
        using var coordinator = Create();

        await coordinator.SetChannelAsync(CatalogChannel.Preview);

        _saved.CatalogChannel.Should().Be("Preview");
        (coordinator.Channel, coordinator.ChannelSource).Should().Be((CatalogChannel.Stable, CatalogChannelSource.Environment));
    }

    [Fact]
    public async Task Setting_the_channel_is_refused_while_a_check_is_running()
    {
        _service.HoldCheck = new TaskCompletionSource();
        using var coordinator = Create();
        var check = coordinator.CheckAsync();

        await coordinator.SetChannelAsync(CatalogChannel.Preview);

        _settings.Verify(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Never);
        _service.HoldCheck.SetResult();
        await check;
    }

    // ----- session link -----

    [Fact]
    public void An_install_starting_or_finishing_re_raises_changed_once_per_flip()
    {
        using var coordinator = Create();
        var raised = 0;
        coordinator.Changed += () => raised++;

        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        _session.Raise(s => s.Changed += null);
        _session.Raise(s => s.Changed += null);   // progress ticks: same state, no re-raise
        raised.Should().Be(1);

        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        _session.Raise(s => s.Changed += null);
        raised.Should().Be(2);
    }

    [Fact]
    public void Disposing_unsubscribes_from_the_session()
    {
        var coordinator = Create();
        coordinator.Dispose();
        _session.VerifyRemove(s => s.Changed -= It.IsAny<Action>(), Times.Once);
    }
}
```

- [ ] **Step 3: Run to see them fail**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~CatalogUpdateCoordinatorTests"
```
Expected: build errors, `ICatalogUpdateCoordinator` / `CatalogUpdateCoordinator` not found.

- [ ] **Step 4: Write the interface**

Create `DiffusionNexus.Installer.Core/Updates/ICatalogUpdateCoordinator.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>Idle → Checking → Checked → Applying → Applied. A failed or cancelled apply returns to Checked.</summary>
public enum CatalogUpdatePhase { Idle, Checking, Checked, Applying, Applied }

/// <summary>
/// The catalog-update state the UI binds to. One process-wide instance, like the app updater's
/// <c>UpdaterLog</c>: pages subscribe to <see cref="Changed"/> (a plain Action they can
/// unsubscribe) and read the properties; they never own update state themselves.
/// </summary>
public interface ICatalogUpdateCoordinator
{
    /// <summary>The channel the next check reads. Resolved env var → saved setting → Stable.</summary>
    CatalogChannel Channel { get; }
    CatalogChannelSource ChannelSource { get; }

    /// <summary>The SDK's local override folder, for the OverrideActive message. Null when none is configured.</summary>
    string? OverridePath { get; }

    CatalogUpdatePhase Phase { get; }
    CatalogUpdateCheck? LastCheck { get; }

    /// <summary>catalog-state.json as of the last check or apply. Null until the first check finishes.</summary>
    LocalCatalogState? Installed { get; }

    /// <summary>Non-null only while <see cref="Phase"/> is Applying.</summary>
    CatalogDownloadProgress? Progress { get; }
    CatalogApplyResult? LastApply { get; }

    bool UpdateAvailable { get; }
    bool CanApply { get; }

    /// <summary>Why Apply is withheld although an update is available (an install is running). Null otherwise.</summary>
    string? ApplyBlockedReason { get; }

    /// <summary>Raised after every state change. Handlers marshal to their own context.</summary>
    event Action? Changed;

    /// <summary>Never throws. A check already in flight is joined, not repeated.</summary>
    Task CheckAsync(CancellationToken ct = default);

    /// <summary>Never throws. A no-op unless <see cref="CanApply"/>.</summary>
    Task ApplyAsync(CancellationToken ct = default);

    /// <summary>Saves the preference and forgets the last check. Refused (no-op) while checking or applying. Settings I/O errors propagate.</summary>
    Task SetChannelAsync(CatalogChannel channel, CancellationToken ct = default);
}
```

- [ ] **Step 5: Write the coordinator (check, channel, session; apply = refusal only)**

Create `DiffusionNexus.Installer.Core/Updates/CatalogUpdateCoordinator.cs`:

```csharp
using System.Text.Json;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Services.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>
/// Owns the check/apply lifecycle around the SDK's <see cref="ICatalogUpdateService"/>. This is
/// the one place that writes <see cref="CatalogOptions.Channel"/>, which the SDK reads on every
/// check. Every step logs, so a stalled update shows its last successful step in the console.
/// </summary>
public sealed class CatalogUpdateCoordinator : ICatalogUpdateCoordinator, IDisposable
{
    private readonly ICatalogUpdateService _updates;
    private readonly CatalogOptions _options;
    private readonly IUserSettingsRepository _settings;
    private readonly IInstallSession _session;
    private readonly Func<string?> _readEnvironment;
    private readonly ILogger<CatalogUpdateCoordinator> _logger;

    // One lock for the state the UI reads; the in-flight task is the concurrency guard.
    private readonly Lock _gate = new();
    private Task? _inFlight;
    private bool _channelResolved;
    private bool _installRunning;

    public CatalogUpdateCoordinator(
        ICatalogUpdateService updates,
        CatalogOptions options,
        IUserSettingsRepository settings,
        IInstallSession session,
        Func<string?> readEnvironment,
        ILogger<CatalogUpdateCoordinator>? logger = null)
    {
        _updates = updates;
        _options = options;
        _settings = settings;
        _session = session;
        _readEnvironment = readEnvironment;
        _logger = logger ?? NullLogger<CatalogUpdateCoordinator>.Instance;

        _installRunning = session.Phase == InstallPhase.Running;
        _session.Changed += OnSessionChanged;
    }

    public CatalogChannel Channel { get; private set; } = CatalogChannel.Stable;
    public CatalogChannelSource ChannelSource { get; private set; } = CatalogChannelSource.Default;
    public string? OverridePath => _options.LocalOverridePath;
    public CatalogUpdatePhase Phase { get; private set; } = CatalogUpdatePhase.Idle;
    public CatalogUpdateCheck? LastCheck { get; private set; }
    public LocalCatalogState? Installed { get; private set; }
    public CatalogDownloadProgress? Progress { get; private set; }
    public CatalogApplyResult? LastApply { get; private set; }

    public bool UpdateAvailable =>
        LastCheck?.Outcome == CatalogUpdateOutcome.UpdatesAvailable && Phase != CatalogUpdatePhase.Applied;

    // Reads the session live rather than the cached flag: the flag only decides when to re-raise.
    public bool CanApply => UpdateAvailable && Phase == CatalogUpdatePhase.Checked && !InstallRunning;

    public string? ApplyBlockedReason => UpdateAvailable && InstallRunning
        ? $"It can be applied once {_session.Plan?.Selection.Workload.Name ?? "the current install"} has finished."
        : null;

    public event Action? Changed;

    private bool InstallRunning => _session.Phase == InstallPhase.Running;

    public Task CheckAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false }) return _inFlight;
            Phase = CatalogUpdatePhase.Checking;
            Progress = null;
            _inFlight = Task.Run(() => CheckCoreAsync(ct), CancellationToken.None);
        }
        Raise();
        return _inFlight;
    }

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        try
        {
            await EnsureChannelAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Catalog update check started on {Channel}", Channel);

            var check = await _updates.CheckAsync(ct).ConfigureAwait(false);
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);

            lock (_gate)
            {
                LastCheck = check;
                Installed = installed;
                Phase = CatalogUpdatePhase.Checked;
            }
            _logger.LogInformation("Catalog update check: {Outcome} (remote v{Remote}, installed v{Installed}, {Workloads} workload / {Workflows} workflow changes){Error}",
                check.Outcome, check.Remote?.CatalogVersion, installed.HighestCatalogVersion, check.Workloads.Count, check.Workflows.Count,
                check.Error is null ? string.Empty : ": " + check.Error);
        }
        catch (Exception ex)
        {
            // The SDK's CheckAsync never throws and Load never throws, so this is the last line of
            // defence for a background task nobody awaits: report, never crash.
            _logger.LogError(ex, "Catalog update check failed unexpectedly");
            lock (_gate)
            {
                LastCheck = new CatalogUpdateCheck(CatalogUpdateOutcome.Failed, Channel, null, Installed ?? new LocalCatalogState(), [], [], ex.Message);
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        Raise();
    }

    public Task ApplyAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!CanApply)
            {
                _logger.LogInformation("Catalog apply refused: phase {Phase}, update available {Available}, install running {Running}",
                    Phase, UpdateAvailable, InstallRunning);
                return _inFlight is { IsCompleted: false } ? _inFlight : Task.CompletedTask;
            }
        }
        // Task 5 replaces this with the download + swap.
        return Task.CompletedTask;
    }

    public async Task SetChannelAsync(CatalogChannel channel, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying)
            {
                _logger.LogInformation("Catalog channel change to {Channel} refused while {Phase}", channel, Phase);
                return;
            }
        }

        var settings = await _settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        settings.CatalogChannel = channel.ToString();
        await _settings.SaveAsync(settings, ct).ConfigureAwait(false);
        _logger.LogInformation("Catalog channel preference saved: {Channel}", channel);

        lock (_gate)
        {
            ApplyResolution(_readEnvironment(), settings.CatalogChannel);
            _channelResolved = true;
            LastCheck = null;
            LastApply = null;
            Progress = null;
            Phase = CatalogUpdatePhase.Idle;
        }
        Raise();
    }

    private async Task EnsureChannelAsync(CancellationToken ct)
    {
        lock (_gate) { if (_channelResolved) return; }

        string? saved = null;
        try
        {
            saved = (await _settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false)).CatalogChannel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "User settings could not be read; following the Stable catalog channel");
        }

        lock (_gate)
        {
            ApplyResolution(_readEnvironment(), saved);
            _channelResolved = true;
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void ApplyResolution(string? environmentValue, string? savedValue)
    {
        var (channel, source) = CatalogChannelResolver.Resolve(environmentValue, savedValue);
        Channel = channel;
        ChannelSource = source;
        _options.Channel = channel;
        _logger.LogInformation("Following the {Channel} catalog channel ({Source})", channel, source);
    }

    private void OnSessionChanged()
    {
        var running = InstallRunning;
        bool flipped;
        lock (_gate)
        {
            flipped = running != _installRunning;
            _installRunning = running;
        }
        // Only on a flip: the session raises ~10x a second during an install, and every page
        // that shows the Apply button would re-render on each tick for nothing.
        if (flipped) Raise();
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose() => _session.Changed -= OnSessionChanged;
}
```

- [ ] **Step 6: Run to see them pass**

Same command. Expected: all 14 tests in the class pass. If `Lock` is unavailable in Core, check the csproj targets `net10.0` (it does; `UpdaterLog` uses `Lock` already in the Electron project).

- [ ] **Step 7: Commit**

```powershell
git add DiffusionNexus.Installer.Core/Updates/ICatalogUpdateCoordinator.cs DiffusionNexus.Installer.Core/Updates/CatalogUpdateCoordinator.cs DiffusionNexus.Installer.Tests/Support/FakeCatalogUpdateService.cs DiffusionNexus.Installer.Tests/Updates/CatalogUpdateCoordinatorTests.cs
git commit -m "feat(core): CatalogUpdateCoordinator -- channel resolution, check, channel switch, install-session link" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 5: Coordinator — apply (download, progress, result, cancel)

**Files:**
- Modify: `DiffusionNexus.Installer.Core/Updates/CatalogUpdateCoordinator.cs` (`ApplyAsync`)
- Test: `DiffusionNexus.Installer.Tests/Updates/CatalogUpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: SDK `CatalogApplyResult(CatalogSections Applied, CatalogSections Failed, string? Error)`, `CatalogDownloadProgress(long BytesReceived, long? TotalBytes)`, `CatalogSections.All`.
- Produces: the full `ApplyAsync` behaviour of `ICatalogUpdateCoordinator` (Task 4's contract). Consumed by Task 9.

- [ ] **Step 1: Write the failing tests**

Add to `CatalogUpdateCoordinatorTests.cs` before the `// ----- channel switch -----` marker:

```csharp
    // ----- apply -----

    private async Task<CatalogUpdateCoordinator> CheckedWithUpdateAsync()
    {
        WriteInstalledState(3);
        _service.NextCheck = () => CatalogChecks.Available(4);
        var coordinator = Create();
        await coordinator.CheckAsync();
        return coordinator;
    }

    [Fact]
    public async Task Apply_sends_both_sections_forwards_progress_and_ends_applied()
    {
        _service.HoldApply = new TaskCompletionSource();
        using var coordinator = await CheckedWithUpdateAsync();
        var raised = 0;
        coordinator.Changed += () => raised++;

        var apply = coordinator.ApplyAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applying);
        coordinator.CanApply.Should().BeFalse("a second click must not start a second download");
        SpinWait.SpinUntil(() => _service.Progress is not null, 2000).Should().BeTrue();
        _service.AppliedSections.Should().Be(CatalogSections.All);

        _service.Progress!.Report(new CatalogDownloadProgress(50, 100));
        coordinator.Progress.Should().Be(new CatalogDownloadProgress(50, 100));
        var raisedBeforeFinish = raised;
        raisedBeforeFinish.Should().BeGreaterThanOrEqualTo(2, "entering Applying and the progress report");

        WriteInstalledState(4);   // what the SDK's swap leaves on disk
        _service.HoldApply.SetResult();
        await apply;

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
        coordinator.LastApply.Should().Be(new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null));
        coordinator.Progress.Should().BeNull();
        coordinator.Installed!.HighestCatalogVersion.Should().Be(4);
        coordinator.UpdateAvailable.Should().BeFalse("the dot and the notice go away once it is in");
        coordinator.CanApply.Should().BeFalse();
        raised.Should().BeGreaterThan(raisedBeforeFinish);
    }

    [Fact]
    public async Task A_failed_apply_returns_to_checked_with_the_error_so_it_can_be_retried()
    {
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "sha256 mismatch");
        using var coordinator = await CheckedWithUpdateAsync();

        await coordinator.ApplyAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        coordinator.LastApply!.Error.Should().Be("sha256 mismatch");
        coordinator.UpdateAvailable.Should().BeTrue();
        coordinator.CanApply.Should().BeTrue();
    }

    [Fact]
    public async Task A_partial_apply_also_returns_to_checked()
    {
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.Workloads, CatalogSections.Workflows, "workflows/ locked");
        using var coordinator = await CheckedWithUpdateAsync();

        await coordinator.ApplyAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        coordinator.LastApply!.Applied.Should().Be(CatalogSections.Workloads);
    }

    [Fact]
    public async Task A_cancelled_apply_returns_to_checked_without_an_error()
    {
        _service.ThrowCancelledOnApply = true;
        using var coordinator = await CheckedWithUpdateAsync();

        await coordinator.ApplyAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        coordinator.LastApply.Should().BeNull();
        coordinator.Progress.Should().BeNull();
    }

    [Fact]
    public async Task A_check_requested_while_applying_joins_the_apply()
    {
        _service.HoldApply = new TaskCompletionSource();
        using var coordinator = await CheckedWithUpdateAsync();
        var apply = coordinator.ApplyAsync();

        var check = coordinator.CheckAsync();
        check.Should().BeSameAs(apply);
        _service.CheckCalls.Should().Be(1);

        _service.HoldApply.SetResult();
        await apply;
    }

    [Fact]
    public async Task After_a_successful_apply_a_new_check_can_run()
    {
        using var coordinator = await CheckedWithUpdateAsync();
        await coordinator.ApplyAsync();
        _service.NextCheck = () => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate);

        await coordinator.CheckAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        _service.CheckCalls.Should().Be(2);
    }
```

- [ ] **Step 2: Run to see the new tests fail**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~CatalogUpdateCoordinatorTests"
```
Expected: the six new tests fail (`ApplyCalls` stays 0, Phase stays Checked); the Task 4 tests still pass.

- [ ] **Step 3: Implement the apply path**

Replace the whole `ApplyAsync` method in `CatalogUpdateCoordinator.cs` with:

```csharp
    public Task ApplyAsync(CancellationToken ct = default)
    {
        CatalogUpdateCheck check;
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false }) return _inFlight;
            if (!CanApply)
            {
                _logger.LogInformation("Catalog apply refused: phase {Phase}, update available {Available}, install running {Running}",
                    Phase, UpdateAvailable, InstallRunning);
                return Task.CompletedTask;
            }
            check = LastCheck!;
            Phase = CatalogUpdatePhase.Applying;
            LastApply = null;
            Progress = null;
            _inFlight = Task.Run(() => ApplyCoreAsync(check, ct), CancellationToken.None);
        }
        Raise();
        return _inFlight;
    }

    private async Task ApplyCoreAsync(CatalogUpdateCheck check, CancellationToken ct)
    {
        _logger.LogInformation("Catalog apply started: v{Version} from {Channel}, both sections", check.Remote?.CatalogVersion, check.Channel);
        try
        {
            var result = await _updates.ApplyAsync(check, CatalogSections.All, new ProgressRelay(this), ct).ConfigureAwait(false);
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
            var succeeded = result.Failed == CatalogSections.None && result.Error is null;

            lock (_gate)
            {
                LastApply = result;
                Installed = installed;
                Progress = null;
                Phase = succeeded ? CatalogUpdatePhase.Applied : CatalogUpdatePhase.Checked;
            }
            _logger.LogInformation("Catalog apply finished: applied={Applied} failed={Failed} installed v{Installed}{Error}",
                result.Applied, result.Failed, installed.HighestCatalogVersion, result.Error is null ? string.Empty : ": " + result.Error);
        }
        catch (OperationCanceledException)
        {
            // The SDK lets a caller's cancel through on purpose; it is not a failure and is not shown as one.
            _logger.LogInformation("Catalog apply cancelled; nothing was changed");
            lock (_gate)
            {
                LastApply = null;
                Progress = null;
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Catalog apply failed unexpectedly");
            lock (_gate)
            {
                LastApply = new CatalogApplyResult(CatalogSections.None, CatalogSections.All, ex.Message);
                Progress = null;
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        Raise();
    }

    /// <summary>
    /// Not <see cref="System.Progress{T}"/>: that posts to the captured SynchronizationContext, which
    /// on a pool thread means "whenever", and the page would render stale percentages out of order.
    /// </summary>
    private sealed class ProgressRelay(CatalogUpdateCoordinator owner) : IProgress<CatalogDownloadProgress>
    {
        public void Report(CatalogDownloadProgress value)
        {
            lock (owner._gate) { owner.Progress = value; }
            owner.Raise();
        }
    }
```

- [ ] **Step 4: Run to see them pass**

Same command. Expected: all 20 tests in the class pass.

- [ ] **Step 5: Run the whole installer suite**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj
```
Expected: green.

- [ ] **Step 6: Commit**

```powershell
git add DiffusionNexus.Installer.Core/Updates/CatalogUpdateCoordinator.cs DiffusionNexus.Installer.Tests/Updates/CatalogUpdateCoordinatorTests.cs
git commit -m "feat(core): CatalogUpdateCoordinator applies both sections with progress; failed or cancelled returns to Checked" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: DI registration + startup check

**Files:**
- Modify: `DiffusionNexus.Installer.Core/CoreServiceCollectionExtensions.cs`
- Create: `DiffusionNexus.Installer.Electron/Services/CatalogUpdateStartupCheck.cs`
- Modify: `DiffusionNexus.Installer.Electron/Services/HostServiceCollectionExtensions.cs`
- Test: `DiffusionNexus.Installer.Tests/DependencyInjectionTests.cs`, `DiffusionNexus.Installer.Tests/Services/CatalogUpdateStartupCheckTests.cs`
- Create: `DiffusionNexus.Installer.Tests/Support/StubCatalogUpdateCoordinator.cs`

**Interfaces:**
- Produces: `ICatalogUpdateCoordinator` singleton from `AddInstallerCore`; `CatalogUpdateStartupCheck : IHostedService` registered by `AddInstallerHostServices`; test stub `StubCatalogUpdateCoordinator : ICatalogUpdateCoordinator` with settable properties, counters `Checks`, `Applies`, `ChannelSet`, method `RaiseChanged()`, property `Subscribers`.

- [ ] **Step 1: Write the stub (used here and by every bUnit fixture from Task 7 on)**

Create `DiffusionNexus.Installer.Tests/Support/StubCatalogUpdateCoordinator.cs`:

```csharp
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>Settable coordinator for component tests: the fixture sets state, the component renders it.</summary>
internal sealed class StubCatalogUpdateCoordinator : ICatalogUpdateCoordinator
{
    public CatalogChannel Channel { get; set; } = CatalogChannel.Stable;
    public CatalogChannelSource ChannelSource { get; set; } = CatalogChannelSource.Default;
    public string? OverridePath { get; set; }
    public CatalogUpdatePhase Phase { get; set; } = CatalogUpdatePhase.Idle;
    public CatalogUpdateCheck? LastCheck { get; set; }
    public LocalCatalogState? Installed { get; set; }
    public CatalogDownloadProgress? Progress { get; set; }
    public CatalogApplyResult? LastApply { get; set; }
    public string? ApplyBlockedReason { get; set; }

    public bool UpdateAvailable => LastCheck?.Outcome == CatalogUpdateOutcome.UpdatesAvailable && Phase != CatalogUpdatePhase.Applied;
    public bool CanApply => UpdateAvailable && Phase == CatalogUpdatePhase.Checked && ApplyBlockedReason is null;

    public event Action? Changed;

    public int Checks { get; private set; }
    public int Applies { get; private set; }
    public CatalogChannel? ChannelSet { get; private set; }
    public int Subscribers => Changed?.GetInvocationList().Length ?? 0;

    public Task CheckAsync(CancellationToken ct = default) { Checks++; return Task.CompletedTask; }
    public Task ApplyAsync(CancellationToken ct = default) { Applies++; return Task.CompletedTask; }
    public Task SetChannelAsync(CatalogChannel channel, CancellationToken ct = default) { ChannelSet = channel; Channel = channel; return Task.CompletedTask; }

    public void RaiseChanged() => Changed?.Invoke();
}
```

- [ ] **Step 2: Write the failing tests**

Add to `DependencyInjectionTests.cs` (inside the class):

```csharp
    [Fact]
    public void The_catalog_update_coordinator_is_a_singleton_and_the_startup_check_is_hosted()
    {
        using var provider = Build();

        var coordinator = provider.GetRequiredService<ICatalogUpdateCoordinator>();
        coordinator.Should().BeSameAs(provider.GetRequiredService<ICatalogUpdateCoordinator>(),
            "the top bar, the welcome page and /updates must all read one state");

        // The startup check is what makes "most installs would simply never update" untrue for
        // the catalog too. A dropped AddHostedService line would leave every page test green.
        provider.GetServices<IHostedService>().Should().ContainSingle(h => h is CatalogUpdateStartupCheck);
    }
```
Add `using DiffusionNexus.Installer.Core.Updates;` and `using Microsoft.Extensions.Hosting;` to that file's usings.

Create `DiffusionNexus.Installer.Tests/Services/CatalogUpdateStartupCheckTests.cs`:

```csharp
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Services;

public class CatalogUpdateStartupCheckTests
{
    [Fact]
    public async Task Start_returns_at_once_and_runs_one_check_in_the_background()
    {
        var coordinator = new StubCatalogUpdateCoordinator();
        var service = new CatalogUpdateStartupCheck(coordinator);

        var start = service.StartAsync(CancellationToken.None);

        start.IsCompleted.Should().BeTrue("a slow GitHub must never delay the window");
        await start;
        SpinWait.SpinUntil(() => coordinator.Checks == 1, 2000).Should().BeTrue();
        await service.StopAsync(CancellationToken.None);
    }
}
```

- [ ] **Step 3: Run to see them fail**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~DependencyInjectionTests|FullyQualifiedName~CatalogUpdateStartupCheckTests"
```
Expected: build error, `CatalogUpdateStartupCheck` not found.

- [ ] **Step 4: Register the coordinator in Core**

In `CoreServiceCollectionExtensions.cs`, add `using DiffusionNexus.Installer.Core.Updates;`, `using DiffusionNexus.Installer.SDK.Catalog.Updates;`, `using DiffusionNexus.Installer.SDK.Services.Settings;`, `using Microsoft.Extensions.Logging;` and, after the `services.AddSingleton<IModelPreflight, ModelPreflight>();` line:

```csharp
        // The one writer of CatalogOptions.Channel. Explicit factory so the env-var reader is a
        // plain delegate (tests pass their own) and the logger stays optional -- the DI test's
        // container registers no logging, exactly like CommunityLinksCache.
        services.AddSingleton<ICatalogUpdateCoordinator>(sp => new CatalogUpdateCoordinator(
            sp.GetRequiredService<ICatalogUpdateService>(),
            sp.GetRequiredService<CatalogOptions>(),
            sp.GetRequiredService<IUserSettingsRepository>(),
            sp.GetRequiredService<IInstallSession>(),
            () => Environment.GetEnvironmentVariable(CatalogChannelResolver.EnvironmentVariable),
            sp.GetService<ILogger<CatalogUpdateCoordinator>>()));
```

- [ ] **Step 5: Write the hosted service and register it**

Create `DiffusionNexus.Installer.Electron/Services/CatalogUpdateStartupCheck.cs`:

```csharp
using DiffusionNexus.Installer.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// One catalog check when the app starts, next to the app self-update check in Program.cs. An
/// installer is a short-lived, occasionally-run app: if it waited for the user to ask, most
/// installs would never see a newer catalog. Fire-and-forget so a slow or unreachable GitHub
/// cannot delay the window. Unlike the app updater it does not need Electron, so it also runs
/// under plain `dotnet run`.
/// </summary>
public sealed class CatalogUpdateStartupCheck(ICatalogUpdateCoordinator coordinator, ILogger<CatalogUpdateStartupCheck>? logger = null) : IHostedService
{
    private readonly ILogger<CatalogUpdateStartupCheck> _logger = logger ?? NullLogger<CatalogUpdateStartupCheck>.Instance;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await coordinator.CheckAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // CheckAsync never throws; this keeps an unobserved task exception from ever
                // becoming the reason the app looks broken.
                _logger.LogWarning(ex, "Startup catalog update check failed");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

In `HostServiceCollectionExtensions.cs`, after `services.AddSingleton<UpdaterLog>();`:

```csharp
        // The catalog counterpart of Program.cs's startup AutoUpdater check.
        services.AddHostedService<CatalogUpdateStartupCheck>();
```

- [ ] **Step 6: Run to see them pass**

Same command. Expected: both new tests pass and the existing DI tests still pass. If `ContainSingle` fails because two hosted services exist, list them with `provider.GetServices<IHostedService>().Select(h => h.GetType().Name)` and adjust the assertion to `Contain(h => h is CatalogUpdateStartupCheck)` only if another hosted service was already registered by the host (none is today).

- [ ] **Step 7: Commit**

```powershell
git add DiffusionNexus.Installer.Core/CoreServiceCollectionExtensions.cs DiffusionNexus.Installer.Electron/Services/CatalogUpdateStartupCheck.cs DiffusionNexus.Installer.Electron/Services/HostServiceCollectionExtensions.cs DiffusionNexus.Installer.Tests/DependencyInjectionTests.cs DiffusionNexus.Installer.Tests/Services/CatalogUpdateStartupCheckTests.cs DiffusionNexus.Installer.Tests/Support/StubCatalogUpdateCoordinator.cs
git commit -m "feat(installer): register the catalog update coordinator; check the catalog once at startup" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 7: Top bar attention dot + fixture helper

**Files:**
- Create: `DiffusionNexus.Installer.Tests/Support/UpdateSignals.cs`
- Modify: `DiffusionNexus.Installer.Electron/Components/Shared/TopBar.razor`
- Modify: `DiffusionNexus.Installer.Electron/wwwroot/app.css` (after `.top-bar-dev { … }`, line ~965)
- Modify tests: `Components/TopBarTests.cs`, `Components/ScreenShellTests.cs`, `Components/WelcomePageTests.cs`, `Components/WelcomeScriptFailureTests.cs`, `Components/SoftwareWorkloadsPageTests.cs`, `Components/InstallPageTests.cs`

**Interfaces:**
- Consumes: `ICatalogUpdateCoordinator` (Task 4), `UpdaterLog` (existing: `bool UpdateReady`, `event Action? Changed`), `StubCatalogUpdateCoordinator` (Task 6).
- Produces: `internal static class UpdateSignals { public static (StubCatalogUpdateCoordinator Catalog, UpdaterLog App) Register(IServiceCollection services); }` in `DiffusionNexus.Installer.Tests.Support`. `TopBar` now injects both services; the `/updates` link carries class `top-bar-attention` and a `title` when something waits.

- [ ] **Step 1: Write the fixture helper**

Create `DiffusionNexus.Installer.Tests/Support/UpdateSignals.cs`:

```csharp
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Electron.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>
/// What every screen that renders <c>TopBar</c> now needs: the two update signals the bar's dot
/// reads. One call per fixture, and the fixture keeps the handles to drive them.
/// </summary>
internal static class UpdateSignals
{
    public static (StubCatalogUpdateCoordinator Catalog, UpdaterLog App) Register(IServiceCollection services)
    {
        var catalog = new StubCatalogUpdateCoordinator();
        var app = new UpdaterLog();
        services.AddSingleton<ICatalogUpdateCoordinator>(catalog);
        services.AddSingleton(app);
        return (catalog, app);
    }
}
```

- [ ] **Step 2: Register the signals in every fixture that renders the bar**

Add `using DiffusionNexus.Installer.Tests.Support;` where missing, then:
- `ScreenShellTests` constructor: add `UpdateSignals.Register(Services);`
- `WelcomePageTests` constructor: add a field `private readonly (StubCatalogUpdateCoordinator Catalog, UpdaterLog App) _signals;` and `_signals = UpdateSignals.Register(Services);` (Task 8 uses it).
- `WelcomeScriptFailureTests`: next to its `Services.AddSingleton(OfflineCommunityLinks.Cache());` line (line ~47) add `UpdateSignals.Register(Services);`
- `SoftwareWorkloadsPageTests` constructor (line ~19-25): add `UpdateSignals.Register(Services);`
- `InstallPageTests`: both places that register `OfflineCommunityLinks.Cache()` (lines ~91 and ~342) get `UpdateSignals.Register(Services);` on the next line.
- `TopBarTests`: add a constructor `public TopBarTests() { _signals = UpdateSignals.Register(Services); }` and the field `private readonly (StubCatalogUpdateCoordinator Catalog, UpdaterLog App) _signals;`.

- [ ] **Step 3: Write the failing TopBar tests**

Append to `TopBarTests.cs` inside the class (add `using DiffusionNexus.Installer.Core.Updates;` and `using DiffusionNexus.Installer.Tests.Support;`):

```csharp
    private static string Updates => "a[href='/updates']";

    [Fact]
    public void Stays_plain_when_nothing_is_waiting()
    {
        var cut = Render<TopBar>();

        cut.Find(Updates).ClassList.Should().NotContain("top-bar-attention");
        cut.Find(Updates).HasAttribute("title").Should().BeFalse();
    }

    [Fact]
    public void Marks_the_updates_link_when_a_catalog_update_is_waiting()
    {
        _signals.Catalog.LastCheck = CatalogChecks.Available();
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;

        var cut = Render<TopBar>();

        cut.Find(Updates).ClassList.Should().Contain("top-bar-attention");
        cut.Find(Updates).GetAttribute("title").Should().Be("Catalog update available");
    }

    [Fact]
    public void Marks_it_when_an_app_update_is_ready()
    {
        _signals.App.MarkUpdateReady();

        var cut = Render<TopBar>();

        cut.Find(Updates).GetAttribute("title").Should().Be("App update ready");
    }

    [Fact]
    public void Names_both_when_both_are_waiting()
    {
        _signals.Catalog.LastCheck = CatalogChecks.Available();
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;
        _signals.App.MarkUpdateReady();

        Render<TopBar>().Find(Updates).GetAttribute("title").Should().Be("Catalog update available and app update ready");
    }

    [Fact]
    public void Lights_up_when_the_startup_check_finishes_after_the_bar_rendered()
    {
        var cut = Render<TopBar>();
        cut.Find(Updates).ClassList.Should().NotContain("top-bar-attention");

        _signals.Catalog.LastCheck = CatalogChecks.Available();
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;
        _signals.Catalog.RaiseChanged();

        cut.WaitForAssertion(() => cut.Find(Updates).ClassList.Should().Contain("top-bar-attention"));
    }

    [Fact]
    public async Task Stops_listening_when_disposed()
    {
        Render<TopBar>();
        _signals.Catalog.Subscribers.Should().Be(1);

        await DisposeComponentsAsync();

        // The coordinator is a singleton that outlives every circuit; a handler left behind pins
        // the bar (and the page under it) for the life of the app.
        _signals.Catalog.Subscribers.Should().Be(0);
    }
```

- [ ] **Step 4: Run to see them fail**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~TopBarTests"
```
Expected: the four attention tests fail (no class, no title); `Stops_listening_when_disposed` fails with `Subscribers` 0 before dispose.

- [ ] **Step 5: Change TopBar.razor**

Replace the file with:

```razor
@using DiffusionNexus.Installer.Core.Updates
@using DiffusionNexus.Installer.Electron.Services
@inject ICatalogUpdateCoordinator Catalog
@inject UpdaterLog App
@implements IDisposable

<div class="top-bar">
    @* Parenthesised, and it matters: Razor's @ disambiguation treats `v@AppVersion.Display`
       as an EMAIL ADDRESS -- non-space, @, dotted text -- and emits it verbatim, so the bar
       literally read "v@AppVersion.Display". An explicit expression removes the ambiguity. *@
    <span class="top-bar-version">v@(AppVersion.Display)</span>

    <div class="top-bar-actions">
        <button class="top-bar-btn top-bar-feedback" type="button" @onclick="OnFeedback">Feedback</button>
        <a class="top-bar-btn" href="/licenses">Licences</a>
        @if (ShowDeveloperTools)
        {
            <a class="top-bar-btn top-bar-dev" href="/debug">Developer tools</a>
        }
        @* A dot, not a count or a word: the bar is on every screen and must not shout. The title
           says what is waiting for whoever hovers. *@
        <a class="top-bar-btn @(Attention is null ? null : "top-bar-attention")" href="/updates" title="@Attention">Check for Updates</a>
    </div>
</div>

@code {
    /// <summary>
    /// Raised when the user asks to send feedback. A callback rather than a route: the dialog is
    /// owned by the hosting page, which knows what context to attach.
    /// </summary>
    [Parameter] public EventCallback OnFeedback { get; set; }

    /// <summary>
    /// The developer tools page is compiled out of Release builds entirely (see the csproj), so
    /// the link has to disappear with it. A #if in Razor MARKUP is not a thing -- preprocessor
    /// directives are only legal inside the @code block, so the flag lives here.
    /// </summary>
    private const bool ShowDeveloperTools =
#if DEBUG
        true;
#else
        false;
#endif

    private string? Attention => (Catalog.UpdateAvailable, App.UpdateReady) switch
    {
        (true, true) => "Catalog update available and app update ready",
        (true, false) => "Catalog update available",
        (false, true) => "App update ready",
        _ => null,
    };

    protected override void OnInitialized()
    {
        Catalog.Changed += OnChanged;
        App.Changed += OnChanged;
    }

    // Both fire off the circuit (pool thread, Electron socket thread), so hop back before rendering.
    private void OnChanged() => _ = InvokeAsync(StateHasChanged);

    // Method group, so -= removes the handler this component actually added.
    public void Dispose()
    {
        Catalog.Changed -= OnChanged;
        App.Changed -= OnChanged;
    }
}
```

Blazor renders `title="@Attention"` as no attribute at all when the value is null, which is what `HasAttribute("title").Should().BeFalse()` checks.

- [ ] **Step 6: Add the CSS**

In `app.css`, directly after the `.top-bar-dev { … }` rule:

```css
/* Something is waiting on /updates: a catalog update to apply or an app update to install. */
.top-bar-attention::after {
    content: "";
    display: inline-block;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--accent);
}
```

- [ ] **Step 7: Run the whole suite**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj
```
Expected: green, including `StylesheetTests` (which guards brace balance) and every fixture touched in Step 2. A failure of the form `Cannot provide a value for property 'Catalog'` names a fixture that still lacks `UpdateSignals.Register(Services)`.

- [ ] **Step 8: Commit**

```powershell
git add DiffusionNexus.Installer.Electron/Components/Shared/TopBar.razor DiffusionNexus.Installer.Electron/wwwroot/app.css DiffusionNexus.Installer.Tests
git commit -m "feat(installer): top bar marks Check for Updates when a catalog or app update is waiting" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 8: Welcome page notice

**Files:**
- Modify: `DiffusionNexus.Installer.Electron/Components/Pages/Welcome.razor` (directives at the top, markup after `<p class="welcome-subtitle">`, `@code` block, `DisposeAsync`)
- Modify: `DiffusionNexus.Installer.Electron/wwwroot/app.css` (after `.welcome-subtitle { … }`, line ~1190)
- Test: `DiffusionNexus.Installer.Tests/Components/WelcomePageTests.cs`

**Interfaces:**
- Consumes: `ICatalogUpdateCoordinator` (`UpdateAvailable`, `LastCheck`, `Changed`), `CatalogUpdateOutcome.RequiresNewerSoftware`, `_signals` field from Task 7.

- [ ] **Step 1: Write the failing tests**

Append to `WelcomePageTests.cs` inside the class (add `using DiffusionNexus.Installer.Core.Updates;`, `using DiffusionNexus.Installer.SDK.Catalog.Updates;`, `using DiffusionNexus.Installer.Tests.Support;`):

```csharp
    private static string Notice => ".catalog-update-notice";

    [Fact]
    public void Announces_a_waiting_catalog_update_with_its_counts()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"));
        _signals.Catalog.LastCheck = CatalogChecks.Available(
            workloads: [CatalogChecks.WorkloadUpdated("A", "V1.0", "V1.1"), CatalogChecks.WorkloadAdded("B", "V1.0"), CatalogChecks.WorkloadRemoved("C", "V1.0")],
            workflows: [CatalogChecks.WorkflowAdded("W", "V1.0", "A")]);
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() =>
        {
            var notice = cut.Find(Notice);
            notice.TextContent.Should().Contain("A catalog update is available: 3 workloads and 1 workflow changed.");
            notice.QuerySelector("a[href='/updates']")!.TextContent.Should().Be("Review and apply");
        });
    }

    [Fact]
    public void Points_at_the_app_update_when_the_catalog_needs_newer_software()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"));
        _signals.Catalog.LastCheck = CatalogChecks.Outcome(CatalogUpdateOutcome.RequiresNewerSoftware);
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() =>
            cut.Find(Notice).TextContent.Should().Contain("Update the installer to receive it."));
    }

    [Theory]
    [InlineData(CatalogUpdateOutcome.UpToDate)]
    [InlineData(CatalogUpdateOutcome.Failed)]
    [InlineData(CatalogUpdateOutcome.OverrideActive)]
    public void Says_nothing_for_other_outcomes(CatalogUpdateOutcome outcome)
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"));
        _signals.Catalog.LastCheck = CatalogChecks.Outcome(outcome, "boom");
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().NotBeEmpty());
        cut.FindAll(Notice).Should().BeEmpty();
    }

    [Fact]
    public void Appears_when_the_startup_check_finishes_after_the_page_rendered()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"));
        var cut = Render<Welcome>();
        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().NotBeEmpty());
        cut.FindAll(Notice).Should().BeEmpty();

        _signals.Catalog.LastCheck = CatalogChecks.Available();
        _signals.Catalog.Phase = CatalogUpdatePhase.Checked;
        _signals.Catalog.RaiseChanged();

        cut.WaitForAssertion(() => cut.Find(Notice).TextContent.Should().Contain("1 workload and 0 workflows changed"));
    }

    [Fact]
    public async Task Unsubscribes_from_the_coordinator_on_dispose()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"));
        var cut = Render<Welcome>();
        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().NotBeEmpty());
        var before = _signals.Catalog.Subscribers;   // page + its TopBar

        await DisposeComponentsAsync();

        _signals.Catalog.Subscribers.Should().Be(0, $"{before} handlers were attached and all must go");
    }
```

- [ ] **Step 2: Run to see them fail**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~WelcomePageTests"
```
Expected: the notice tests fail with "no elements matching .catalog-update-notice"; `Says_nothing_for_other_outcomes` passes already; the dispose test fails only if the page has not subscribed yet (it will, after Step 3).

- [ ] **Step 3: Edit Welcome.razor**

Directives: add after `@using DiffusionNexus.Installer.SDK.Catalog`:
```razor
@using DiffusionNexus.Installer.Core.Updates
@using DiffusionNexus.Installer.SDK.Catalog.Updates
@inject ICatalogUpdateCoordinator Catalog
```

Markup: directly after `<p class="welcome-subtitle">Choose which AI application you want to install.</p>` insert:
```razor
        @* Above the strip and below the title, so it reads as news about the list, not as an
           error about it. Only the two outcomes the user can act on get a line; a failed
           startup check stays quiet here -- /updates says why if they go looking. *@
        @if (Catalog.UpdateAvailable && Catalog.LastCheck is { } available)
        {
            <p class="catalog-update-notice">
                A catalog update is available: @Plural(available.Workloads.Count, "workload") and @Plural(available.Workflows.Count, "workflow") changed.
                <a href="/updates">Review and apply</a>
            </p>
        }
        else if (Catalog.LastCheck?.Outcome == CatalogUpdateOutcome.RequiresNewerSoftware)
        {
            <p class="catalog-update-notice">
                The catalog has moved to a format this version cannot read. Update the installer to receive it.
                <a href="/updates">Check for updates</a>
            </p>
        }
```

`@code`: add these members, and in `OnInitializedAsync` add `Catalog.Changed += OnCatalogChanged;` as the first statement inside the method (before the `try`):
```csharp
    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // Fires on a pool thread when the startup check lands; hop back onto the circuit.
    private void OnCatalogChanged() => _ = InvokeAsync(StateHasChanged);
```

`DisposeAsync`: add `Catalog.Changed -= OnCatalogChanged;` as its first statement, before `_disposed = true;` (the method already exists; find `public async ValueTask DisposeAsync()`).

- [ ] **Step 4: Add the CSS**

In `app.css`, after the `.welcome-subtitle { … }` rule:

```css
/* News about the list, not an error about it: accent border, no red. */
.catalog-update-notice {
    margin: 0 auto 1rem;
    padding: .6rem 1rem;
    max-width: 48rem;
    border: 1px solid var(--accent);
    border-radius: 8px;
    background: var(--panel);
    font-size: .9rem;
    text-align: center;
}

.catalog-update-notice a {
    color: var(--accent);
    font-weight: 600;
    margin-left: .4rem;
}
```

- [ ] **Step 5: Run to see them pass, then the whole suite**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~WelcomePageTests"
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj
```
Expected: green. `WelcomeScriptFailureTests` renders the same page and already registers the signals (Task 7).

- [ ] **Step 6: Commit**

```powershell
git add DiffusionNexus.Installer.Electron/Components/Pages/Welcome.razor DiffusionNexus.Installer.Electron/wwwroot/app.css DiffusionNexus.Installer.Tests/Components/WelcomePageTests.cs
git commit -m "feat(installer): welcome screen announces a waiting catalog update" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 9: `/updates` — Content catalog section and combined check button

**Files:**
- Modify: `DiffusionNexus.Installer.Electron/Components/Pages/Home.razor`
- Modify: `DiffusionNexus.Installer.Electron/wwwroot/app.css` (after `.shell .log h2 { … }`, line ~122)
- Test: `DiffusionNexus.Installer.Tests/Components/UpdatesPageTests.cs`

**Interfaces:**
- Consumes: `ICatalogUpdateCoordinator` (everything), `CatalogChangeRows.Build`, `CatalogChannelSource`, `CatalogUpdatePhase`, SDK `CatalogSections`, `CatalogApplyResult`, `CatalogDownloadProgress`, `LocalCatalogState`, `SectionState`.

- [ ] **Step 1: Register the stub in the existing fixture**

In `UpdatesPageTests.cs`, add `using DiffusionNexus.Installer.Core.Updates;`, `using DiffusionNexus.Installer.SDK.Catalog.Packaging;`, `using DiffusionNexus.Installer.SDK.Catalog.Updates;`, `using DiffusionNexus.Installer.Tests.Support;`. Add the field `private readonly StubCatalogUpdateCoordinator _catalog = new();` and, inside `Register(...)`, after `Services.AddSingleton(log);`, add `Services.AddSingleton<ICatalogUpdateCoordinator>(_catalog);`. Existing tests must keep passing.

- [ ] **Step 2: Write the failing tests**

Append inside the class:

```csharp
    private static string Apply => "Apply catalog update";

    private void Available(params WorkloadChange[] workloads)
    {
        _catalog.LastCheck = CatalogChecks.Available(4, CatalogChannel.Preview, workloads.Length == 0 ? null : workloads);
        _catalog.Phase = CatalogUpdatePhase.Checked;
    }

    [Fact]
    public void Says_not_checked_yet_before_any_check()
    {
        Register(InstallPhase.Idle);

        var page = Render<UpdatesPage>();

        page.Find(".catalog-outcome").TextContent.Trim().Should().Be("Not checked yet.");
        page.Find(".catalog-channel").TextContent.Should().Contain("Stable");
        page.Find(".catalog-installed").TextContent.Should().Contain("unknown");
    }

    [Fact]
    public void Describes_what_is_installed()
    {
        Register(InstallPhase.Idle);
        _catalog.Installed = new LocalCatalogState
        {
            Channel = CatalogChannel.Stable,
            Workloads = new SectionState(3, "abc", new DateTimeOffset(2026, 9, 15, 19, 0, 0, TimeSpan.Zero)),
            Workflows = new SectionState(3, "abc", new DateTimeOffset(2026, 9, 15, 19, 0, 0, TimeSpan.Zero)),
        };

        Render<UpdatesPage>().Find(".catalog-installed").TextContent.Should().Contain("v3 (Stable), applied 2026-09-15");
    }

    [Fact]
    public void Names_the_environment_variable_when_it_pins_the_channel()
    {
        Register(InstallPhase.Idle);
        _catalog.Channel = CatalogChannel.Preview;
        _catalog.ChannelSource = CatalogChannelSource.Environment;

        Render<UpdatesPage>().Find(".catalog-channel").TextContent.Should().Contain("Preview (set by DIFFUSIONNEXUS_CATALOG_CHANNEL)");
    }

    [Fact]
    public void Lists_the_changes_and_offers_apply_when_an_update_is_available()
    {
        Register(InstallPhase.Idle);
        Available(CatalogChecks.WorkloadUpdated("Krea-2-Turbo", "V1.0", "V1.1"), CatalogChecks.WorkloadAdded("Ernie", "V1.0"));

        var page = Render<UpdatesPage>();

        page.Find(".catalog-outcome").TextContent.Should().Contain("Catalog v4 is available on Preview.");
        page.FindAll(".catalog-changes h3").Select(h => h.TextContent).Should().Equal("Added", "Updated");
        page.Markup.Should().Contain("Krea-2-Turbo").And.Contain("V1.0 → V1.1").And.Contain("Ernie");
        var button = page.FindAll("button").Single(b => b.TextContent.Trim() == Apply);
        button.HasAttribute("disabled").Should().BeFalse();

        button.Click();

        _catalog.Applies.Should().Be(1);
    }

    [Fact]
    public void Withholds_apply_while_an_install_runs_and_says_why()
    {
        Register(InstallPhase.Running);
        Available();
        _catalog.ApplyBlockedReason = "It can be applied once Krea-2-Turbo has finished.";

        var page = Render<UpdatesPage>();

        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == Apply);
        page.Markup.Should().Contain("It can be applied once Krea-2-Turbo has finished.");
    }

    [Theory]
    [InlineData(50L, 100L, "Downloading… 50%")]
    [InlineData(3_250_000L, null, "Downloading… 3.1 MB")]
    public void Shows_progress_while_applying(long received, long? total, string expected)
    {
        Register(InstallPhase.Idle);
        Available();
        _catalog.Phase = CatalogUpdatePhase.Applying;
        _catalog.Progress = new CatalogDownloadProgress(received, total);

        var page = Render<UpdatesPage>();

        page.Find(".catalog-progress").TextContent.Trim().Should().Be(expected);
        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == Apply);
    }

    [Fact]
    public void Reports_the_new_version_after_a_successful_apply()
    {
        Register(InstallPhase.Idle);
        Available();
        _catalog.Phase = CatalogUpdatePhase.Applied;
        _catalog.LastApply = new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);

        var page = Render<UpdatesPage>();

        page.Find(".catalog-outcome").TextContent.Should().Contain("Catalog updated to v4.");
        page.Find(".catalog-outcome a[href='/']").TextContent.Should().Be("Back to all software");
        page.FindAll(".catalog-changes").Should().BeEmpty("what changed is now what is installed");
    }

    [Fact]
    public void Reports_a_failed_apply_and_offers_a_retry()
    {
        Register(InstallPhase.Idle);
        Available();
        _catalog.LastApply = new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "sha256 mismatch");

        var page = Render<UpdatesPage>();

        page.Find(".catalog-error").TextContent.Should().Contain("The catalog update failed: sha256 mismatch Nothing was changed.");
        page.FindAll("button").Should().Contain(b => b.TextContent.Trim() == Apply);
    }

    [Fact]
    public void Names_the_section_that_did_land_on_a_partial_apply()
    {
        Register(InstallPhase.Idle);
        Available();
        _catalog.LastApply = new CatalogApplyResult(CatalogSections.Workloads, CatalogSections.Workflows, "workflows/ locked");

        Render<UpdatesPage>().Find(".catalog-error").TextContent.Should().Contain("Workloads were updated; workflows failed: workflows/ locked");
    }

    [Theory]
    [InlineData(CatalogUpdateOutcome.UpToDate, null, "The catalog is up to date.")]
    [InlineData(CatalogUpdateOutcome.RequiresNewerSoftware, null, "This catalog update needs a newer version of the installer. Install the app update above first.")]
    [InlineData(CatalogUpdateOutcome.Failed, "HTTP 503", "The catalog check failed: HTTP 503")]
    public void Each_outcome_has_its_own_line(CatalogUpdateOutcome outcome, string? error, string expected)
    {
        Register(InstallPhase.Idle);
        _catalog.LastCheck = CatalogChecks.Outcome(outcome, error);
        _catalog.Phase = CatalogUpdatePhase.Checked;

        Render<UpdatesPage>().Find(".catalog-outcome").TextContent.Trim().Should().Be(expected);
    }

    [Fact]
    public void An_active_override_names_its_path()
    {
        Register(InstallPhase.Idle);
        _catalog.LastCheck = CatalogChecks.Outcome(CatalogUpdateOutcome.OverrideActive);
        _catalog.Phase = CatalogUpdatePhase.Checked;
        _catalog.OverridePath = @"E:\Repos\DiffusionNexus.Catalog";

        Render<UpdatesPage>().Find(".catalog-outcome").TextContent.Should()
            .Contain(@"a local catalog override is active at E:\Repos\DiffusionNexus.Catalog");
    }

    [Fact]
    public void Says_checking_while_a_check_runs()
    {
        Register(InstallPhase.Idle);
        _catalog.Phase = CatalogUpdatePhase.Checking;

        Render<UpdatesPage>().Find(".catalog-outcome").TextContent.Trim().Should().Be("Checking the catalog…");
    }

    [Fact]
    public void The_check_button_runs_the_catalog_check_even_outside_electron()
    {
        // ElectronHost.IsActive is false under test, which used to disable the button outright.
        // The catalog check has no Electron dependency, so the button now always works for it.
        Register(InstallPhase.Idle);
        var page = Render<UpdatesPage>();
        var button = page.FindAll("button").Single(b => b.TextContent.Trim() == "Check for updates");
        button.HasAttribute("disabled").Should().BeFalse();

        button.Click();

        _catalog.Checks.Should().Be(1);
    }

    [Fact]
    public void Re_renders_when_the_coordinator_changes()
    {
        Register(InstallPhase.Idle);
        var page = Render<UpdatesPage>();
        page.Find(".catalog-outcome").TextContent.Trim().Should().Be("Not checked yet.");

        Available();
        _catalog.RaiseChanged();

        page.WaitForAssertion(() => page.Find(".catalog-outcome").TextContent.Should().Contain("Catalog v4 is available"));
    }

    [Fact]
    public async Task Stops_listening_to_the_coordinator_when_disposed()
    {
        Register(InstallPhase.Idle);
        Render<UpdatesPage>();
        _catalog.Subscribers.Should().Be(1);

        await DisposeComponentsAsync();

        _catalog.Subscribers.Should().Be(0);
    }
```

- [ ] **Step 3: Run to see them fail**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~UpdatesPageTests"
```
Expected: the four pre-existing tests pass; every new one fails on a missing element.

- [ ] **Step 4: Edit Home.razor — directives and the check button**

Directives: after `@inject IInstallSession Session` add
```razor
@using System.Globalization
@using DiffusionNexus.Installer.Core.Updates
@using DiffusionNexus.Installer.SDK.Catalog.Updates
@inject ICatalogUpdateCoordinator Catalog
```

Header: change `<p class="host">@HostDescription</p>` to read the app updater only:
```razor
        <p class="host">@HostDescription</p>
```
and change `HostDescription`'s second string to `"Running as a plain web app - the app updater is unavailable outside Electron; the catalog check still works."`.

Button: replace `disabled="@(_busy || !ElectronActive)"` with `disabled="@_busy"`.

Replace `CheckForUpdatesAsync` with:
```csharp
    private async Task CheckForUpdatesAsync()
    {
        _busy = true;
        try
        {
            // Both checks from one button. The catalog one never throws and needs no Electron;
            // the app one is guarded exactly as before.
            var catalog = Catalog.CheckAsync();
            if (ElectronActive)
            {
                try
                {
                    await Electron.AutoUpdater.CheckForUpdatesAsync();
                }
                catch (Exception ex)
                {
                    // A failed update check must never take the app down with it - the installer
                    // still works perfectly well on an older version.
                    Log.Append($"Update check failed: {ex.Message}");
                }
            }
            await catalog;
        }
        finally
        {
            _busy = false;
        }
    }
```

Subscription: in `OnInitialized` add `Catalog.Changed += OnLogChanged;` after the `Session.Changed += OnLogChanged;` line; in `Dispose` add `Catalog.Changed -= OnLogChanged;` after `Session.Changed -= OnLogChanged;`.

- [ ] **Step 5: Edit Home.razor — the section**

Insert between the closing `</section>` of `class="actions"` and `<section class="log">`:

```razor
    <section class="catalog">
        <h2>Content catalog</h2>
        <p class="catalog-channel">Following: <strong>@Catalog.Channel</strong>@(Catalog.ChannelSource == CatalogChannelSource.Environment ? " (set by DIFFUSIONNEXUS_CATALOG_CHANNEL)" : null)</p>
        <p class="catalog-installed">Installed: @InstalledText</p>
        <p class="catalog-outcome">
            @OutcomeText
            @if (Catalog.Phase == CatalogUpdatePhase.Applied)
            {
                <a href="/">Back to all software</a>
            }
        </p>

        @if (Catalog.UpdateAvailable && Catalog.LastCheck is { } available)
        {
            var rows = CatalogChangeRows.Build(available);
            foreach (var group in new[] { ChangeKind.Added, ChangeKind.Updated, ChangeKind.Removed })
            {
                var inGroup = rows.Where(r => r.Change == group).ToList();
                if (inGroup.Count == 0) continue;
                <div class="catalog-changes">
                    <h3>@group</h3>
                    <ul>
                        @foreach (var row in inGroup)
                        {
                            <li>
                                <span class="catalog-change-kind">@row.Kind</span>
                                <span class="catalog-change-name">@row.Name</span>
                                <span class="catalog-change-version">@row.VersionText</span>
                            </li>
                        }
                    </ul>
                </div>
            }

            @if (Catalog.Phase == CatalogUpdatePhase.Applying)
            {
                <p class="catalog-progress">@ProgressText</p>
            }
            else if (Catalog.ApplyBlockedReason is { } blocked)
            {
                @* Same rule as "Restart and install" above: never swap content under a running plan. *@
                <p class="panel-hint">@blocked</p>
            }
            else
            {
                <button class="primary" @onclick="ApplyCatalogAsync" disabled="@(!Catalog.CanApply)">Apply catalog update</button>
            }

            @if (Catalog.LastApply is { } failed && (failed.Error is not null || failed.Failed != CatalogSections.None))
            {
                <p class="catalog-error">@ApplyFailureText(failed)</p>
            }
        }
    </section>
```

- [ ] **Step 6: Edit Home.razor — the presenters**

Add to `@code`:

```csharp
    private string InstalledText => Catalog.Installed switch
    {
        null => "unknown",
        { HighestCatalogVersion: 0 } => "not yet installed",
        var s => $"v{s.HighestCatalogVersion} ({s.Channel}), applied {(s.Workloads?.AppliedAt ?? s.Workflows?.AppliedAt)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
    };

    private string OutcomeText
    {
        get
        {
            if (Catalog.Phase == CatalogUpdatePhase.Checking) return "Checking the catalog…";
            if (Catalog.Phase == CatalogUpdatePhase.Applied) return $"Catalog updated to v{Catalog.LastCheck?.Remote?.CatalogVersion}.";
            return Catalog.LastCheck switch
            {
                null => "Not checked yet.",
                { Outcome: CatalogUpdateOutcome.UpToDate } => "The catalog is up to date.",
                { Outcome: CatalogUpdateOutcome.UpdatesAvailable } c => $"Catalog v{c.Remote?.CatalogVersion} is available on {c.Channel}.",
                { Outcome: CatalogUpdateOutcome.RequiresNewerSoftware } => "This catalog update needs a newer version of the installer. Install the app update above first.",
                { Outcome: CatalogUpdateOutcome.OverrideActive } => $"Update check skipped: a local catalog override is active at {Catalog.OverridePath}.",
                { Outcome: CatalogUpdateOutcome.Failed } c => $"The catalog check failed: {c.Error}",
                _ => string.Empty,
            };
        }
    }

    private string ProgressText => Catalog.Progress switch
    {
        null => "Downloading…",
        { TotalBytes: > 0 } p => $"Downloading… {(100.0 * p.BytesReceived / p.TotalBytes.Value).ToString("F0", CultureInfo.InvariantCulture)}%",
        var p => $"Downloading… {(p.BytesReceived / 1_048_576.0).ToString("F1", CultureInfo.InvariantCulture)} MB",
    };

    private static string ApplyFailureText(CatalogApplyResult result) => result.Applied switch
    {
        CatalogSections.None => $"The catalog update failed: {result.Error} Nothing was changed.",
        CatalogSections.Workloads => $"Workloads were updated; workflows failed: {result.Error}",
        CatalogSections.Workflows => $"Workflows were updated; workloads failed: {result.Error}",
        _ => $"The catalog update reported a problem: {result.Error}",
    };

    // Never throws; a refused click (state moved on) is a no-op the next render explains.
    private Task ApplyCatalogAsync() => Catalog.ApplyAsync();
```

- [ ] **Step 7: Add the CSS**

In `app.css`, after `.shell .log h2 { … }`:

```css
/* /updates: the content-catalog section under the app updater. */
.shell .catalog h2 {
    margin: 1.5rem 0 .5rem;
}

.shell .catalog p {
    margin: .25rem 0;
}

.catalog-changes {
    margin: .75rem 0;
}

.catalog-changes h3 {
    margin: .5rem 0 .25rem;
    font-size: .9rem;
    color: var(--muted);
}

.catalog-changes ul {
    list-style: none;
    padding: 0;
    margin: 0;
}

.catalog-changes li {
    display: flex;
    gap: .6rem;
    padding: .25rem 0;
    border-bottom: 1px solid var(--border);
    font-size: .9rem;
}

.catalog-change-kind {
    color: var(--muted);
    min-width: 5.5rem;
}

.catalog-change-version {
    margin-left: auto;
    font-family: Consolas, "Courier New", monospace;
    color: var(--muted);
}

.catalog-error {
    color: #e07070;
}
```

- [ ] **Step 8: Run to see them pass, then the whole suite**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~UpdatesPageTests"
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj
```
Expected: green. If `Lists_the_changes…` fails on the `h3` order, the `foreach` over the fixed `ChangeKind` array is what guarantees Added before Updated; check it was not reordered.

- [ ] **Step 9: Commit**

```powershell
git add DiffusionNexus.Installer.Electron/Components/Pages/Home.razor DiffusionNexus.Installer.Electron/wwwroot/app.css DiffusionNexus.Installer.Tests/Components/UpdatesPageTests.cs
git commit -m "feat(installer): /updates shows the content catalog -- channel, changes, apply with progress" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 10: Developer tools — channel picker (Debug builds)

**Files:**
- Modify: `DiffusionNexus.Installer.Electron/Components/Pages/DebugTools.razor` (directives, a new `<section>` before the export panel, `@code`)
- Test: `DiffusionNexus.Installer.Tests/Components/DebugToolsPageTests.cs` (new, whole file inside `#if DEBUG`)

**Interfaces:**
- Consumes: `ICatalogUpdateCoordinator.Channel / ChannelSource / SetChannelAsync / CheckAsync`, `NavigationManager` (already injected as `Nav`), `IWorkloadSource`, `IFolderPicker`, `LauncherScriptPreview` (parameterless), `StubCatalogUpdateCoordinator`.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Installer.Tests/Components/DebugToolsPageTests.cs`:

```csharp
#if DEBUG
using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.DevTools;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The page is compiled out of Release builds (csproj), so this file goes with it. The channel
/// panel is the only way to switch channel without an environment variable, and a Debug run
/// that forgot the saved preference on every start would make Preview useless for the author.
/// </summary>
public class DebugToolsPageTests : BunitContext
{
    private readonly StubCatalogUpdateCoordinator _catalog = new();

    public DebugToolsPageTests()
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<InstallationConfiguration>());
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());
        Services.AddSingleton(source.Object);
        Services.AddSingleton(Mock.Of<IFolderPicker>());
        Services.AddSingleton(new LauncherScriptPreview());
        Services.AddSingleton<ICatalogUpdateCoordinator>(_catalog);
    }

    private static string Radio(CatalogChannel channel) => $"input[name='catalog-channel'][value='{channel}']";

    [Fact]
    public void Shows_the_channel_the_coordinator_follows()
    {
        _catalog.Channel = CatalogChannel.Preview;

        var cut = Render<DebugTools>();

        cut.Find(Radio(CatalogChannel.Preview)).HasAttribute("checked").Should().BeTrue();
        cut.Find(Radio(CatalogChannel.Stable)).HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public void Picking_a_channel_saves_it_through_the_coordinator()
    {
        var cut = Render<DebugTools>();

        cut.Find(Radio(CatalogChannel.Preview)).Change("Preview");

        _catalog.ChannelSet.Should().Be(CatalogChannel.Preview);
        cut.WaitForAssertion(() => cut.Find(Radio(CatalogChannel.Preview)).HasAttribute("checked").Should().BeTrue());
    }

    [Fact]
    public void Disables_the_radios_when_the_environment_pins_the_channel()
    {
        _catalog.Channel = CatalogChannel.Preview;
        _catalog.ChannelSource = CatalogChannelSource.Environment;

        var cut = Render<DebugTools>();

        cut.Find(Radio(CatalogChannel.Preview)).HasAttribute("disabled").Should().BeTrue();
        cut.Find(Radio(CatalogChannel.Stable)).HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("Set by DIFFUSIONNEXUS_CATALOG_CHANNEL for this run");
    }

    [Fact]
    public void Check_now_starts_a_check_and_goes_to_the_updates_page()
    {
        var cut = Render<DebugTools>();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Check now").Click();

        _catalog.Checks.Should().Be(1);
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/updates");
    }
}
#endif
```

- [ ] **Step 2: Run to see them fail**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~DebugToolsPageTests"
```
Expected: all four fail on missing `input[name='catalog-channel']` elements (the page does not render the panel yet). Run in Debug, the default for `dotnet test`.

- [ ] **Step 3: Edit DebugTools.razor**

Directives: after `@inject NavigationManager Nav` add
```razor
@using DiffusionNexus.Installer.Core.Updates
@using DiffusionNexus.Installer.SDK.Catalog.Packaging
@inject ICatalogUpdateCoordinator Catalog
```

Markup: directly after `<p class="panel-hint">Debug builds only. Not present in a Release build.</p>` insert:

```razor
<section class="panel">
    <h2>Catalog channel</h2>
    <p class="panel-hint">
        Preview follows every push to the catalog repo's main branch; Stable follows tagged
        releases, which is what users get. Saved per user. A Release build switches only through
        the DIFFUSIONNEXUS_CATALOG_CHANNEL environment variable.
    </p>

    @foreach (var channel in new[] { CatalogChannel.Stable, CatalogChannel.Preview })
    {
        <label class="catalog-channel-option">
            <input type="radio" name="catalog-channel" value="@channel"
                   checked="@(Catalog.Channel == channel)"
                   disabled="@EnvironmentPinned"
                   @onchange="() => SetChannelAsync(channel)" />
            @channel
        </label>
    }

    @if (EnvironmentPinned)
    {
        <p class="panel-hint">Set by DIFFUSIONNEXUS_CATALOG_CHANNEL for this run; the saved setting is not in effect.</p>
    }

    <div class="wizard-actions">
        <button class="btn-secondary" @onclick="CheckNow">Check now</button>
    </div>

    @if (_channelError is not null)
    {
        <p class="validation-error">@_channelError</p>
    }
</section>
```

`@code`: add

```csharp
    private string? _channelError;

    private bool EnvironmentPinned => Catalog.ChannelSource == CatalogChannelSource.Environment;

    private async Task SetChannelAsync(CatalogChannel channel)
    {
        _channelError = null;
        try
        {
            await Catalog.SetChannelAsync(channel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _channelError = $"The channel could not be saved: {ex.Message}";
        }
    }

    private void CheckNow()
    {
        // Fire and go: /updates shows "Checking the catalog…" and then the outcome.
        _ = Catalog.CheckAsync();
        Nav.NavigateTo("/updates");
    }
```

- [ ] **Step 4: Add the CSS**

In `app.css`, after the `.panel-hint { … }` rule:

```css
.catalog-channel-option {
    display: inline-flex;
    align-items: center;
    gap: .4rem;
    margin: 0 1.25rem .75rem 0;
    cursor: pointer;
}
```

- [ ] **Step 5: Run to see them pass, then the whole suite in both configurations**

```powershell
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj --filter "FullyQualifiedName~DebugToolsPageTests"
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj -c Release
```
Expected: green in Debug; in Release the `DebugToolsPageTests` class is absent and everything else is green (this is the check that the `#if DEBUG` wrapper is complete).

- [ ] **Step 6: Commit**

```powershell
git add DiffusionNexus.Installer.Electron/Components/Pages/DebugTools.razor DiffusionNexus.Installer.Electron/wwwroot/app.css DiffusionNexus.Installer.Tests/Components/DebugToolsPageTests.cs
git commit -m "feat(installer): Developer tools can switch the catalog channel (Debug builds)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 11: Manual smoke checklist

**Files:**
- Modify: `docs/manual-smoke.md` (append after §6)

- [ ] **Step 1: Append §7**

```markdown
## 7. Catalog updates and the Preview channel

The catalog editor (Tools repo) has two publish actions. **Preview** commits and pushes to
`main`; the catalog repo's CI repoints the `preview` pre-release within about a minute.
**Release** tags `vN`; CI creates the stable release GitHub serves as "Latest" (the redirect
can lag a minute after the release appears). Stable is what users follow.

1. In the editor, change one workload's description and press **Preview**. Wait until
   `https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases/tag/preview` shows the
   new commit hash in its title.
2. Launch a Debug build with `DIFFUSIONNEXUS_CATALOG_CHANNEL=preview` set for the process.
   **Expect:** within a few seconds the top bar's "Check for Updates" grows a dot (hover: "Catalog
   update available") and the welcome screen shows "A catalog update is available: 1 workload and
   0 workflows changed. Review and apply".
3. Open `/updates`. **Expect:** "Following: Preview (set by DIFFUSIONNEXUS_CATALOG_CHANNEL)",
   "Installed: vN (Stable), applied <date>", "Catalog vN+1 is available on Preview.", one
   Updated row naming the workload you edited with its version text — the same row the editor's
   Release dialog would show — and an **Apply catalog update** button.
4. Press Apply. **Expect:** "Downloading… NN%" ticking, then "Catalog updated to vN+1." with a
   "Back to all software" link; the dot and the welcome notice are gone. Follow the link and open
   the workload. **Expect:** the edited description.
5. Quit. Launch again **without** the variable. **Expect:** `/updates` says "Following: Stable",
   the installed line still says vN+1 (Preview) — provenance, not preference — and the check
   says "The catalog is up to date." or offers the stable content back if it differs (the diff is
   hash-based; a Preview client returning to Stable is simply offered what Stable has).
6. In a Debug build open Developer tools. **Expect:** a Catalog channel panel with Stable and
   Preview radios; the saved one is checked. Pick the other, press **Check now**. **Expect:**
   `/updates` opens and shows the new channel. Quit and relaunch. **Expect:** the choice stuck.
   With the environment variable set, the radios are disabled and the hint says so.
7. Editor: press **Release**, confirm. About a minute later launch on Stable. **Expect:** the
   same update offered and applied.
8. Start an install of any workload, then open `/updates` while it runs with an update pending.
   **Expect:** no Apply button, instead "It can be applied once <workload> has finished." Let the
   install finish. **Expect:** the button appears without leaving the page.
9. Disconnect the network and press Check for updates. **Expect:** "The catalog check failed:
   …" on `/updates`, nothing on the welcome screen, no dot.
10. Run with `DIFFUSIONNEXUS_CATALOG_PATH` pointing at a catalog checkout. **Expect:** "Update
    check skipped: a local catalog override is active at <path>." and no Apply button.
```

- [ ] **Step 2: Commit**

```powershell
git add docs/manual-smoke.md
git commit -m "docs(smoke): catalog update check and Preview channel walkthrough" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 12: Ship — SDK tag, installer reference bump, PR

Preconditions: Task 1's SDK PR has been reviewed and merged into `develop` by the user. Do not merge it yourself.

**Files:**
- Modify: `DiffusionNexus.Installer.Core/DiffusionNexus.Installer.Core.csproj` (lines 10-13)
- Modify: `DiffusionNexus.Installer.Electron/DiffusionNexus.Installer.Electron.csproj` (lines 92-95)

- [ ] **Step 1: Tag the SDK**

```powershell
cd E:\Repos\DiffusionNexus.Installer.SDK
gh auth switch --user Little-God1983
git checkout develop
git pull --ff-only
git log --oneline -1            # must include the CatalogChannel commit
git tag v2.0.0-preview.8
git push origin v2.0.0-preview.8
gh run list --repo Little-God1983/DiffusionNexus.Installer.SDK --limit 3
```
Wait for the publish workflow to go green, then confirm the package exists:
```powershell
gh api "/users/Little-God1983/packages/nuget/DiffusionNexus.Installer.SDK.Models/versions" --jq '.[].name' | Select-String "preview.8"
```

- [ ] **Step 2: Bump the installer's references**

In both csproj files replace every `Version="2.0.0-preview.7"` with `Version="2.0.0-preview.8"` (eight lines in total).

- [ ] **Step 3: Prove the package build, not only the local-SDK build**

```powershell
cd E:\Repos\DiffusionNexus.Installer
dotnet restore -p:UseLocalSDK=false --force
dotnet build -c Release -p:UseLocalSDK=false
dotnet test DiffusionNexus.Installer.Tests/DiffusionNexus.Installer.Tests.csproj -c Release -p:UseLocalSDK=false
```
Expected: the `[SDK] Using NuGet package references` message, then green. A restore 403 means the wrong `gh`/NuGet credential is active for the `github-littlegod` source; fix that rather than skipping the step (local-SDK builds hide missing package references — that is exactly what this step exists to catch).

- [ ] **Step 4: Line-ending check and push**

```powershell
git add DiffusionNexus.Installer.Core/DiffusionNexus.Installer.Core.csproj DiffusionNexus.Installer.Electron/DiffusionNexus.Installer.Electron.csproj
git commit -m "build: consume SDK 2.0.0-preview.8 (UserSettings.CatalogChannel)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git diff --numstat origin/main...HEAD
git diff -w --numstat origin/main...HEAD
```
Any file whose added/removed counts collapse to near zero under `-w` has flipped its line endings; fix it (`git show origin/main:<path> > <path>` then re-apply the intended edit) before pushing.
```powershell
gh auth switch --user Into-The-Latent
git push -u origin feature/catalog-update-check
```

- [ ] **Step 5: Open the PR**

```powershell
gh pr create --repo Into-The-Latent/DiffusionNexus.Installer --base main --title "Catalog update check, apply, and opt-in Preview channel" --body-file <scratchpad>/pr-body.md
```
Write `pr-body.md` to the session scratchpad (never into the repo) with this content:

```markdown
The installer now calls the SDK's `ICatalogUpdateService`. A tagged catalog **Release** reaches every Stable user; a **Preview** push reaches machines that opted in.

- Channel preference: `UserSettings.CatalogChannel` (SDK 2.0.0-preview.8) with the per-process override `DIFFUSIONNEXUS_CATALOG_CHANNEL`. Stable by default.
- `CatalogUpdateCoordinator` (Core) owns check/apply; one check at startup via a hosted service; apply sends both sections and is refused while an install runs.
- UI: top-bar dot, welcome notice, "Content catalog" section on `/updates`, Debug-only channel picker on Developer tools.
- Spec: `docs/superpowers/specs/2026-09-16-catalog-update-check-design.md`. Plan: `docs/superpowers/plans/2026-09-16-catalog-update-check.md`. Manual smoke: `docs/manual-smoke.md` §7 (not yet run).
- Follow-up: app self-updater Preview channel, #19.

Before merging: run smoke §7; then refresh `Assets/Catalog/{catalog.zip,manifest.json}` from the latest stable tag and cut v3.0.8.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

- [ ] **Step 6: Watch CI**

```powershell
gh pr checks --repo Into-The-Latent/DiffusionNexus.Installer --watch
```
Expected: Restore, notices, Build, Test all green. Report the PR URL and that manual smoke §7 is still owed.
