# Welcome Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the 3.x installer's unstyled workload list with the 2.x welcome screen — branded top bar, banner, title, software selection — plus a second screen that selects the workload, with cards finally showing the artwork the catalog already ships.

**Architecture:** `Gallery.razor` splits into `Welcome.razor` (`/`) and `SoftwareWorkloads.razor` (`/software/{type}`), sharing a `WorkloadCard` component and a catalog-loading base. Software cards are derived from the catalog by a new `SoftwareGalleryBuilder`; a software with one workload routes straight to `/install/{id}`. Workload thumbnails are absolute disk paths, so a minimal `GET /thumbnail/{id:guid}` endpoint streams them over the existing `IWorkloadSource.GetThumbnailAsync`. Feedback is a Blazor dialog over `IFeedbackReportingService`, already present in `SDK.Shared`.

**Tech Stack:** .NET 10, Blazor Server (interactive server render mode), ElectronNET.Core 0.5.2, bUnit 2.8.6, xUnit, FluentAssertions 7, Moq.

**Spec:** `docs/superpowers/specs/2026-09-13-welcome-screen-design.md`

## Global Constraints

- Branch is `feature/welcome-screen`, already created off `main`. Do not create another.
- Build and test in **Release**: `dotnet test -c Release`. The Debug bin of the Electron project is locked while the user runs the app from Visual Studio.
- Before pushing, verify the package-only build: `dotnet build -c Release -p:UseLocalSDK=false`. CI is the gate for package completeness; a local build with the SDK checkout present cannot prove it.
- **Every new CSS rule uses the existing `:root` variables** — `--bg #12161a`, `--panel #1a2026`, `--border #27313a`, `--text #e6edf3`, `--muted #8b98a5`, `--accent #1fb8a6`, `--accent-hover #26d4bf`. Established literals for semantics: error text `#ff8f8f`, warning text `#e8c67a`, text-on-solid-accent `#06231f`. Do not introduce a second palette.
- `wwwroot/app.css` has **no compiler**. A dropped brace silently kills every rule after it while all tests stay green. `StylesheetTests` guards brace balance — never bypass it, and add new rules at the end of the file, never mid-block.
- Preprocessor directives (`#if DEBUG`) are **not legal in Razor markup**. They may only appear inside `@code`. The existing `ShowDeveloperTools` const is the pattern.
- The SDK defines two public `InstallationOptions` types (`Models.Installation` class vs `Services` record). Importing both namespaces is CS0104-ambiguous.
- FluentAssertions 7: `Should().Equal(string, because)` binds to the params overload. Wrap a single expected string as `["x"]`.
- `RepositoryType` values: `ComfyUI`, `A1111`, `Forge`, `AIToolkit`, `Fooocus`, `AceStep`, `None`. `None` is only used by `DiffusionNexusCore` workloads, which `GetInstallerWorkloadsAsync` already excludes.
- Community links, verbatim, hardcoded this slice: `https://www.youtube.com/@IntoTheLatent`, `https://patreon.com/AIKnowledgeCentral`, `https://civitai.com/user/AIknowlege2go`.
- Every commit message ends with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- Tool edits emit LF. Before pushing, compare `git diff --numstat` against `git diff --numstat -w`; if a file shows a large diff under the first and none under the second, it is a CRLF flip — restore it.

---

### Task 1: Brand assets and the software logo map

**Files:**
- Create: `DiffusionNexus.Installer.Electron/wwwroot/img/banner.jpg` (from `DiffusionNexus.Installer.Electron/Banner.png`)
- Create: `DiffusionNexus.Installer.Electron/wwwroot/img/software/*.png` (6 files copied from the 2.x repo)
- Create: `DiffusionNexus.Installer.Core/Gallery/SoftwareBranding.cs`
- Test: `DiffusionNexus.Installer.Tests/Gallery/SoftwareBrandingTests.cs` (create)
- Delete: `DiffusionNexus.Installer.Electron/Banner.png` (moves into wwwroot)

**Interfaces:**
- Produces: `static class SoftwareBranding` with `static string DisplayName(RepositoryType type)` and `static string? LogoPath(RepositoryType type)` (a `wwwroot`-relative URL such as `img/software/comfyui.jpg`, or null when there is no logo).

- [ ] **Step 1: Write the failing test**

```csharp
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Gallery;

/// <summary>
/// Names and logos for the software cards. Keyed by RepositoryType so a card never shows a raw
/// enum name, and null-safe so a software the catalog adds later renders a neutral tile instead
/// of a broken image.
/// </summary>
public class SoftwareBrandingTests
{
    [Theory]
    [InlineData(RepositoryType.ComfyUI, "ComfyUI")]
    [InlineData(RepositoryType.A1111, "Automatic 1111")]
    [InlineData(RepositoryType.Forge, "Forge")]
    [InlineData(RepositoryType.AIToolkit, "AI Toolkit")]
    [InlineData(RepositoryType.Fooocus, "Fooocus")]
    [InlineData(RepositoryType.AceStep, "ACE-Step")]
    public void Gives_every_offerable_software_a_human_name(RepositoryType type, string expected)
    {
        SoftwareBranding.DisplayName(type).Should().Be(expected);
    }

    [Theory]
    [InlineData(RepositoryType.ComfyUI)]
    [InlineData(RepositoryType.A1111)]
    [InlineData(RepositoryType.Forge)]
    [InlineData(RepositoryType.AIToolkit)]
    [InlineData(RepositoryType.Fooocus)]
    [InlineData(RepositoryType.AceStep)]
    public void Gives_every_offerable_software_a_logo_under_wwwroot(RepositoryType type)
    {
        var path = SoftwareBranding.LogoPath(type);

        path.Should().NotBeNull();
        path!.Should().StartWith("img/software/");
        path.Should().NotStartWith("/", "a leading slash breaks the base-href-relative asset URL");
    }

    [Fact]
    public void Falls_back_rather_than_throwing_for_a_software_with_no_artwork()
    {
        // None is never offered today, but a RepositoryType added to the SDK tomorrow must not
        // take the welcome screen down with a KeyNotFoundException.
        SoftwareBranding.LogoPath(RepositoryType.None).Should().BeNull();
        SoftwareBranding.DisplayName(RepositoryType.None).Should().Be("None");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test -c Release --filter FullyQualifiedName~SoftwareBrandingTests`
Expected: FAIL — `SoftwareBranding` does not exist (CS0103).

- [ ] **Step 3: Write the implementation**

Create `DiffusionNexus.Installer.Core/Gallery/SoftwareBranding.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>
/// Display names and logo assets for the software cards on the welcome screen.
///
/// Deliberately total over <see cref="RepositoryType"/> rather than a dictionary lookup that
/// throws: the catalog drives which cards render, so a RepositoryType added to the SDK before its
/// artwork exists must degrade to a neutral tile, not an unhandled exception on the first screen.
/// </summary>
public static class SoftwareBranding
{
    /// <summary>The name shown on the card. Never a raw enum value.</summary>
    public static string DisplayName(RepositoryType type) => type switch
    {
        RepositoryType.ComfyUI => "ComfyUI",
        RepositoryType.A1111 => "Automatic 1111",
        RepositoryType.Forge => "Forge",
        RepositoryType.AIToolkit => "AI Toolkit",
        RepositoryType.Fooocus => "Fooocus",
        RepositoryType.AceStep => "ACE-Step",
        _ => type.ToString()
    };

    /// <summary>
    /// wwwroot-relative URL of the card artwork, or null when none ships for this software.
    /// No leading slash: the app is served from the root but the asset URLs stay base-href
    /// relative, matching how app.css is referenced.
    /// </summary>
    public static string? LogoPath(RepositoryType type) => type switch
    {
        RepositoryType.ComfyUI => "img/software/comfyui.jpg",
        RepositoryType.A1111 => "img/software/automatic1111.jpg",
        RepositoryType.Forge => "img/software/forge.jpg",
        RepositoryType.AIToolkit => "img/software/ai-toolkit.jpg",
        RepositoryType.Fooocus => "img/software/fooocus.jpg",
        RepositoryType.AceStep => "img/software/ace-step.jpg",
        _ => null
    };
}
```

- [ ] **Step 4: Place the image assets**

The banner is 2688×1512 and 3.3 MB — too large to ship as-is. Downscale to 1600 px wide and move it:

```powershell
Add-Type -AssemblyName System.Drawing
$root = "E:\Repos\DiffusionNexus.Installer\DiffusionNexus.Installer.Electron"
New-Item -ItemType Directory -Force "$root\wwwroot\img\software" | Out-Null

$src = [System.Drawing.Image]::FromFile("$root\Banner.png")
$w = 1600; $h = [int]($src.Height * $w / $src.Width)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = 'HighQualityBicubic'
$g.DrawImage($src, 0, 0, $w, $h)
$bmp.Save("$root\wwwroot\img\banner.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $src.Dispose()
Remove-Item "$root\Banner.png"
```

Copy the six logos from the 2.x repo, renaming to the slugs `SoftwareBranding.LogoPath` returns:

```powershell
$from = "E:\Repos\DiffusionNexus.Installers\DiffusionNexus.WizardUI\Assets"
$to   = "E:\Repos\DiffusionNexus.Installer\DiffusionNexus.Installer.Electron\wwwroot\img\software"
Copy-Item "$from\ComfyUI-logo.png"      "$to\comfyui.png"
Copy-Item "$from\Automatic-Logo.png"    "$to\automatic1111.png"
Copy-Item "$from\ForgeUI-Logo.png"      "$to\forge.png"
Copy-Item "$from\AI-Toolkit-Logo.png"   "$to\ai-toolkit.png"
Copy-Item "$from\Fooocus.png"           "$to\fooocus.png"
Copy-Item "$from\ACE-Step.png"          "$to\ace-step.png"
Get-ChildItem $to | Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB)}}
```

Confirm all seven files exist and the banner is now well under 1 MB.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test -c Release --filter FullyQualifiedName~SoftwareBrandingTests`
Expected: PASS — 13 tests.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Installer.Core/Gallery/SoftwareBranding.cs \
        DiffusionNexus.Installer.Tests/Gallery/SoftwareBrandingTests.cs \
        DiffusionNexus.Installer.Electron/wwwroot/img \
        DiffusionNexus.Installer.Electron/Banner.png
git commit -m "$(cat <<'EOF'
feat(welcome): brand assets and the software logo map

Banner moves out of the project root into wwwroot, downscaled from
2688x1512/3.3MB to 1600px wide. Six software logos copied from the 2.x
WizardUI assets. SoftwareBranding is total over RepositoryType so a software
the SDK adds before its artwork exists renders a neutral tile rather than
throwing on the first screen.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Thumbnail endpoint

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Endpoints/ThumbnailEndpoint.cs`
- Modify: `DiffusionNexus.Installer.Electron/Program.cs` (after `app.MapStaticAssets();`, around line 133)
- Test: `DiffusionNexus.Installer.Tests/Host/ThumbnailEndpointTests.cs` (create)

**Interfaces:**
- Consumes: `IWorkloadSource.GetThumbnailAsync(Guid workloadId, CancellationToken ct)` and `IWorkloadSource.GetInstallerWorkloadsAsync(CancellationToken ct)`, both already on the interface.
- Produces: `static class ThumbnailEndpoint` with `static IEndpointRouteBuilder MapWorkloadThumbnails(this IEndpointRouteBuilder app)`, and `static string ContentTypeFor(string? thumbnailPath)`. Route: `GET /thumbnail/{workloadId:guid}`. Later tasks reference thumbnails as `thumbnail/{id}`.

Why an endpoint at all: `InstallationConfiguration.ThumbnailPath` is an absolute Windows path under `%LocalAppData%\DiffusionNexus\catalog`. A browser cannot load it, and base64-inlining 20+ images would push megabytes through the Blazor circuit on every render. Keying by workload id means no caller-supplied path, so there is no traversal surface.

- [ ] **Step 1: Write the failing test**

```csharp
using DiffusionNexus.Installer.Electron.Endpoints;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Host;

/// <summary>
/// Content-type derivation for the thumbnail endpoint. The catalog writer emits
/// "thumbnail.&lt;ext&gt;" preserving the source extension, so webp is the common case but not the
/// only one -- serving a PNG as image/webp makes it fail to decode in Chromium.
/// </summary>
public class ThumbnailEndpointTests
{
    [Theory]
    [InlineData(@"C:\catalog\workloads\krea\thumbnail.webp", "image/webp")]
    [InlineData(@"C:\catalog\workloads\krea\thumbnail.png", "image/png")]
    [InlineData(@"C:\catalog\workloads\krea\thumbnail.jpg", "image/jpeg")]
    [InlineData(@"C:\catalog\workloads\krea\thumbnail.jpeg", "image/jpeg")]
    [InlineData(@"C:\catalog\workloads\krea\THUMBNAIL.WEBP", "image/webp")]
    public void Derives_the_content_type_from_the_extension(string path, string expected)
    {
        ThumbnailEndpoint.ContentTypeFor(path).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\catalog\workloads\krea\thumbnail.tiff")]
    [InlineData(@"C:\catalog\workloads\krea\thumbnail")]
    public void Falls_back_to_octet_stream_for_anything_it_does_not_know(string? path)
    {
        // Never guess image/webp for an unknown extension: a wrong content type renders as a
        // broken image, which reads as "the catalog is broken" rather than "we shipped an odd file".
        ThumbnailEndpoint.ContentTypeFor(path).Should().Be("application/octet-stream");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test -c Release --filter FullyQualifiedName~ThumbnailEndpointTests`
Expected: FAIL — `ThumbnailEndpoint` does not exist (CS0246).

- [ ] **Step 3: Write the implementation**

Create `DiffusionNexus.Installer.Electron/Endpoints/ThumbnailEndpoint.cs`:

```csharp
using DiffusionNexus.Installer.Core.Catalog;

namespace DiffusionNexus.Installer.Electron.Endpoints;

/// <summary>
/// Serves workload thumbnails out of the catalog.
///
/// InstallationConfiguration.ThumbnailPath is an ABSOLUTE disk path under the installed catalog,
/// which no browser can load, so the cards cannot point at it directly. This endpoint is keyed by
/// workload id -- never by a caller-supplied path -- so there is no traversal surface.
/// </summary>
public static class ThumbnailEndpoint
{
    public static IEndpointRouteBuilder MapWorkloadThumbnails(this IEndpointRouteBuilder app)
    {
        app.MapGet("/thumbnail/{workloadId:guid}", async (
            Guid workloadId,
            IWorkloadSource workloads,
            HttpResponse response,
            CancellationToken ct) =>
        {
            var bytes = await workloads.GetThumbnailAsync(workloadId, ct);
            if (bytes is null || bytes.Length == 0) return Results.NotFound();

            var all = await workloads.GetInstallerWorkloadsAsync(ct);
            var path = all.FirstOrDefault(w => w.Id == workloadId)?.ThumbnailPath;

            // The catalog is immutable between updates, and 20+ cards would otherwise re-request
            // every thumbnail on each render of the workload screen.
            response.Headers.CacheControl = "private, max-age=3600";

            return Results.File(bytes, ContentTypeFor(path));
        });

        return app;
    }

    /// <summary>
    /// Content type from the thumbnail's own extension. The catalog writer preserves the source
    /// extension, so this is not always webp.
    /// </summary>
    public static string ContentTypeFor(string? thumbnailPath) =>
        Path.GetExtension(thumbnailPath ?? string.Empty).ToLowerInvariant() switch
        {
            ".webp" => "image/webp",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };
}
```

- [ ] **Step 4: Register the endpoint**

In `DiffusionNexus.Installer.Electron/Program.cs`, add the using alongside the others at the top:

```csharp
using DiffusionNexus.Installer.Electron.Endpoints;
```

and map it immediately after `app.MapStaticAssets();`:

```csharp
app.MapStaticAssets();
app.MapWorkloadThumbnails();
app.MapRazorComponents<BlazorApp>()
    .AddInteractiveServerRenderMode();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test -c Release --filter FullyQualifiedName~ThumbnailEndpointTests`
Expected: PASS — 9 tests.

Then confirm nothing else broke: `dotnet test -c Release`

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Installer.Electron/Endpoints/ThumbnailEndpoint.cs \
        DiffusionNexus.Installer.Electron/Program.cs \
        DiffusionNexus.Installer.Tests/Host/ThumbnailEndpointTests.cs
git commit -m "$(cat <<'EOF'
feat(welcome): serve workload thumbnails from the catalog

ThumbnailPath is an absolute disk path a browser cannot load, so cards get an
id-keyed endpoint over IWorkloadSource.GetThumbnailAsync instead. Content type
comes from the file's own extension -- the catalog writer preserves it, so webp
is common but not guaranteed -- and unknown extensions fall back to
octet-stream rather than guessing.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Group workloads into software entries

**Files:**
- Create: `DiffusionNexus.Installer.Core/Gallery/SoftwareEntry.cs`
- Create: `DiffusionNexus.Installer.Core/Gallery/SoftwareGalleryBuilder.cs`
- Modify: `DiffusionNexus.Installer.Core/ServiceCollectionExtensions.cs` (wherever `AddInstallerCore` registers `GalleryBuilder`)
- Test: `DiffusionNexus.Installer.Tests/Gallery/SoftwareGalleryBuilderTests.cs` (create)

**Interfaces:**
- Consumes: `GalleryBuilder.BuildAsync()` returning `IReadOnlyList<GalleryEntry>`; `GalleryEntry(InstallationConfiguration Workload, bool IsInstallable, WorkloadCapability MissingCapabilities, string? Incompatibility)` with `UnavailableReason`.
- Produces:
  - `sealed record SoftwareEntry(RepositoryType Type, string DisplayName, string? LogoPath, IReadOnlyList<GalleryEntry> Workloads)` with `int WorkloadCount`, `GalleryEntry? SingleWorkload`, `bool GoesStraightToSetup`.
  - `sealed class SoftwareGalleryBuilder(GalleryBuilder inner)` with `Task<IReadOnlyList<SoftwareEntry>> BuildAsync(CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing test**

```csharp
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Gallery;

/// <summary>
/// Software cards are derived from the catalog, never a fixed list of six: a card for a software
/// the catalog does not contain is a dead control, and a software it gains later must appear
/// without a code change.
/// </summary>
public class SoftwareGalleryBuilderTests
{
    private static GalleryEntry Entry(RepositoryType software, string name, bool installable = true) =>
        new(
            new InstallationConfiguration
            {
                Id = Guid.NewGuid(),
                Name = name,
                WorkflowType = WorkflowType.Image,
                Repository = new MainRepositorySettings { Type = software }
            },
            installable,
            WorkloadCapability.None,
            installable ? null : "Coming soon");

    private static IReadOnlyList<SoftwareEntry> Build(params GalleryEntry[] entries) =>
        SoftwareGalleryBuilder.Group(entries);

    [Fact]
    public void Groups_workloads_under_their_software()
    {
        var result = Build(
            Entry(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Entry(RepositoryType.ComfyUI, "Ideogram-4.0"),
            Entry(RepositoryType.Fooocus, "Fooocus"));

        result.Should().HaveCount(2);
        result.Single(s => s.Type == RepositoryType.ComfyUI).WorkloadCount.Should().Be(2);
        result.Single(s => s.Type == RepositoryType.Fooocus).WorkloadCount.Should().Be(1);
    }

    [Fact]
    public void A_software_with_one_workload_goes_straight_to_setup()
    {
        var only = Entry(RepositoryType.Fooocus, "Fooocus");

        var software = Build(only).Single();

        software.GoesStraightToSetup.Should().BeTrue();
        software.SingleWorkload.Should().BeSameAs(only);
    }

    [Fact]
    public void A_software_with_several_workloads_does_not()
    {
        var software = Build(
            Entry(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Entry(RepositoryType.ComfyUI, "Wan 2.2 - GGUF")).Single();

        software.GoesStraightToSetup.Should().BeFalse();
        software.SingleWorkload.Should().BeNull();
    }

    [Fact]
    public void Keeps_a_software_whose_only_workload_is_uninstallable()
    {
        // Hiding it makes "why is Fooocus missing?" unanswerable. The card renders, disabled,
        // and carries the reason.
        var blocked = Entry(RepositoryType.Fooocus, "Fooocus", installable: false);

        var software = Build(blocked).Single();

        software.WorkloadCount.Should().Be(1);
        software.SingleWorkload!.UnavailableReason.Should().Be("Coming soon");
    }

    [Fact]
    public void Carries_the_display_name_and_logo_for_each_software()
    {
        var software = Build(Entry(RepositoryType.AIToolkit, "AI-Toolkit")).Single();

        software.DisplayName.Should().Be("AI Toolkit");
        software.LogoPath.Should().Be("img/software/ai-toolkit.jpg");
    }

    [Fact]
    public void Orders_softwares_by_how_much_they_offer_then_by_name()
    {
        // ComfyUI carries 16 of the 21 offerable workloads; it belongs first rather than
        // wherever the catalog's own order happens to place it.
        var result = Build(
            Entry(RepositoryType.Fooocus, "Fooocus"),
            Entry(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Entry(RepositoryType.ComfyUI, "Ideogram-4.0"),
            Entry(RepositoryType.A1111, "Stable Diffusion web UI"));

        result.Select(s => s.Type).Should().ContainInOrder(
            RepositoryType.ComfyUI, RepositoryType.A1111, RepositoryType.Fooocus);
    }

    [Fact]
    public void Returns_nothing_for_an_empty_catalog()
    {
        Build().Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test -c Release --filter FullyQualifiedName~SoftwareGalleryBuilderTests`
Expected: FAIL — `SoftwareEntry` and `SoftwareGalleryBuilder` do not exist (CS0246).

- [ ] **Step 3: Write SoftwareEntry**

Create `DiffusionNexus.Installer.Core/Gallery/SoftwareEntry.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>One card on the welcome screen: a software and the workloads the catalog offers for it.</summary>
public sealed record SoftwareEntry(
    RepositoryType Type,
    string DisplayName,
    string? LogoPath,
    IReadOnlyList<GalleryEntry> Workloads)
{
    public int WorkloadCount => Workloads.Count;

    /// <summary>The only workload, when there is exactly one. Null otherwise.</summary>
    public GalleryEntry? SingleWorkload => Workloads.Count == 1 ? Workloads[0] : null;

    /// <summary>
    /// True when picking this card should open the wizard directly. A screen that asks the user
    /// to choose from a list of one is a screen that should not exist.
    /// </summary>
    public bool GoesStraightToSetup => SingleWorkload is not null;
}
```

- [ ] **Step 4: Write SoftwareGalleryBuilder**

Create `DiffusionNexus.Installer.Core/Gallery/SoftwareGalleryBuilder.cs`:

```csharp
namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>
/// Turns the flat workload gallery into the software cards the welcome screen shows.
///
/// Catalog-derived, like the filters it replaces: a software the catalog does not contain never
/// gets a card, and one it gains later appears without a code change.
/// </summary>
public sealed class SoftwareGalleryBuilder(GalleryBuilder inner)
{
    public async Task<IReadOnlyList<SoftwareEntry>> BuildAsync(CancellationToken ct = default) =>
        Group(await inner.BuildAsync(ct));

    /// <summary>Pure grouping, separated from the catalog read so it can be tested without one.</summary>
    public static IReadOnlyList<SoftwareEntry> Group(IEnumerable<GalleryEntry> entries) => entries
        .GroupBy(e => e.Workload.Repository.Type)
        .Select(g => new SoftwareEntry(
            g.Key,
            SoftwareBranding.DisplayName(g.Key),
            SoftwareBranding.LogoPath(g.Key),
            g.ToList()))
        // Most-offering first: ComfyUI carries 16 of the 21 offerable workloads and belongs at
        // the top, not wherever the catalog's own order happens to put it.
        .OrderByDescending(s => s.WorkloadCount)
        .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
}
```

The signature this consumes is `GalleryBuilder.BuildAsync(CancellationToken ct = default)` on `sealed class GalleryBuilder(IWorkloadSource source, WizardModuleRegistry registry)` — verified, no check needed.

- [ ] **Step 5: Register it in DI**

Find where `AddInstallerCore` registers `GalleryBuilder` in `DiffusionNexus.Installer.Core/ServiceCollectionExtensions.cs` and register the new builder next to it with the same lifetime:

```csharp
services.AddSingleton<SoftwareGalleryBuilder>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test -c Release --filter FullyQualifiedName~SoftwareGalleryBuilderTests`
Expected: PASS — 7 tests.

Then check DI still resolves: `dotnet test -c Release --filter FullyQualifiedName~DependencyInjectionTests`

- [ ] **Step 7: Commit**

```bash
git add DiffusionNexus.Installer.Core/Gallery/SoftwareEntry.cs \
        DiffusionNexus.Installer.Core/Gallery/SoftwareGalleryBuilder.cs \
        DiffusionNexus.Installer.Core/ServiceCollectionExtensions.cs \
        DiffusionNexus.Installer.Tests/Gallery/SoftwareGalleryBuilderTests.cs
git commit -m "$(cat <<'EOF'
feat(welcome): group workloads into software cards

Catalog-derived like the filters it replaces. A software with exactly one
workload is marked GoesStraightToSetup so the wizard opens directly instead of
offering a choice of one; a software whose only workload is blocked still gets
a card, because hiding it makes "why is this missing" unanswerable.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Top bar

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Components/Shared/TopBar.razor`
- Modify: `DiffusionNexus.Installer.Electron/wwwroot/app.css` (append)
- Test: `DiffusionNexus.Installer.Tests/Components/TopBarTests.cs` (create)

**Interfaces:**
- Produces: `TopBar` component with one parameter, `[Parameter] public EventCallback OnFeedback { get; set; }`. Renders `.top-bar` containing `.top-bar-version`, and anchors to `/licenses`, `/debug`, `/updates`, plus a Feedback button with class `top-bar-feedback`.

Feedback is a callback rather than navigation because the dialog is owned by the hosting page (Task 9). This task renders the button and leaves the callback unwired.

- [ ] **Step 1: Write the failing test**

```csharp
using Bunit;
using DiffusionNexus.Installer.Electron.Components.Shared;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The bar across the top of the welcome and workload screens. Carries the links that used to sit
/// in the gallery footer.
/// </summary>
public class TopBarTests : BunitContext
{
    [Fact]
    public void Links_to_licences_and_updates()
    {
        var cut = Render<TopBar>();

        cut.Find("a[href='/licenses']").TextContent.Should().Contain("Licences");
        cut.Find("a[href='/updates']").TextContent.Should().Contain("Check for Updates");
    }

    [Fact]
    public void Shows_the_app_version()
    {
        var cut = Render<TopBar>();

        // Whatever the assembly reports, it must not be the raw informational version with its
        // "+<commit sha>" suffix -- that is build metadata, not something to show a user.
        var version = cut.Find(".top-bar-version").TextContent;
        version.Should().StartWith("v");
        version.Should().NotContain("+");
    }

    [Fact]
    public void Raises_the_feedback_callback_rather_than_navigating()
    {
        var raised = false;
        var cut = Render<TopBar>(p => p.Add(x => x.OnFeedback, () => raised = true));

        var button = cut.Find(".top-bar-feedback");
        button.TagName.Should().Be("BUTTON", "an anchor would navigate the Electron window away");

        button.Click();

        raised.Should().BeTrue();
    }

    [Fact]
    public void Only_offers_developer_tools_when_the_page_it_links_to_exists()
    {
        var cut = Render<TopBar>();

        // The /debug page is compiled out of Release builds by the csproj, so the link must go
        // with it -- a Release build linking to a page that does not exist is a 404 in the user's
        // face. This assertion therefore flips with the build configuration.
        var expected =
#if DEBUG
            1;
#else
            0;
#endif
        cut.FindAll("a[href='/debug']").Should().HaveCount(expected);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test -c Release --filter FullyQualifiedName~TopBarTests`
Expected: FAIL — `TopBar` does not exist (CS0246).

- [ ] **Step 3: Write the component**

Create `DiffusionNexus.Installer.Electron/Components/Shared/TopBar.razor`:

```razor
@using System.Reflection

<div class="top-bar">
    <span class="top-bar-version">v@AppVersion</span>

    <div class="top-bar-actions">
        <button class="top-bar-btn top-bar-feedback" type="button" @onclick="OnFeedback">Feedback</button>
        <a class="top-bar-btn" href="/licenses">Licences</a>
        @if (ShowDeveloperTools)
        {
            <a class="top-bar-btn top-bar-dev" href="/debug">Developer tools</a>
        }
        <a class="top-bar-btn" href="/updates">Check for Updates</a>
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

    /// <summary>
    /// InformationalVersion carries a "+&lt;commit sha&gt;" suffix in CI builds. That is build
    /// metadata; the user wants a version number.
    /// </summary>
    private static string AppVersion
    {
        get
        {
            var raw = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "unknown";
            var plus = raw.IndexOf('+');
            return plus < 0 ? raw : raw[..plus];
        }
    }
}
```

- [ ] **Step 4: Add the styles**

Append to the end of `DiffusionNexus.Installer.Electron/wwwroot/app.css`. Check the file's existing colour variables first and reuse them rather than introducing new literals:

```css
/* ---------- Top bar ---------- */

.top-bar {
    display: flex;
    align-items: center;
    gap: 12px;
    flex-wrap: wrap;
    padding: 10px 16px;
    background: var(--panel);
    border-bottom: 1px solid var(--border);
}

.top-bar-version {
    font-family: Consolas, "Courier New", monospace;
    font-size: 12px;
    color: var(--muted);
    margin-right: auto;
}

.top-bar-actions {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
}

.top-bar-btn {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 6px 12px;
    border-radius: 8px;
    border: 1px solid var(--border);
    background: var(--panel);
    color: var(--text);
    font-size: 12.5px;
    font-weight: 500;
    text-decoration: none;
    cursor: pointer;
}

.top-bar-btn:hover {
    border-color: var(--accent);
}

.top-bar-feedback {
    border-color: var(--accent);
}

.top-bar-dev {
    border-style: dashed;
    color: var(--muted);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TopBarTests|FullyQualifiedName~StylesheetTests"`
Expected: PASS — 4 top-bar tests, and `StylesheetTests` still green, proving no brace was dropped.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Installer.Electron/Components/Shared/TopBar.razor \
        DiffusionNexus.Installer.Electron/wwwroot/app.css \
        DiffusionNexus.Installer.Tests/Components/TopBarTests.cs
git commit -m "$(cat <<'EOF'
feat(welcome): top bar with version, feedback, licences and updates

Carries the links that used to sit in the gallery footer. Feedback is a button
raising a callback, not an anchor -- an anchor would navigate the Electron
window away. Developer tools keeps the existing #if DEBUG const guard, since the
page it links to is compiled out of Release entirely.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Community links

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Components/Shared/CommunityLinks.razor`
- Create: `DiffusionNexus.Installer.Core/Gallery/CommunityLink.cs`
- Modify: `DiffusionNexus.Installer.Electron/wwwroot/app.css` (append)
- Test: `DiffusionNexus.Installer.Tests/Components/CommunityLinksTests.cs` (create)

**Interfaces:**
- Produces: `sealed record CommunityLink(string Name, string Url)` and `static IReadOnlyList<CommunityLink> CommunityLink.Default`; `CommunityLinks` component rendering `.community` with one `.community-link` per row.

- [ ] **Step 1: Write the failing test**

```csharp
using Bunit;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Electron.Components.Shared;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The "Join the Community" footer. Hardcoded this slice; issue #6 covers making it editable
/// without a release.
/// </summary>
public class CommunityLinksTests : BunitContext
{
    [Fact]
    public void Ships_the_three_links_the_2x_installer_ships()
    {
        CommunityLink.Default.Select(l => l.Name)
            .Should().Equal("YouTube", "Patreon", "Civitai");

        CommunityLink.Default.Select(l => l.Url).Should().Equal(
            "https://www.youtube.com/@IntoTheLatent",
            "https://patreon.com/AIKnowledgeCentral",
            "https://civitai.com/user/AIknowlege2go");
    }

    [Fact]
    public void Every_link_is_https()
    {
        CommunityLink.Default.Should().OnlyContain(l => l.Url.StartsWith("https://"));
    }

    [Fact]
    public void Renders_a_row_per_link_under_a_heading()
    {
        var cut = Render<CommunityLinks>();

        cut.Find(".community h4").TextContent.Should().Contain("Join the Community");
        cut.FindAll(".community-link").Should().HaveCount(3);
    }

    [Fact]
    public void Opens_links_outside_the_app_window()
    {
        var cut = Render<CommunityLinks>();

        // Outside Electron these are ordinary anchors, and they MUST carry target=_blank:
        // a same-window navigation strands the user in an installer that has become a browser
        // with no address bar and no way back.
        foreach (var link in cut.FindAll(".community-link"))
        {
            link.GetAttribute("target").Should().Be("_blank");
            link.GetAttribute("rel").Should().Contain("noopener");
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test -c Release --filter FullyQualifiedName~CommunityLinksTests`
Expected: FAIL — `CommunityLink` and `CommunityLinks` do not exist (CS0246).

- [ ] **Step 3: Write the model**

Create `DiffusionNexus.Installer.Core/Gallery/CommunityLink.cs`. `Default` lives **on the record**, not on a separate static class — the tests reach it as `CommunityLink.Default`, and the component type is already named `CommunityLinks`, so a static class by that name would collide:

```csharp
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
```

- [ ] **Step 4: Write the component**

Create `DiffusionNexus.Installer.Electron/Components/Shared/CommunityLinks.razor`:

```razor
@using DiffusionNexus.Installer.Core.Gallery
@using ElectronNET.API

<div class="community">
    <h4>Join the Community</h4>
    <div class="community-links">
        @foreach (var link in CommunityLink.Default)
        {
            <a class="community-link"
               href="@link.Url"
               target="_blank"
               rel="noopener noreferrer"
               @onclick="() => OpenAsync(link.Url)"
               @onclick:preventDefault="@ElectronActive">@link.Name</a>
        }
    </div>
</div>

@code {
    private static bool ElectronActive => HybridSupport.IsElectronActive;

    /// <summary>
    /// Inside Electron a plain anchor navigates the APP's own window to the target, stranding the
    /// user in an installer that has become a browser with no address bar. Hand the URL to the
    /// shell instead, and suppress the default navigation. Outside Electron -- running as a plain
    /// web app -- the anchor does the right thing on its own and this is a no-op.
    /// </summary>
    private async Task OpenAsync(string url)
    {
        if (!ElectronActive) return;

        try
        {
            await Electron.Shell.OpenExternalAsync(url);
        }
        catch (Exception)
        {
            // A community link that fails to open must never take the welcome screen down.
        }
    }
}
```

- [ ] **Step 5: Add the styles**

Append to `wwwroot/app.css`:

```css
/* ---------- Community footer ---------- */

.community {
    border-top: 1px solid var(--border);
    margin-top: 8px;
    padding-top: 18px;
    text-align: center;
}

.community h4 {
    font-size: 14px;
    font-weight: 700;
    margin: 0 0 12px;
    color: var(--text);
}

.community-links {
    display: flex;
    flex-wrap: wrap;
    gap: 9px;
    justify-content: center;
}

.community-link {
    display: inline-flex;
    align-items: center;
    padding: 6px 14px;
    border-radius: 999px;
    border: 1px solid var(--border);
    background: var(--panel);
    color: var(--text);
    font-size: 12.5px;
    font-weight: 500;
    text-decoration: none;
}

.community-link:hover {
    border-color: var(--accent);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~CommunityLinksTests|FullyQualifiedName~StylesheetTests"`
Expected: PASS — 4 community tests plus the stylesheet guard.

- [ ] **Step 7: Commit**

```bash
git add DiffusionNexus.Installer.Core/Gallery/CommunityLink.cs \
        DiffusionNexus.Installer.Electron/Components/Shared/CommunityLinks.razor \
        DiffusionNexus.Installer.Electron/wwwroot/app.css \
        DiffusionNexus.Installer.Tests/Components/CommunityLinksTests.cs
git commit -m "$(cat <<'EOF'
feat(welcome): community footer

The three links the 2.x installer ships, hardcoded. Inside Electron they go to
the shell rather than the app window -- a plain anchor turns the installer into
a browser with no address bar. Issue #6 covers making the list editable without
a release; reading the Linktree is ruled out and the reason is recorded on the
model.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Workload card

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Components/Shared/WorkloadCard.razor`
- Modify: `DiffusionNexus.Installer.Electron/wwwroot/app.css` (append)
- Test: `DiffusionNexus.Installer.Tests/Components/WorkloadCardTests.cs` (create)

**Interfaces:**
- Consumes: `GalleryEntry` from Task 3's interface block; the `thumbnail/{id}` route from Task 2.
- Produces: `WorkloadCard` with `[Parameter, EditorRequired] public GalleryEntry Entry { get; set; }` and `[Parameter] public EventCallback<GalleryEntry> OnInstall { get; set; }`. Renders `.workload-card`, `.workload-card-art` (an `img` when a thumbnail exists, else `.workload-card-art-fallback`), `.workload-card-name`, `.workload-card-badge`, and either `button.workload-card-install` or `.workload-card-unavailable`.

- [ ] **Step 1: Write the failing test**

```csharp
using Bunit;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Shared;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The card shared by the welcome screen (single-workload softwares) and the workload screen.
/// </summary>
public class WorkloadCardTests : BunitContext
{
    private static readonly Guid KreaId = Guid.Parse("e79c079a-2fd7-4fe7-8086-23731092555d");

    private static GalleryEntry Entry(
        bool installable = true,
        string? thumbnailPath = @"C:\catalog\workloads\krea\thumbnail.webp",
        WorkflowType type = WorkflowType.Image) =>
        new(
            new InstallationConfiguration
            {
                Id = KreaId,
                Name = "Krea-2-Turbo",
                WorkflowType = type,
                ThumbnailPath = thumbnailPath,
                Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI }
            },
            installable,
            WorkloadCapability.None,
            installable ? null : "Coming soon — needs VramProfile");

    [Fact]
    public void Shows_the_thumbnail_through_the_endpoint_not_the_disk_path()
    {
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry()));

        var img = cut.Find(".workload-card-art img");
        img.GetAttribute("src").Should().Be($"thumbnail/{KreaId}");
        img.GetAttribute("src").Should().NotContain(@"C:\", "a disk path is unloadable in a browser");
        img.GetAttribute("alt").Should().Be("Krea-2-Turbo");
    }

    [Fact]
    public void Falls_back_to_a_neutral_tile_when_there_is_no_thumbnail()
    {
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry(thumbnailPath: null)));

        cut.FindAll(".workload-card-art img").Should().BeEmpty();
        cut.Find(".workload-card-art-fallback").TextContent.Should().Contain("Krea-2-Turbo");
    }

    [Fact]
    public void Names_the_workload_and_badges_its_type()
    {
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry(type: WorkflowType.Video)));

        cut.Find(".workload-card-name").TextContent.Should().Be("Krea-2-Turbo");
        cut.Find(".workload-card-badge").TextContent.Should().Be("Video");
    }

    [Fact]
    public void Offers_install_when_the_workload_is_installable()
    {
        GalleryEntry? installed = null;
        var entry = Entry();
        var cut = Render<WorkloadCard>(p => p
            .Add(x => x.Entry, entry)
            .Add(x => x.OnInstall, e => installed = e));

        cut.Find(".workload-card-install").Click();

        installed.Should().BeSameAs(entry);
    }

    [Fact]
    public void Shows_the_reason_instead_of_an_install_button_when_it_is_not()
    {
        var cut = Render<WorkloadCard>(p => p.Add(x => x.Entry, Entry(installable: false)));

        cut.FindAll(".workload-card-install").Should().BeEmpty();
        cut.Find(".workload-card-unavailable").TextContent
            .Should().Contain("Coming soon — needs VramProfile");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test -c Release --filter FullyQualifiedName~WorkloadCardTests`
Expected: FAIL — `WorkloadCard` does not exist (CS0246).

- [ ] **Step 3: Write the component**

Create `DiffusionNexus.Installer.Electron/Components/Shared/WorkloadCard.razor`:

```razor
@using DiffusionNexus.Installer.Core.Gallery

<div class="workload-card @(Entry.IsInstallable ? "" : "workload-card-disabled")">
    <div class="workload-card-art">
        @if (Entry.Workload.ThumbnailPath is not null)
        {
            @* Through the endpoint, never the disk path: ThumbnailPath is absolute and
               unloadable in a browser. A 404 leaves the alt text, which is the workload name. *@
            <img src="thumbnail/@Entry.Workload.Id" alt="@Entry.Workload.Name" loading="lazy" />
        }
        else
        {
            <span class="workload-card-art-fallback">@Entry.Workload.Name</span>
        }
    </div>

    <div class="workload-card-meta">
        <p class="workload-card-name">@Entry.Workload.Name</p>
        <span class="workload-card-badge">@Entry.Workload.WorkflowType</span>

        @if (Entry.IsInstallable)
        {
            <button class="workload-card-install" type="button" @onclick="() => OnInstall.InvokeAsync(Entry)">
                Install
            </button>
        }
        else
        {
            <p class="workload-card-unavailable">@Entry.UnavailableReason</p>
        }
    </div>
</div>

@code {
    [Parameter, EditorRequired] public GalleryEntry Entry { get; set; } = default!;

    /// <summary>Raised with this card's entry when the user chooses to install it.</summary>
    [Parameter] public EventCallback<GalleryEntry> OnInstall { get; set; }
}
```

- [ ] **Step 4: Add the styles**

Append to `wwwroot/app.css`:

```css
/* ---------- Workload cards ---------- */

.workload-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
    gap: 14px;
}

.workload-card {
    display: flex;
    flex-direction: column;
    border: 1px solid var(--border);
    border-radius: 11px;
    background: var(--panel);
    overflow: hidden;
}

.workload-card-disabled {
    opacity: 0.55;
}

.workload-card-art {
    aspect-ratio: 1 / 1;
    display: grid;
    place-items: center;
    background: var(--bg);
    border-bottom: 1px solid var(--border);
}

.workload-card-art img {
    width: 100%;
    height: 100%;
    max-width: 100%;
    object-fit: cover;
    display: block;
}

.workload-card-art-fallback {
    padding: 12px;
    text-align: center;
    font-size: 12px;
    font-weight: 600;
    color: var(--muted);
}

.workload-card-meta {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 6px;
    padding: 10px 12px 12px;
}

.workload-card-name {
    margin: 0;
    font-size: 13px;
    font-weight: 600;
    line-height: 1.3;
    color: var(--text);
}

.workload-card-badge {
    font-size: 10px;
    letter-spacing: 0.07em;
    text-transform: uppercase;
    padding: 2px 7px;
    border-radius: 4px;
    background: rgba(31, 184, 166, 0.18);
    color: var(--accent);
}

.workload-card-install {
    margin-top: 2px;
    padding: 6px 14px;
    border-radius: 8px;
    border: 1px solid var(--accent);
    background: rgba(31, 184, 166, 0.15);
    color: var(--text);
    font-size: 12.5px;
    font-weight: 600;
    cursor: pointer;
}

.workload-card-unavailable {
    margin: 0;
    font-size: 11.5px;
    color: var(--muted);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~WorkloadCardTests|FullyQualifiedName~StylesheetTests"`
Expected: PASS — 5 card tests plus the stylesheet guard.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Installer.Electron/Components/Shared/WorkloadCard.razor \
        DiffusionNexus.Installer.Electron/wwwroot/app.css \
        DiffusionNexus.Installer.Tests/Components/WorkloadCardTests.cs
git commit -m "$(cat <<'EOF'
feat(welcome): workload card with catalog artwork

The catalog has shipped a thumbnail for 23 of 25 workloads all along and the app
rendered a text heading instead. Cards now load it through the id-keyed
endpoint, falling back to a neutral name tile for the two that have none.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Welcome page

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Components/Pages/Welcome.razor`
- Delete: `DiffusionNexus.Installer.Electron/Components/Pages/Gallery.razor`
- Modify: `DiffusionNexus.Installer.Electron/wwwroot/app.css` (append)
- Modify/Delete: `DiffusionNexus.Installer.Tests/Components/GalleryPageTests.cs` (its one test moves to the new page)
- Test: `DiffusionNexus.Installer.Tests/Components/WelcomePageTests.cs` (create)

**Interfaces:**
- Consumes: `SoftwareGalleryBuilder.BuildAsync()`, `SoftwareEntry`, `TopBar`, `CommunityLinks`, `WorkloadCard`, `IWorkloadSource.Diagnostics`.
- Produces: the page at route `/`. Renders `.welcome-banner`, `.welcome-title`, `.software-grid` with `.software-card` children, and the shared top bar and community footer.

- [ ] **Step 1: Write the failing test**

```csharp
using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class WelcomePageTests : BunitContext
{
    private static InstallationConfiguration Workload(RepositoryType software, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        WorkflowType = WorkflowType.Image,
        Repository = new MainRepositorySettings { Type = software }
    };

    private void Arrange(params InstallationConfiguration[] workloads)
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(workloads);
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());

        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));
    }

    [Fact]
    public void Shows_the_banner_and_the_title()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() =>
            cut.Find(".welcome-banner img").GetAttribute("src").Should().Be("img/banner.jpg"));
        cut.Find(".welcome-title").TextContent.Should().Contain("Easy Workload Installer");
    }

    [Fact]
    public void Renders_one_card_per_software_the_catalog_contains()
    {
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Workload(RepositoryType.ComfyUI, "Ideogram-4.0"),
            Workload(RepositoryType.Fooocus, "Fooocus"));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().HaveCount(2));
        cut.Markup.Should().Contain("ComfyUI").And.Contain("Fooocus");
        cut.Markup.Should().NotContain("Automatic 1111", "no A1111 workload is in this catalog");
    }

    [Fact]
    public void A_multi_workload_software_links_to_its_workload_screen()
    {
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo"),
            Workload(RepositoryType.ComfyUI, "Ideogram-4.0"));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() =>
            cut.Find(".software-card a").GetAttribute("href").Should().Be("/software/ComfyUI"));
        cut.Find(".software-card-count").TextContent.Should().Contain("2 workloads");
    }

    [Fact]
    public void A_single_workload_software_offers_the_workload_directly()
    {
        var fooocus = Workload(RepositoryType.Fooocus, "Fooocus");
        Arrange(fooocus);

        var cut = Render<Welcome>();

        // No /software/Fooocus link: a screen offering a choice of one should not exist.
        cut.WaitForAssertion(() => cut.FindAll("a[href='/software/Fooocus']").Should().BeEmpty());
        cut.Find(".software-card-count").TextContent.Should().Contain("straight to setup");
    }

    [Fact]
    public void Keeps_a_software_whose_only_workload_cannot_be_installed()
    {
        // Registering no modules makes every workload with blocking capabilities uninstallable;
        // the card must still render so the reason is visible somewhere.
        Arrange(Workload(RepositoryType.Fooocus, "Fooocus"));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.FindAll(".software-card").Should().ContainSingle());
    }

    [Fact]
    public void Says_why_it_is_empty_rather_than_taking_the_app_down()
    {
        // This rule was written for the old gallery and still applies: a hard catalog failure
        // must report itself, not throw out of the component lifecycle.
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync([]);
        source.SetupGet(s => s.Diagnostics).Returns(new[]
        {
            new CatalogDiagnostic(CatalogDiagnosticSeverity.Error, "CAT001", "catalog.zip is corrupt", "catalog.zip")
        });

        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));

        var cut = Render<Welcome>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CAT001").And.Contain("corrupt"));
    }

    [Fact]
    public void Shows_the_community_footer_and_the_top_bar_whatever_the_catalog_did()
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());
        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));

        var cut = Render<Welcome>();

        // Moved up from the old gallery footer; must survive an empty catalog.
        cut.WaitForAssertion(() => cut.Find("a[href='/licenses']").Should().NotBeNull());
        cut.FindAll(".community-link").Should().HaveCount(3);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test -c Release --filter FullyQualifiedName~WelcomePageTests`
Expected: FAIL — `Welcome` does not exist (CS0246).

- [ ] **Step 3: Write the page**

Create `DiffusionNexus.Installer.Electron/Components/Pages/Welcome.razor`:

```razor
@page "/"
@using DiffusionNexus.Installer.Core.Catalog
@using DiffusionNexus.Installer.Core.Gallery
@using DiffusionNexus.Installer.Electron.Components.Shared
@using DiffusionNexus.Installer.SDK.Catalog
@inject SoftwareGalleryBuilder Builder
@inject IWorkloadSource Workloads
@inject NavigationManager Nav

<PageTitle>DiffusionNexus Installer</PageTitle>

<TopBar />

<div class="welcome">
    <div class="welcome-banner">
        <img src="img/banner.jpg" alt="Into The Latent" />
    </div>

    <h1 class="welcome-title">Easy Workload Installer</h1>
    <p class="welcome-subtitle">Choose which AI application you want to install.</p>

    @if (_software is null)
    {
        <p>Loading the catalog...</p>
    }
    else if (_loadError is not null)
    {
        <p class="gallery-error">The catalog could not be read: @_loadError</p>
    }
    else if (_software.Count == 0)
    {
        @* Diagnostics before the bland empty message. The SDK reports a missing or unreadable
           catalog as Error diagnostics on a successfully-returned empty snapshot -- never as an
           exception -- so without this a hard failure on a cold start is indistinguishable from
           "nothing to install". *@
        @if (_errors.Count > 0)
        {
            <p class="gallery-error">The catalog could not be loaded:</p>
            <ul class="gallery-error">
                @foreach (var diagnostic in _errors)
                {
                    <li>@diagnostic.Code: @diagnostic.Message</li>
                }
            </ul>
        }
        else
        {
            <p>No workloads are available.</p>
        }
    }
    else
    {
        <div class="software-grid">
            @foreach (var software in _software)
            {
                @if (software.GoesStraightToSetup)
                {
                    var only = software.SingleWorkload!;
                    <div class="software-card">
                        <div class="software-card-art">
                            @if (software.LogoPath is not null)
                            {
                                <img src="@software.LogoPath" alt="@software.DisplayName" />
                            }
                            else
                            {
                                <span class="software-card-art-fallback">@software.DisplayName</span>
                            }
                        </div>
                        <div class="software-card-meta">
                            <p class="software-card-name">@software.DisplayName</p>
                            @if (only.IsInstallable)
                            {
                                <p class="software-card-count">straight to setup</p>
                                <button class="workload-card-install" type="button"
                                        @onclick="() => Install(only)">Install</button>
                            }
                            else
                            {
                                <p class="software-card-count">straight to setup</p>
                                <p class="workload-card-unavailable">@only.UnavailableReason</p>
                            }
                        </div>
                    </div>
                }
                else
                {
                    <div class="software-card">
                        <a href="/software/@software.Type">
                            <div class="software-card-art">
                                @if (software.LogoPath is not null)
                                {
                                    <img src="@software.LogoPath" alt="@software.DisplayName" />
                                }
                                else
                                {
                                    <span class="software-card-art-fallback">@software.DisplayName</span>
                                }
                            </div>
                            <div class="software-card-meta">
                                <p class="software-card-name">@software.DisplayName</p>
                                <p class="software-card-count">@software.WorkloadCount workloads</p>
                            </div>
                        </a>
                    </div>
                }
            }
        </div>
    }

    <CommunityLinks />
</div>

@code {
    private IReadOnlyList<SoftwareEntry>? _software;
    private IReadOnlyList<CatalogDiagnostic> _errors = [];
    private string? _loadError;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _software = await Builder.BuildAsync();

            // Read after the build: the catalog populates diagnostics during the load the build
            // triggers, so asking earlier reports on a catalog that has not been resolved yet.
            _errors = Workloads.Diagnostics.Where(d => d.IsError).ToList();
        }
        catch (Exception ex)
        {
            // This is the first screen; it must say why it is empty rather than take the app down
            // with an unhandled exception out of the component lifecycle.
            _loadError = ex.Message;
            _software = [];
        }
    }

    private void Install(GalleryEntry entry) => Nav.NavigateTo($"/install/{entry.Workload.Id}");
}
```

- [ ] **Step 4: Add the styles**

Append to `wwwroot/app.css`:

```css
/* ---------- Welcome screen ---------- */

.welcome {
    max-width: 1000px;
    margin-inline: auto;
    padding-inline: 24px;
    padding-block: 26px 28px;
    display: flex;
    flex-direction: column;
    gap: 20px;
}

.welcome-banner {
    aspect-ratio: 3.83 / 1;
    width: 100%;
    border-radius: 10px;
    overflow: hidden;
    background: #000;
}

.welcome-banner img {
    width: 100%;
    height: 100%;
    max-width: 100%;
    object-fit: cover;
    object-position: center;
    display: block;
}

.welcome-title {
    margin: 0;
    text-align: center;
    font-size: 32px;
    font-weight: 800;
    letter-spacing: -0.02em;
    background: linear-gradient(96deg, #8b7bf0 6%, #5b8def 62%, var(--accent) 100%);
    -webkit-background-clip: text;
    background-clip: text;
    color: transparent;
}

.welcome-subtitle {
    margin: -12px 0 0;
    text-align: center;
    font-size: 13px;
    color: var(--muted);
}

.software-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
    gap: 14px;
}

.software-card {
    border: 1px solid var(--border);
    border-radius: 11px;
    background: var(--panel);
    overflow: hidden;
}

.software-card:hover {
    border-color: var(--accent);
}

.software-card a {
    display: block;
    color: inherit;
    text-decoration: none;
}

.software-card-art {
    aspect-ratio: 16 / 10;
    display: grid;
    place-items: center;
    background: var(--bg);
    border-bottom: 1px solid var(--border);
}

.software-card-art img {
    width: 100%;
    height: 100%;
    max-width: 100%;
    object-fit: cover;
    display: block;
}

.software-card-art-fallback {
    padding: 12px;
    text-align: center;
    font-size: 13px;
    font-weight: 700;
    color: var(--muted);
}

.software-card-meta {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 5px;
    padding: 11px 13px 13px;
}

.software-card-name {
    margin: 0;
    font-size: 14px;
    font-weight: 600;
    color: var(--text);
}

.software-card-count {
    margin: 0;
    font-size: 11.5px;
    color: var(--muted);
}
```

- [ ] **Step 5: Delete the old gallery and move its surviving test**

```bash
git rm DiffusionNexus.Installer.Electron/Components/Pages/Gallery.razor
git rm DiffusionNexus.Installer.Tests/Components/GalleryPageTests.cs
```

`GalleryPageTests`' single test — that the licences link survives an empty catalog — is already
reproduced as `Shows_the_community_footer_and_the_top_bar_whatever_the_catalog_did` above, so
nothing is lost. Grep for any other reference to the deleted page and fix it:

```bash
grep -rn "Pages.Gallery\|Pages/Gallery" --include=*.cs --include=*.razor . | grep -v obj/ | grep -v bin/
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test -c Release`
Expected: PASS — the whole suite, including the seven new welcome tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat(welcome): welcome screen replaces the workload list

Banner, gradient title, catalog-derived software cards, community footer, top
bar. A software with several workloads links to its own screen; one with a
single workload offers it directly rather than opening a screen with one choice.

Gallery.razor is deleted. Its catalog-failure and diagnostics-before-empty rules
move here intact and are re-asserted by tests, because after the split both this
page and the workload page are "the first screen" depending on where the user
lands.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: Workload selection page

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Components/Pages/SoftwareWorkloads.razor`
- Modify: `DiffusionNexus.Installer.Electron/wwwroot/app.css` (append)
- Test: `DiffusionNexus.Installer.Tests/Components/SoftwareWorkloadsPageTests.cs` (create)

**Interfaces:**
- Consumes: `SoftwareGalleryBuilder`, `SoftwareEntry`, `WorkloadCard`, `TopBar`.
- Produces: the page at route `/software/{Software}` where `{Software}` is a `RepositoryType` name. Renders `.workload-screen`, `.workload-screen-back`, `.filters` with `.filter`/`.filter-active` buttons, `.workload-grid`, and a `.software-not-found` state.

- [ ] **Step 1: Write the failing test**

```csharp
using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class SoftwareWorkloadsPageTests : BunitContext
{
    private static InstallationConfiguration Workload(RepositoryType software, string name, WorkflowType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        WorkflowType = type,
        Repository = new MainRepositorySettings { Type = software }
    };

    private void Arrange(params InstallationConfiguration[] workloads)
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(workloads);
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());
        var gallery = new GalleryBuilder(source.Object, new WizardModuleRegistry(() => []));
        Services.AddSingleton(source.Object);
        Services.AddSingleton(gallery);
        Services.AddSingleton(new SoftwareGalleryBuilder(gallery));
    }

    private IRenderedComponent<SoftwareWorkloads> RenderFor(string software) =>
        Render<SoftwareWorkloads>(p => p.Add(x => x.Software, software));

    [Fact]
    public void Lists_only_the_chosen_softwares_workloads()
    {
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image),
            Workload(RepositoryType.ComfyUI, "Wan 2.2 - GGUF", WorkflowType.Video),
            Workload(RepositoryType.Fooocus, "Fooocus", WorkflowType.Image));

        var cut = RenderFor("ComfyUI");

        cut.WaitForAssertion(() => cut.FindAll(".workload-card").Should().HaveCount(2));
        cut.Markup.Should().NotContain("Fooocus");
    }

    [Fact]
    public void Offers_only_the_types_this_software_actually_has()
    {
        // Audio exists in the catalog but belongs to AceStep, so it must not appear here. The
        // filter is catalog-derived, so it will appear by itself the day a ComfyUI workload
        // declares Audio -- nothing to schedule for later.
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image),
            Workload(RepositoryType.ComfyUI, "Wan 2.2 - GGUF", WorkflowType.Video),
            Workload(RepositoryType.AceStep, "ACE-Step-1.5", WorkflowType.Audio));

        var cut = RenderFor("ComfyUI");

        cut.WaitForAssertion(() =>
        {
            var labels = cut.FindAll(".filters button").Select(b => b.TextContent.Trim()).ToList();
            labels.Should().Equal("All", "Image", "Video");
        });
    }

    [Fact]
    public void Filters_the_cards_when_a_type_is_chosen()
    {
        Arrange(
            Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image),
            Workload(RepositoryType.ComfyUI, "Wan 2.2 - GGUF", WorkflowType.Video));

        var cut = RenderFor("ComfyUI");
        cut.WaitForAssertion(() => cut.FindAll(".workload-card").Should().HaveCount(2));

        cut.FindAll(".filters button").Single(b => b.TextContent.Trim() == "Video").Click();

        cut.FindAll(".workload-card").Should().ContainSingle();
        cut.Markup.Should().Contain("Wan 2.2 - GGUF").And.NotContain("Krea-2-Turbo");
    }

    [Fact]
    public void Has_no_software_filter_because_the_previous_screen_answered_it()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image));

        var cut = RenderFor("ComfyUI");

        cut.WaitForAssertion(() => cut.FindAll(".filters").Should().ContainSingle());
    }

    [Fact]
    public void Offers_a_way_back_to_the_welcome_screen()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image));

        var cut = RenderFor("ComfyUI");

        cut.WaitForAssertion(() =>
            cut.Find(".workload-screen-back").GetAttribute("href").Should().Be("/"));
    }

    [Theory]
    [InlineData("Nonsense")]
    [InlineData("")]
    public void Says_so_when_the_software_is_not_in_the_catalog(string software)
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image));

        var cut = RenderFor(software);

        cut.WaitForAssertion(() => cut.Find(".software-not-found").Should().NotBeNull());
        cut.Find(".software-not-found a").GetAttribute("href").Should().Be("/");
    }

    [Fact]
    public void Says_so_for_a_real_software_the_catalog_has_no_workloads_for()
    {
        Arrange(Workload(RepositoryType.ComfyUI, "Krea-2-Turbo", WorkflowType.Image));

        var cut = RenderFor("Forge");

        cut.WaitForAssertion(() => cut.Find(".software-not-found").Should().NotBeNull());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test -c Release --filter FullyQualifiedName~SoftwareWorkloadsPageTests`
Expected: FAIL — `SoftwareWorkloads` does not exist (CS0246).

- [ ] **Step 3: Write the page**

Create `DiffusionNexus.Installer.Electron/Components/Pages/SoftwareWorkloads.razor`:

```razor
@page "/software/{Software}"
@using DiffusionNexus.Installer.Core.Gallery
@using DiffusionNexus.Installer.Electron.Components.Shared
@using DiffusionNexus.Installer.SDK.Models.Configuration
@using DiffusionNexus.Installer.SDK.Models.Enums
@inject SoftwareGalleryBuilder Builder
@inject NavigationManager Nav

<PageTitle>Select workload</PageTitle>

<TopBar />

<div class="workload-screen">
    @if (_entry is null && _loaded)
    {
        <div class="software-not-found">
            <p>That software is not in the catalog.</p>
            <a href="/">Back to all software</a>
        </div>
    }
    else if (_entry is null)
    {
        <p>Loading the catalog...</p>
    }
    else
    {
        <a class="workload-screen-back" href="/">&larr; All software</a>

        <div class="workload-screen-head">
            <h1>Select workload</h1>
            <p>Choose what you want @_entry.DisplayName set up for. You can install more later.</p>
        </div>

        <div class="filters">
            <button class="@TypeClass(null)" type="button" @onclick="() => _type = null">All</button>
            @foreach (var type in AvailableTypes())
            {
                var captured = type;
                <button class="@TypeClass(captured)" type="button" @onclick="() => _type = captured">@captured</button>
            }
        </div>

        <div class="workload-grid">
            @foreach (var workload in Filtered())
            {
                <WorkloadCard Entry="workload" OnInstall="Install" />
            }
        </div>
    }
</div>

@code {
    /// <summary>The RepositoryType name from the route. Not parsed into the enum by the router:
    /// an unrecognised value must render the not-found state, not a 404 or an exception.</summary>
    [Parameter] public string? Software { get; set; }

    private SoftwareEntry? _entry;
    private bool _loaded;
    private WorkflowType? _type;

    protected override async Task OnParametersSetAsync()
    {
        // Filter state is per-software: arriving at a different software must not inherit the
        // previous one's chosen type.
        _type = null;
        _entry = null;
        _loaded = false;

        var all = await Builder.BuildAsync();

        if (Enum.TryParse<RepositoryType>(Software, ignoreCase: true, out var parsed))
        {
            _entry = all.FirstOrDefault(s => s.Type == parsed);
        }

        _loaded = true;
    }

    private IEnumerable<GalleryEntry> Filtered() => _entry!.Workloads
        .Where(e => _type is null || e.Workload.WorkflowType == _type);

    /// <summary>
    /// Derived from this software's own workloads, so the button set answers "what can ComfyUI
    /// make", not "what types exist somewhere in the catalog". A filter that can only ever return
    /// nothing is a dead control.
    /// </summary>
    private IEnumerable<WorkflowType> AvailableTypes() => _entry!.Workloads
        .Select(e => e.Workload.WorkflowType)
        .Distinct()
        .OrderBy(t => t.ToString(), StringComparer.CurrentCultureIgnoreCase);

    private string TypeClass(WorkflowType? type) => _type == type ? "filter-active" : "filter";

    private void Install(GalleryEntry entry) => Nav.NavigateTo($"/install/{entry.Workload.Id}");
}
```

- [ ] **Step 4: Add the styles**

Append to `wwwroot/app.css` (`.filters`, `.filter` and `.filter-active` already exist from the old
gallery — reuse them, and only add what is missing):

```css
/* ---------- Workload selection screen ---------- */

.workload-screen {
    max-width: 1000px;
    margin-inline: auto;
    padding-inline: 24px;
    padding-block: 22px 28px;
    display: flex;
    flex-direction: column;
    gap: 16px;
}

.workload-screen-back {
    align-self: flex-start;
    padding: 5px 11px;
    border-radius: 8px;
    border: 1px solid var(--border);
    background: var(--panel);
    color: var(--text);
    font-size: 12.5px;
    font-weight: 500;
    text-decoration: none;
}

.workload-screen-head h1 {
    margin: 0;
    font-size: 22px;
    font-weight: 700;
    color: var(--text);
}

.workload-screen-head p {
    margin: 4px 0 0;
    font-size: 13px;
    color: var(--muted);
}

.software-not-found {
    padding: 40px 0;
    text-align: center;
    color: var(--muted);
}

.software-not-found a {
    color: var(--accent);
}
```

`.filters` (line 226), `.filter-label` (234), `.filter` / `.filter-active` (243, 260) and
`.filter:hover` (255) already exist from the old gallery — **keep them**, this page uses them.
Deleting `Gallery.razor` in Task 7 leaves them referenced only from here.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test -c Release`
Expected: PASS — the whole suite, including eight new page tests.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Installer.Electron/Components/Pages/SoftwareWorkloads.razor \
        DiffusionNexus.Installer.Electron/wwwroot/app.css \
        DiffusionNexus.Installer.Tests/Components/SoftwareWorkloadsPageTests.cs
git commit -m "$(cat <<'EOF'
feat(welcome): workload selection screen

Reached only from a multi-workload software card. The software filter is gone —
the previous screen answered it — and the type filter is derived from this
software's own workloads, so it says what ComfyUI can make rather than what
exists somewhere in the catalog. An unrecognised or empty software renders a
not-found state with a way back, never a 404.

Filter state resets on navigation so a second software does not inherit the
first one's chosen type.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: Feedback dialog

**Files:**
- Create: `DiffusionNexus.Installer.Electron/Components/Shared/FeedbackDialog.razor`
- Modify: `DiffusionNexus.Installer.Electron/Program.cs` (DI registration)
- Modify: `DiffusionNexus.Installer.Electron/Components/Pages/Welcome.razor` (host the dialog, wire `OnFeedback`)
- Modify: `DiffusionNexus.Installer.Electron/Components/Pages/SoftwareWorkloads.razor` (same)
- Modify: `DiffusionNexus.Installer.Electron/wwwroot/app.css` (append)
- Test: `DiffusionNexus.Installer.Tests/Components/FeedbackDialogTests.cs` (create)

**Interfaces:**
- Consumes: `IFeedbackReportingService.SubmitAsync(FeedbackReport, CancellationToken)` returning `FeedbackSubmissionResult { bool Success, string? IssueUrl, string? ErrorMessage }`; `FeedbackReport { FeedbackProduct Product, FeedbackReportType ReportType, string Title, string Description, string? WhatHappened, string? WhatShouldHaveHappened, string? Email, byte[]? ScreenshotPng, string? LogTail, string AppVersion, string Os, DateTimeOffset TimestampUtc }`. All in `DiffusionNexus.Installer.SDK.Shared.Services.Feedback`.
- Produces: `FeedbackDialog` with `[Parameter] public bool Visible { get; set; }` and `[Parameter] public EventCallback OnClose { get; set; }`.

No backend work: `FeedbackReportingService` is already in `SDK.Shared`, which this app already
references, and it posts to the relay that 2.x uses.

- [ ] **Step 1: Write the failing test**

```csharp
using Bunit;
using DiffusionNexus.Installer.Electron.Components.Shared;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class FeedbackDialogTests : BunitContext
{
    private Mock<IFeedbackReportingService> Arrange(FeedbackSubmissionResult result)
    {
        var service = new Mock<IFeedbackReportingService>();
        service.Setup(s => s.SubmitAsync(It.IsAny<FeedbackReport>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(result);
        Services.AddSingleton(service.Object);
        return service;
    }

    private IRenderedComponent<FeedbackDialog> Open() =>
        Render<FeedbackDialog>(p => p.Add(x => x.Visible, true));

    [Fact]
    public void Renders_nothing_until_it_is_opened()
    {
        Arrange(FeedbackSubmissionResult.Succeeded("https://github.com/x/y/issues/1"));

        var cut = Render<FeedbackDialog>(p => p.Add(x => x.Visible, false));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Will_not_submit_without_a_title_and_a_description()
    {
        Arrange(FeedbackSubmissionResult.Succeeded("https://github.com/x/y/issues/1"));

        var cut = Open();

        cut.Find(".feedback-submit").HasAttribute("disabled").Should().BeTrue();

        cut.Find("#feedback-title").Change("Install fails on a folder with spaces");
        cut.Find(".feedback-submit").HasAttribute("disabled").Should().BeTrue("description is still empty");

        cut.Find("#feedback-description").Change("It stops at step 3.");
        cut.Find(".feedback-submit").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Sends_a_report_stamped_as_coming_from_the_installer()
    {
        var service = Arrange(FeedbackSubmissionResult.Succeeded("https://github.com/x/y/issues/1"));

        var cut = Open();
        cut.Find("#feedback-title").Change("Install fails");
        cut.Find("#feedback-description").Change("It stops at step 3.");
        cut.Find(".feedback-submit").Click();

        cut.WaitForAssertion(() => service.Verify(s => s.SubmitAsync(
            It.Is<FeedbackReport>(r =>
                r.Product == FeedbackProduct.Installer &&
                r.Title == "Install fails" &&
                r.Description == "It stops at step 3." &&
                r.ScreenshotPng == null &&
                r.LogTail == null &&
                r.AppVersion != null &&
                r.Os != null),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void Shows_the_issue_url_when_the_report_lands()
    {
        Arrange(FeedbackSubmissionResult.Succeeded("https://github.com/Into-The-Latent/Feedback/issues/42"));

        var cut = Open();
        cut.Find("#feedback-title").Change("Install fails");
        cut.Find("#feedback-description").Change("It stops at step 3.");
        cut.Find(".feedback-submit").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".feedback-success").TextContent.Should().Contain("issues/42"));
    }

    [Fact]
    public void Keeps_the_dialog_and_the_typed_text_when_submission_fails()
    {
        // SubmitAsync never throws for network or parse failures -- it returns Success == false.
        // Closing on failure would throw away the thing the user just wrote.
        Arrange(FeedbackSubmissionResult.Failed("The relay returned 503."));

        var cut = Open();
        cut.Find("#feedback-title").Change("Install fails");
        cut.Find("#feedback-description").Change("It stops at step 3.");
        cut.Find(".feedback-submit").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".feedback-error").TextContent.Should().Contain("The relay returned 503."));

        cut.Find("#feedback-title").GetAttribute("value").Should().Be("Install fails");
        cut.FindAll(".feedback-dialog").Should().ContainSingle("the dialog must stay open");
    }

    [Fact]
    public async Task Closes_when_cancelled()
    {
        Arrange(FeedbackSubmissionResult.Succeeded("https://github.com/x/y/issues/1"));
        var closed = false;

        var cut = Render<FeedbackDialog>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.OnClose, () => closed = true));

        await cut.Find(".feedback-cancel").ClickAsync(new());

        closed.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test -c Release --filter FullyQualifiedName~FeedbackDialogTests`
Expected: FAIL — `FeedbackDialog` does not exist (CS0246).

- [ ] **Step 3: Write the dialog**

Create `DiffusionNexus.Installer.Electron/Components/Shared/FeedbackDialog.razor`:

```razor
@using System.Reflection
@using System.Runtime.InteropServices
@using DiffusionNexus.Installer.SDK.Shared.Services.Feedback
@inject IFeedbackReportingService Feedback

@if (Visible)
{
    <div class="modal-backdrop">
        <div class="feedback-dialog">
            <h3>Send feedback</h3>

            @if (_issueUrl is not null)
            {
                <p class="feedback-success">
                    Thanks — your report is filed as <span class="feedback-issue">@_issueUrl</span>.
                </p>
                <div class="feedback-actions">
                    <button class="feedback-cancel" type="button" @onclick="CloseAsync">Close</button>
                </div>
            }
            else
            {
                <label for="feedback-type">What is this?</label>
                <select id="feedback-type" @bind="_reportType">
                    <option value="@FeedbackReportType.Feedback">Feedback</option>
                    <option value="@FeedbackReportType.Bug">Something is broken</option>
                    <option value="@FeedbackReportType.FeatureRequest">An idea</option>
                </select>

                <label for="feedback-title">Summary</label>
                <input id="feedback-title" value="@_title"
                       @onchange="e => _title = e.Value?.ToString() ?? string.Empty" />

                <label for="feedback-description">Details</label>
                <textarea id="feedback-description" rows="5" value="@_description"
                          @onchange="e => _description = e.Value?.ToString() ?? string.Empty"></textarea>

                <label for="feedback-email">Your e-mail (optional, for follow-up questions)</label>
                <input id="feedback-email" value="@_email"
                       @onchange="e => _email = e.Value?.ToString() ?? string.Empty" />

                @if (_error is not null)
                {
                    <p class="feedback-error">@_error</p>
                }

                <div class="feedback-actions">
                    <button class="feedback-cancel" type="button" @onclick="CloseAsync" disabled="@_busy">Cancel</button>
                    <button class="feedback-submit" type="button" @onclick="SubmitAsync" disabled="@(!CanSubmit)">
                        @(_busy ? "Sending..." : "Send")
                    </button>
                </div>
            }
        </div>
    </div>
}

@code {
    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private FeedbackReportType _reportType = FeedbackReportType.Feedback;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _email = string.Empty;
    private string? _error;
    private string? _issueUrl;
    private bool _busy;

    private bool CanSubmit =>
        !_busy && !string.IsNullOrWhiteSpace(_title) && !string.IsNullOrWhiteSpace(_description);

    private async Task SubmitAsync()
    {
        if (!CanSubmit) return;

        _busy = true;
        _error = null;

        var report = new FeedbackReport
        {
            Product = FeedbackProduct.Installer,
            ReportType = _reportType,
            Title = _title,
            Description = _description,
            Email = string.IsNullOrWhiteSpace(_email) ? null : _email,

            // 2.x does not send screenshots and this slice does not start. The log tail belongs to
            // an install-failure report, which this screen does not have -- attaching an unrelated
            // tail to a general comment is worse than attaching nothing.
            ScreenshotPng = null,
            LogTail = null,

            AppVersion = AppVersion,
            Os = RuntimeInformation.OSDescription,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        // SubmitAsync never throws except on real cancellation; failures arrive as Success == false
        // with a message meant to be shown directly.
        var result = await Feedback.SubmitAsync(report);

        _busy = false;

        if (result.Success)
        {
            _issueUrl = result.IssueUrl;
        }
        else
        {
            // Do NOT close: the dialog holds the only copy of what the user just typed.
            _error = result.ErrorMessage ?? "The report could not be sent.";
        }
    }

    private async Task CloseAsync()
    {
        _error = null;
        _issueUrl = null;
        _title = string.Empty;
        _description = string.Empty;
        _email = string.Empty;
        await OnClose.InvokeAsync();
    }

    private static string AppVersion
    {
        get
        {
            var raw = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "unknown";
            var plus = raw.IndexOf('+');
            return plus < 0 ? raw : raw[..plus];
        }
    }
}
```

- [ ] **Step 4: Register the service**

In `Program.cs`, add the using and the registration next to the other singletons (mirroring how
2.x registers it in `App.axaml.cs`):

```csharp
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
```

```csharp
// Posts to the Cloudflare Worker relay, which files the GitHub issue. Same relay the 2.x
// installer uses; the service itself ships in SDK.Shared, already referenced.
builder.Services.AddSingleton<IFeedbackReportingService>(_ => new FeedbackReportingService(
    new FeedbackReportingServiceOptions
    {
        RelayUrl = "https://diffusionnexus-feedback-relay.diffusionnexus.workers.dev"
    }));
```

- [ ] **Step 5: Host the dialog on both screens**

In `Welcome.razor`, change `<TopBar />` to wire the callback and add the dialog at the end of the
markup:

```razor
<TopBar OnFeedback="() => _feedbackOpen = true" />
```

```razor
<FeedbackDialog Visible="_feedbackOpen" OnClose="() => _feedbackOpen = false" />
```

and add to its `@code` block:

```csharp
private bool _feedbackOpen;
```

Make the same three changes in `SoftwareWorkloads.razor`.

- [ ] **Step 6: Add the styles**

Append to `wwwroot/app.css`. `.modal-backdrop` already exists at line 581 (from `PromptModal`) —
**do not redefine it**, the dialog reuses it. Add only the `.feedback-*` rules:

```css
/* ---------- Feedback dialog ---------- */

.feedback-dialog {
    display: flex;
    flex-direction: column;
    gap: 8px;
    width: min(520px, 92vw);
    max-height: 88vh;
    overflow-y: auto;
    padding: 22px;
    border-radius: 12px;
    border: 1px solid var(--border);
    background: var(--bg);
    color: var(--text);
}

.feedback-dialog h3 {
    margin: 0 0 6px;
    font-size: 17px;
    font-weight: 700;
}

.feedback-dialog label {
    font-size: 12px;
    font-weight: 600;
    color: var(--muted);
}

.feedback-dialog input,
.feedback-dialog textarea,
.feedback-dialog select {
    width: 100%;
    max-width: 100%;
    padding: 8px 10px;
    border-radius: 8px;
    border: 1px solid var(--border);
    background: var(--bg);
    color: var(--text);
    font-family: inherit;
    font-size: 13px;
}

.feedback-actions {
    display: flex;
    justify-content: flex-end;
    gap: 9px;
    margin-top: 10px;
}

.feedback-submit,
.feedback-cancel {
    padding: 7px 16px;
    border-radius: 8px;
    font-size: 13px;
    font-weight: 600;
    cursor: pointer;
    border: 1px solid var(--border);
    background: var(--panel);
    color: var(--text);
}

.feedback-submit {
    border-color: var(--accent);
    background: rgba(31, 184, 166, 0.16);
}

.feedback-submit:disabled,
.feedback-cancel:disabled {
    opacity: 0.5;
    cursor: default;
}

.feedback-error {
    margin: 4px 0 0;
    font-size: 12.5px;
    color: #ff8f8f;
}

.feedback-success {
    margin: 4px 0 0;
    font-size: 13px;
}

.feedback-issue {
    font-family: Consolas, "Courier New", monospace;
    font-size: 12px;
    color: var(--accent);
    overflow-wrap: anywhere;
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test -c Release`
Expected: PASS — the whole suite, including six new feedback tests. If
`DependencyInjectionTests` asserts on the registered service set, update it to include
`IFeedbackReportingService`.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat(welcome): feedback dialog

UI only: IFeedbackReportingService already ships in SDK.Shared, which this app
references, and posts to the same Cloudflare Worker relay the 2.x installer
uses. No backend, no token.

A failed submit keeps the dialog open and shows the relay's own message, because
the dialog holds the only copy of what the user just typed. Screenshots and log
tails stay unattached: 2.x sends neither, and an unrelated log tail on a general
comment is worse than none.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 10: Refresh the catalog seed, update the smoke checklist, open the PR

**Files:**
- Modify: `DiffusionNexus.Installer.Electron/Assets/Catalog/catalog.zip`
- Modify: `DiffusionNexus.Installer.Electron/Assets/Catalog/manifest.json`
- Modify: `docs/manual-smoke.md`

- [ ] **Step 1: Verify the staleness before changing anything**

```bash
python -c "
import zipfile, json, collections
z = zipfile.ZipFile('DiffusionNexus.Installer.Electron/Assets/Catalog/catalog.zip')
c = collections.Counter()
for n in z.namelist():
    if n.endswith('workload.json'):
        c[json.loads(z.read(n).decode('utf-8')).get('workflowType')] += 1
print('seed:', dict(c))
"
```

Expected before the refresh: `{'Image': 21, 'Video': 4}` — no Audio.

- [ ] **Step 2: Regenerate the seed from the current catalog**

The catalog repo is checked out at `E:\Repos\DiffusionNexus.Catalog`. Use the `dn-catalog` tool
the SDK ships to produce `catalog.zip` + `manifest.json`, exactly as the existing pair was
produced, and copy both over `DiffusionNexus.Installer.Electron/Assets/Catalog/`.

Find the tool's pack verb first:

```bash
dn-catalog --help
```

If the tool is not on PATH, run it from the SDK checkout:
`dotnet run --project E:\Repos\DiffusionNexus.Installer.SDK\DiffusionNexus.Installer.SDK.Catalog.Tool -- --help`

- [ ] **Step 3: Verify the refresh landed**

Re-run the Step 1 command. Expected after: `{'Image': 20, 'Video': 4, 'Audio': 1}`.

Also confirm the manifest's `commit` and `generatedAt` changed from `8dcff119…` / `2026-08-22`,
and that `archive.sha256` and `archive.bytes` match the new zip — a stale hash makes the SDK
reject the seed at load.

- [ ] **Step 4: Update the smoke checklist**

In `docs/manual-smoke.md`:

1. Retitle from "(slice 1)" to cover the welcome screen.
2. In §1.3, remove the note that the seed predates the Audio workflow type and replace it with:

> The embedded seed now carries ACE-Step-1.5 as Audio. Note that no Audio filter appears on the
> ComfyUI workload screen, and that is correct — the only Audio workload belongs to ACE-Step,
> which is a single-workload software and goes straight to setup. The filter is catalog-derived,
> so an Audio button appears there by itself the day a ComfyUI workload declares it.

3. Add a new section for this slice:

```markdown
## 6. Welcome screen

1. Launch. **Expect:** a top bar with the version on the left and Feedback, Licences,
   Check for Updates on the right (plus Developer tools in a Debug build only); the Into The
   Latent banner as a wide strip, not a 16:9 block; "Easy Workload Installer" in the gradient
   wordmark; six software cards with their logos; and a "Join the Community" footer with
   YouTube, Patreon and Civitai.
   **Look at the banner crop specifically** — it is cropped from a 16:9 source and the portal
   ring at the bottom may clip.
2. **Expect:** the ComfyUI card reads "16 workloads"; the other five read "straight to setup".
3. Click a community link. **Expect:** it opens in your normal browser. The installer window must
   NOT navigate to it — if the app itself turns into a web page, that is the bug this was written
   to catch.
4. Click Licences, then come back. Click Check for Updates, then come back. **Expect:** both pages
   still work from the top bar.
5. Click ComfyUI. **Expect:** the Select workload screen, with 16 cards showing their artwork,
   an "All / Image / Video" filter and no software filter. Filter to Video. **Expect:** four
   cards. Click "← All software", then pick ComfyUI again. **Expect:** the filter is back on All.
6. Click Fooocus. **Expect:** the wizard opens directly — no workload screen.
7. Navigate to `/software/Nonsense` by hand. **Expect:** "That software is not in the catalog"
   and a link back, not an error page.
8. Click Feedback, send a report with a summary and details. **Expect:** a GitHub issue URL comes
   back. Check the issue exists in the Feedback repo and is labelled as coming from the installer.
9. Disconnect from the network and click Feedback again. **Expect:** the dialog stays open, shows
   the failure reason, and your typed text is still there.
```

- [ ] **Step 5: Run the full suite and the package-only build**

```bash
dotnet test -c Release
dotnet build -c Release -p:UseLocalSDK=false
```

Both must succeed. The second is the one CI enforces — a local build with the SDK checkout
present cannot prove the package references are complete.

- [ ] **Step 6: Check for CRLF flips before pushing**

```bash
git diff --numstat > /tmp/with-ws.txt
git diff --numstat -w > /tmp/without-ws.txt
diff /tmp/with-ws.txt /tmp/without-ws.txt
```

Any file with a large count in the first and none in the second was rewritten LF-only. Restore it
with `git checkout -- <file>` and redo the edit preserving line endings.

- [ ] **Step 7: Commit and push**

```bash
git add -A
git commit -m "$(cat <<'EOF'
fix(catalog): refresh the embedded seed, and document the welcome screen smoke

The seed was generated 2026-08-22 from commit 8dcff119, before ACE-Step-1.5
became an Audio workload, so a cold start with no installed catalog typed it as
Image. Regenerated from the current catalog: Image 20, Video 4, Audio 1.

Be clear about what this does not do: the type filter lives on the workload
screen, and the only Audio workload belongs to a single-workload software that
skips that screen, so no Audio button appears anywhere yet. The filter is
catalog-derived and will show one the day a ComfyUI workload declares Audio.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"

gh auth switch --user Into-The-Latent
git push -u origin feature/welcome-screen
gh auth switch --user Little-God1983
```

The account switch is required: this repo's `.git/config` routes credentials through
`gh auth git-credential`, and the default active account `Little-God1983` gets a 403 on
`Into-The-Latent`.

- [ ] **Step 8: Open the PR**

```bash
gh auth switch --user Into-The-Latent
gh pr create --repo Into-The-Latent/DiffusionNexus.Installer \
  --base main --head feature/welcome-screen \
  --title "Welcome screen and workload selection" \
  --body "$(cat <<'EOF'
Brings back the 2.x welcome screen. Spec: `docs/superpowers/specs/2026-09-13-welcome-screen-design.md`.

- Top bar with version, Feedback, Licences, Developer tools (Debug only) and Check for Updates, replacing the gallery's footer link row.
- The Into The Latent banner, moved into `wwwroot` and downscaled, cropped to the 2.x strip ratio with `object-fit` so the original stays intact.
- Software cards derived from the catalog. A software with several workloads opens a Select workload screen; the five with one workload go straight to `/install/{id}`.
- Workload cards finally render the `thumbnail.webp` the catalog has shipped for 23 of 25 workloads, through a new id-keyed `/thumbnail/{id:guid}` endpoint — `ThumbnailPath` is an absolute disk path no browser can load.
- Feedback dialog over `IFeedbackReportingService`, already in `SDK.Shared` and already referenced. UI only; no backend, no token.
- Community footer with the 2.x three, opened through the Electron shell rather than the app window. Making them editable without a release is #6.
- Embedded catalog seed regenerated: it predated the Audio workflow type and typed ACE-Step-1.5 as Image on a cold start.

### Owed before merge

- Manual smoke §6 in `docs/manual-smoke.md`, especially the banner crop and that community links open in the real browser.
- Slice 2's smoke (§2.2, §2.6–2.8, §3.6–3.7) is still owed separately and unrelated to this PR.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
gh auth switch --user Little-God1983
```

---

## Self-Review

**Spec coverage** — §3.1 welcome screen → Tasks 4, 5, 7. §3.2 workload screen → Tasks 6, 8.
§3.3 the Gallery split → Task 7 Step 5. §4.1 banner → Task 1. §4.2 logos → Task 1. §4.3
thumbnails → Task 2, consumed in Task 6. §5 top bar → Task 4. §6 feedback → Task 9. §7 community
links → Task 5. §8 seed refresh → Task 10. §9 testing → every task's Step 1. §10 risks: the
banner crop is a manual-smoke step in Task 10 Step 4; the Gallery split is pinned by Task 7's
diagnostics and empty-catalog tests; back-navigation filter state is pinned by
`SoftwareWorkloads.OnParametersSetAsync` resetting `_type`, asserted in Task 8.

**Known follow-up, deliberately not in this plan:** `docs/manual-smoke.md` §2's existing steps
still describe reaching workloads through the old single gallery. Task 10 adds §6 rather than
rewriting §2, because §2's slice-2 steps have never been run and rewriting an unrun checklist
loses the record of what is owed. Whoever runs the slice-2 smoke should reconcile them.
