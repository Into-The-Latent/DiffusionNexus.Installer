# Welcome screen and workload selection — design

Date: 2026-09-13
Branch: `feature/welcome-screen`
Slice: 3 (follows slice 1 — catalog-driven wizard, slice 2 — Content stage)

## 1. Problem

The 3.x installer's first screen is an unstyled list. It renders `<h1>Choose a workload</h1>` over
25 text-only cards, with the version, licences and developer-tools links exiled to a footer line
and no branding anywhere. The 2.x Avalonia installer it replaces opened with a banner, a title, a
pair of large choice cards and a community footer. Side by side the new app looks worse than the
one it supersedes, which is the user's verdict and the reason for this slice:

> "visually the electron installer UI is shit. It looks way worse than the Avalonia one."

Three things are missing rather than merely unstyled:

- **No branding.** `wwwroot/` contains exactly one file, `app.css`. The app has never shipped an
  image.
- **No Feedback.** The word does not appear in any `.cs`, `.razor`, `.css` or `.md` in the repo.
  2.x has had a feedback dialog since SDK 1.2.x.
- **No artwork on cards**, despite 23 of the 25 catalog workloads already shipping a
  `thumbnail.webp` that the app fetches and discards.

## 2. Goals

1. A welcome screen that reads as the same product as the 2.x installer: top bar, banner, title,
   software selection, community footer.
2. Software selection replaces the 2.x Classic/Wizard mode picker, which has no 3.x equivalent —
   slice 1 ruled the Classic page out of scope permanently.
3. A second screen selects the workload, as the 2.x wizard did, but only where there is a choice
   to make.
4. Workload cards show the artwork the catalog already ships.
5. Licences and Developer tools move into the top bar.
6. Feedback returns.

### Non-goals

- Remote/editable community links. Hardcoded to the 2.x three for now; the remote source and
  whether it belongs in the SDK is [issue #6](https://github.com/Into-The-Latent/DiffusionNexus.Installer/issues/6).
- Any change to the wizard stages themselves. This slice ends where `/install/{id}` begins.
- The shortcut-launch bug and code signing. Still open, still release blockers, unrelated to this.
- Persisting the last-used software or workload. User settings are still write-only for
  `ComfyFoldersModule`; nothing else persists and this slice does not change that.

## 3. Screens

### 3.1 Welcome (`/`)

Replaces today's `Gallery.razor` at the same route. Five regions, top to bottom:

| # | Region | Content |
|---|--------|---------|
| 1 | Top bar | Version (left); Feedback, Licences, Developer tools, Check for Updates (right) |
| 2 | Banner | `Banner.png`, cropped to a strip |
| 3 | Title | "Easy Workload Installer", gradient wordmark |
| 4 | Software | One card per distinct `Repository.Type` in the catalog |
| 5 | Community | "Join the Community" — YouTube, Patreon, Civitai |

Software cards are catalog-derived, not a hardcoded list of six, for the same reason the existing
filters are: a card for a software the catalog does not contain is a dead control. Today that
yields six.

Each card shows the software's logo, its name, and a subtitle that depends on how many workloads
sit behind it:

- More than one → "N workloads", and the card navigates to `/software/{type}`.
- Exactly one → "straight to setup", and the card navigates directly to `/install/{workloadId}`,
  skipping a screen that would offer a choice of one.

The counts today are ComfyUI 16 and one each for A1111, Forge, Fooocus, AI-Toolkit and ACE-Step
(21 offerable of 25 catalogued — the other 4 target DiffusionNexusCore and are never offered here),
so five of the six cards take the direct path. This is a property of the catalog, not a
hardcoded special case — a second Fooocus workload would give Fooocus a selection screen with no
code change.

**Uninstallable workloads do not disappear from the count.** A software whose only workload is
blocked (Config535's torch/CUDA pairing, say) still shows its card, still navigates, and the
disabled state with its reason appears on the workload screen or, for the single-workload case,
as a disabled card on the welcome screen itself. Silently hiding a software would make an
already-confusing "why is this missing" question unanswerable.

### 3.2 Select workload (`/software/{type}`)

Reached only from a multi-workload software card.

- A back control to `/`, and the software's name.
- The **type** filter — All plus one button per `WorkflowType` present *in this software's
  workloads*. The software filter is gone: the previous screen already answered it.
- Workload cards: thumbnail, name, type badge, and either an Install action or the disabled
  reason, exactly as today's gallery cards behave.

An unknown or absent `{type}` route value renders a "software not found" state with a link back to
`/`, rather than an empty grid.

### 3.3 What happens to `Gallery.razor`

It is split, not extended. Today it holds catalog loading, error/diagnostic rendering, two
filters, card rendering and the footer in 168 lines. After the split:

- `Welcome.razor` — regions 1–5 above.
- `SoftwareWorkloads.razor` — §3.2.
- `WorkloadCard.razor` — the card the workload screen renders for each workload. Not shared with
  the welcome screen: `Welcome.razor` inlines its own software-card markup, including the
  single-workload disabled state.

Catalog loading, the diagnostics-before-empty-message rule and the `_loadError` guard move into a
shared component or base so that **both** screens keep today's behaviour: a hard catalog failure
must say why it is empty rather than take the app down from the component lifecycle. That rule was
written for the first screen; both screens are now first screens depending on where the user
lands.

## 4. Assets

### 4.1 Banner

`Banner.png` currently sits untracked at `DiffusionNexus.Installer.Electron/Banner.png`, where
nothing serves it. It is 2688×1512 (**16:9**), 3.3 MB. The 2.x banner it replaces is
`Banner-cut.png`, 2687×702 (**3.83:1**) — the same artwork, pre-cropped.

- Moves to `wwwroot/img/banner.png`, served by the existing `app.MapStaticAssets()`.
- Downscaled to 1600 px wide on the way in — roughly 2× the widest the strip is displayed at, so
  it stays sharp on a HiDPI screen. 3.3 MB is far past what a strip needs, and static assets are
  fetched over the same connection the app runs on.
- Rendered into a **3.83 : 1** box — the 2.x strip ratio — with
  `object-fit: cover; object-position: center`, rather than re-cropped on disk, so the strip
  height is a one-line change and the original stays intact.

### 4.2 Software logos

Six PNGs already exist in the 2.x repo at
`DiffusionNexus.Installers/DiffusionNexus.WizardUI/Assets/` — `ComfyUI-logo.png`,
`Automatic-Logo.png`, `ForgeUI-Logo.png`, `Fooocus.png`, `AI-Toolkit-Logo.png`, `ACE-Step.png`.
Copied into `wwwroot/img/software/`, keyed by `RepositoryType`.

A `RepositoryType` with no logo file renders a neutral tile bearing the software's name. The map
must not throw or render a broken image for a software the catalog adds later.

### 4.3 Workload thumbnails

`InstallationConfiguration.ThumbnailPath` is an **absolute path on disk** —
`Path.Combine(workloadDirectory, "thumbnail.webp")`, under `%LocalAppData%\DiffusionNexus\catalog`
or a local override. A browser cannot load an absolute Windows path, so the markup cannot point at
it.

The SDK already exposes the right seam: `ICatalog.ReadThumbnailAsync(Guid workloadId)` returns the
bytes or null. A minimal endpoint maps onto it:

```
GET /thumbnail/{workloadId:guid}  ->  200 image/webp | 404
```

Keyed by workload id, so there is no caller-supplied path and no traversal surface. Registered
alongside `MapStaticAssets()` in `Program.cs`. A null result is a 404 and the card falls back to
the neutral tile — the two workloads with `"thumbnail": null` take that path, as does any workload
whose thumbnail is missing on disk (the catalog reader already nulls `ThumbnailPath` and raises
`CAT033` in that case).

Content type is derived from the thumbnail's own extension rather than assumed to be webp: the
catalog writer emits `thumbnail.<ext>` preserving the source extension, so png and jpg are
possible. Responses carry a cache header; the catalog is immutable between updates and 20+ cards
re-fetching on every render is waste.

## 5. Top bar

| Control | Behaviour |
|---------|-----------|
| Version | `AssemblyInformationalVersionAttribute`, as `Home.razor` already reads it |
| Feedback | Opens the feedback dialog (§6) |
| Licences | Navigates to the existing `/licenses` page |
| Developer tools | Navigates to the existing `/debug` page |
| Check for Updates | Navigates to the existing `/updates` page |

Licences and Developer tools move out of `Gallery.razor`'s footer link row, which disappears with
the page. "Version and updates" becomes the Check for Updates control.

Developer tools keeps its existing guard verbatim: the page is compiled out of Release builds by
the csproj, and the link is gated on a `const bool` set under `#if DEBUG` inside `@code`, because
preprocessor directives are not legal in Razor markup. Moving the link must not quietly drop that
guard — a Release build that links to a page that does not exist is a 404 in the user's face.

The bar renders on the welcome screen and the workload screen. It is not a global layout
component: `/install/{id}` deliberately does not get it, because a Feedback dialog or a navigation
away mid-install is not a thing to offer while a workload is being installed.

## 6. Feedback

Cheaper than it appears. `IFeedbackReportingService` and `FeedbackReportingService` live in
`DiffusionNexus.Installer.SDK.Shared`, which the Electron app **already references** at
`2.0.0-preview.4`. The service posts to the existing Cloudflare Worker relay, which creates the
GitHub issue. This slice adds UI and a DI registration; no backend, no token, no new dependency.

- `FeedbackDialog.razor`, styled as the existing `PromptModal` / `MismatchModal` are, submitting a
  `FeedbackReport`.
- `Product` is `FeedbackProduct.Installer`.
- `ReportType` is chosen by the user from Feedback / Bug / Feature request.
- `AppVersion` from the same assembly attribute the top bar shows; `Os` from
  `RuntimeInformation.OSDescription`; `TimestampUtc` at submit.
- `ScreenshotPng` stays null. 2.x does not send screenshots and this slice does not start.
- `LogTail` stays null on this screen. The welcome screen has no install log, and attaching an
  unrelated tail to a general comment is worse than attaching nothing. Wiring the tail into an
  install-failure report is a separate change to the Install stage, out of scope here.

`SubmitAsync` never throws except on real cancellation; failures come back as
`Success == false` with an `ErrorMessage` the dialog shows directly. The dialog must render that
message rather than a generic failure, and must not close on failure — the user's typed text is
the thing being lost.

The relay URL is registered in DI exactly as 2.x does it, as a
`FeedbackReportingServiceOptions.RelayUrl` constant.

## 7. Community links

Three links, hardcoded, matching 2.x verbatim:

```
YouTube  https://www.youtube.com/@IntoTheLatent
Patreon  https://patreon.com/AIKnowledgeCentral
Civitai  https://civitai.com/user/AIknowlege2go
```

**They must open in the system browser, never in the app window.** A plain
`<a href="https://...">` inside Electron navigates the app's own window to YouTube, stranding the
user in an installer that is now a web browser with no address bar. Clicks call
`Electron.Shell.OpenExternalAsync(url)` when `HybridSupport.IsElectronActive`, and fall back to a
normal `target="_blank"` anchor when the app is running as a plain web app (which the `/updates`
page already distinguishes for the updater).

Making the list editable without a release is [issue #6](https://github.com/Into-The-Latent/DiffusionNexus.Installer/issues/6),
which also records that the 2.x Civitai link points at the pre-rebrand `AIknowlege2go` handle
while the Linktree points at `IntoTheLatent`.

Why not read the Linktree, recorded so the question is not reopened: `linktr.ee` serves
`x-frame-options: SAMEORIGIN`, so it cannot be framed; and scraping its `__NEXT_DATA__` payload
for `linktr.ee/intothelatent` yields 31 rows of which 5 are ours and 26 are Linktree's own
sponsored placements (Hulu, HelloFresh, Acorns, Curology, AAA, Headspace, Babbel, …) with no field
distinguishing them.

## 8. Catalog seed refresh

Independent of the UI, included here because it is the other half of the user's report.

The workflow-type filter is already correct: `AvailableTypes()` derives its buttons from the
loaded catalog, so it renders an Audio button the moment the data contains one. What is stale is
the catalog compiled into the app — `Electron/Assets/Catalog/catalog.zip`, generated 2026-08-22
from commit `8dcff119`, which still types ACE-Step-1.5 as `Image`:

```
seed  (Assets/Catalog, 22 Aug)   Image 21 · Video 4 · Audio 0
live  (catalog repo, today)      Image 20 · Video 4 · Audio 1
```

On a cold start with no installed catalog the seed wins and Audio does not exist. The fix is to
regenerate the seed from the current catalog. No filter code changes.

**Be honest about what the refresh does and does not fix.** After this redesign the type filter
lives only on the workload screen (§3.2), and the sole Audio workload belongs to AceStep, a
single-workload software that takes the direct-to-setup path and therefore has no workload screen.
So a fresh seed does not make an Audio filter appear anywhere. What it fixes is the underlying
data: ACE-Step-1.5 stops being mislabelled `Image` on a cold start, and the filter — already
catalog-derived — will show Audio by itself the day a ComfyUI workload declares it. The user has
accepted that this is hidden for now and self-resolving.

`docs/manual-smoke.md` §1.3 carries a note to re-check the Audio filter "once an Audio-tagged
workload ships in the embedded catalog". The seed refresh is that moment, so the note is
discharged and the step rewritten to state that no Audio filter is expected under ComfyUI and why.

### 8.1 Correction: the refresh does not reach an existing install

Added after review. The section above, and commit `8ad4db6`'s message, say the refresh fixes the
ACE-Step typing. That is true **only on a cold start with no installed catalog**. It is not true for
anyone who has already run a 3.x build, and they are the majority.

`CatalogLocator.StaleSections` re-seeds from the embedded archive only when the embedded
`catalogVersion` is strictly greater than the installed one:

```csharp
if (embeddedVersion > (state.Workloads?.CatalogVersion ?? 0)) stale |= CatalogSections.Workloads;
```

The regenerated `manifest.json` changed `commit`, `archive.sha256` and ACE-Step-1.5's
`subVersion`/`hash`, but left `catalogVersion` at `1`. An existing
`%LocalAppData%\DiffusionNexus\catalog` also says `1`, `1 > 1` is false, and `SeedFromEmbedded`
never runs. `docs/manual-smoke.md` §1.1 tells the tester to delete that folder, so the smoke pass
could not have seen it either — §1.4 now exists to exercise the upgrade path deliberately.

Cold starts are not safe either, for a different reason — §8.2.

### 8.2 The seed is ahead of the tag, and that undoes the fix where it did land

Worse than the upgrader gap, and true **today** with nothing bumped. The embedded seed is packed
from catalog commit `3847a24`, which is **newer than the `v1` tag** (`8dcff11`) — and `v1` is what
the stable channel serves. At `v1`, `workloads/ace-step-1-5/workload.json` still says
`"workflowType": "Image"`.

So a machine that cold-starts, gets `Audio` from the seed, and then runs its first update check is
offered that older remote copy and takes it: `CatalogDiff.Compute` is item-hash-level and
deliberately version-agnostic ("a changed hash counts as Updated even when the author forgot to bump
the version"), so a *differing* hash is an update regardless of direction. Applying it reverts
ACE-Step-1.5 to `Image`.

Tagging catalog `v2` is therefore not a nice-to-have for upgraders. It is what stops the fix being
undone on the machines that did receive it.

### 8.3 Why bumping `catalogVersion` in the seed was rejected

Not, as an earlier draft of this section claimed, because it would freeze remote updates.
`CatalogUpdateService.cs:84`'s `remote.CatalogVersion > local` test is only one arm of an `||`; the
other is the hash diff above, which ignores versions entirely. A local `2` against a remote `1`
would not block updates.

It was rejected for two concrete reasons:

1. **Reseed/update ping-pong.** `CatalogUpdateService.cs:132` stamps
   `new SectionState(check.Remote.CatalogVersion, ...)` when an apply succeeds, so applying the
   remote v1 writes the installed state back down to `1`. `CatalogLocator.cs:79` then sees embedded
   `2 > 1` and re-seeds on the next launch; the next check offers v1 again. Every apply undoes the
   ACE-Step fix, forever, and the machine oscillates.
2. **It claims a version that does not exist.** On the stable channel the number comes from the
   catalog repo's git tag (`VERSION=${GITHUB_REF_NAME#v}` in its `release.yml`), and no `v2` has
   been cut. A seed asserting `2` is asserting a release nobody can fetch.

**The real fix lives in another repo and is a release action, not a code change here:** tag `v2` in
`Into-The-Latent/DiffusionNexus.Catalog` — which publishes the content the seed was already packed
from, resolving §8.2 at the same time — then regenerate the embedded seed with `--version 2`. Until
then, upgraders keep ACE-Step-1.5 typed `Image` and cold starts lose it again at their first update
check. Nothing in the UI shows that type today (no Audio filter exists on any reachable screen —
§8 above), so the visible cost of the wait is zero.

## 9. Testing

bUnit 2.8.6 is already in the test project, with existing component tests under `Tests/Components`
and `Tests/Gallery`.

**Welcome screen**
- Software cards are derived from the catalog, not a fixed list.
- A software with >1 workload links to `/software/{type}`; a software with exactly 1 links
  straight to `/install/{id}`.
- A software whose only workload is uninstallable still renders, disabled, with its reason.
- Catalog load failure renders the diagnostics, not an empty grid (the existing rule, re-asserted
  on the new page).
- The Developer tools control is absent in a Release-configuration compile.

**Workload screen**
- Type filter offers only the types present in that software's workloads.
- An unknown `{type}` renders the not-found state.
- Cards render the thumbnail endpoint URL, and fall back to the neutral tile when the workload
  has no thumbnail.

**Thumbnail endpoint**
- Known id with bytes → 200 and the right content type for the extension.
- Known id with no thumbnail → 404.
- Unknown id → 404.

**Feedback**
- A successful submit closes the dialog and shows the issue URL.
- A failed submit keeps the dialog open and renders `ErrorMessage` verbatim.
- Required fields are enforced before submit is enabled.

**Community links**
- Outside Electron, links render as anchors with `target="_blank"`.
- Link clicks never produce same-window navigation.

**Not covered by tests, owed as manual smoke:** that the banner crop looks right, that logos load,
and that `OpenExternalAsync` actually opens the system browser — none of which a headless test can
prove. These go into `docs/manual-smoke.md` as a new section.

## 10. Risks

**The banner crop.** 16:9 cropped to 3.83:1 keeps the middle 46% of the image. The portal ring
under the figure sits near the bottom of the frame and may clip. Mitigated by `object-position`
being tunable and the original file staying uncropped, but it needs a human to look at it.

> **This risk landed.** At 3.83:1 the retained band runs from 27% to 73% of the image's height,
> which clips the runner's head, the portal ring beneath it, and the top and bottom of the
> wordmark. §12 replaces the crop rather than tuning `object-position` around it.

**`Gallery.razor` splitting into three files** is the largest behavioural risk in the slice: the
catalog-failure and diagnostics rules were written once for one page and now apply to two. The
tests above pin them on both.

**Two screens where there was one** means the back path matters. Reaching `/software/{type}`,
going back, and picking a different software must not leave filter state from the first visit.

## 11. Out-of-scope carry-ins, unchanged

- Start Menu shortcut launches the packaged exe and it exits instantly — shipped in v3.0.5.
- No code signing.
- Slice 2's manual smoke (§2.2, §2.6–2.8, §3.6–3.7) has never been run, and slice 2 has never
  been released — the latest public build is still v3.0.5, which is slice 1.
- User settings are read but only `ComfyFoldersModule` writes them.

## 12. Revision, 2026-09-14: one strip, and a hero on the first stage

The screen above shipped and works, but two things were wrong with it in use.

### 12.1 It did not fit a 16:9 window

The six software cards were a `repeat(auto-fill, minmax(220px, 1fr))` grid, so at any realistic
width they wrapped to a second row and pushed "Join the Community" — the whole reason the footer
exists — below the fold. The banner made it worse: a 16:9 image stretched across a 1000px column
and cropped to a strip is still 260px tall, a third of the height budget, for artwork that is
decoration.

**The strip.** One row of fixed-width (200px) tiles in a horizontal scroll container, with `<` and
`>` buttons in grid columns either side of it. Fixed width so a page is a whole number of tiles and
the scroll-snap points line up; arrows beside the track rather than floated over it, because an
overlay covers the artwork of the first and last visible tile, which is what the tile is for.

The track is an ordinary scroll container, so a wheel, a trackpad and the keyboard all move it
whether or not the buttons work. The buttons drive it through `wwwroot/js/jukebox.js` — this app's
first JavaScript — whose two functions scroll one page and report whether the strip is against
either end, so an arrow that can do nothing is disabled rather than merely inert. `step` reports
the edges of the position it scrolls **to**, not the one it leaves: `scrollTo` with smooth
behaviour returns long before the scroll lands, so reading `scrollLeft` afterwards would leave the
buttons one click behind.

**The banner** is capped by width at a 3.4:1 crop rather than stretched and cropped to 3.83:1.
3.4:1 is the ratio at which the whole logo survives — see the note under §10 — and the width cap is
what turns 260px of height into 141px without cropping harder. `object-position: center 54%` keeps
the portal ring in frame.

### 12.1.1 Correction: the first version fitted 720p and then stopped

It was built to a fixed size — 200px tiles, a 480px banner, 28px type, a 1240px column — so a
maximised 1938×1010 window rendered a 720p screen marooned in the middle of it, with dead space
down both sides and across the whole bottom half. Fitting the smallest supported window is not the
same as fitting every window, and this only did the first.

Everything is now sized from the window. `--tile` on `.welcome` is the keystone — the artwork is
square, so the tile width sets the height of the entire strip — and the banner, the type and the
gaps scale alongside it or the composition comes apart at the extremes:

| | floor | tracks the window | ceiling |
|---|---|---|---|
| `--tile` | 190px | 14vw | 264px |
| banner width | 440px | 38vw | 820px |
| title | 26px | 2.6vw | 46px |
| vertical gap | 12px | 1.8vh | 26px |

The tile ceiling is arithmetic, not taste: the column caps at 1800px, of which 136px goes to
padding, arrows and their gaps, leaving 1664px of track; six tiles and five 14px gaps fit that at
264px each. Larger, and the widest window overflows a strip that had room to spare.

`.screen` takes `min-height: 100vh` so a screen can fill the window at all, and `.welcome` takes
that height with `flex: 1` and **centres** its content — leftover height on a tall window belongs
evenly above and below, not all of it underneath. The column widens to 1800px: at 1240px the six
tiles could never all be on screen however large the window got.

Measured against the committed stylesheet with the components' own markup:

| window | six tiles fit? | content bottom |
|---|---|---|
| 2560×1380 | yes | 1071 |
| 1938×1010 | yes | 863 |
| 1600×830 | yes | 727 |
| 1280×688 | no — 119px over, arrows live | 639 |
| 900×620 (hard minimum) | no — 499px over | 619 |

Everything is on screen at every size, including the 900×650 minimum the earlier version
overflowed by 53px. The minimum window still shows ~13px of scroll for the bottom padding alone.

**The strip also opened scrolled to its far end**, slicing the first tile in half — visible in the
packaged app, never in a headless repro, because it needs contents that settle after first layout.
The cause was `scroll-snap-type: x mandatory` re-snapping on its own. Snapping bought nothing here
(the buttons scroll by a computed page, not by nudging toward a snap point) so it is gone, and
`observe` now explicitly opens the strip at zero. Centring the tiles uses `justify-content: safe
center`: plain `center` pushes the first tile out past the scroll container's start edge, where
nothing can bring it back.

### 12.2 Nothing confirmed what the user had picked

Five of the six softwares go straight to the wizard, so the welcome tile was the only thing the
user ever saw before being asked where to install. Someone who misclicks has no way to notice.

A **hero** now opens the wizard's first stage: the same tile artwork at 200px, the workload's name
beside it, and the catalog's own description under that — directly above the install-location
panel that was already there. No new route: this is the `Location` stage, which those five
softwares already land on.

Shown only when the software has exactly one offerable workload, counted over the same list the
welcome screen groups into cards. ComfyUI is excluded because its workload screen has already
shown that workload's card, thumbnail and description one navigation ago. It is also first stage
only — past that, the question it answers has been answered, and repeating it would push each
stage's actual question down the page.

The software's name appears above the workload's only when they differ: A1111's single workload is
"Stable Diffusion web UI" under a tile labelled "Automatic 1111", and the eyebrow is the only thing
connecting the two.

### 12.3 Descriptions are Markdown, and nothing rendered it

The catalog authors descriptions in Markdown; before this they appeared only in a `title`
tooltip, where the raw `**` did not matter. On the hero they do.

The subset was counted across all 25 `workload.json` files rather than assumed: bold (22 files),
bullet lists (13), italic (4), inline code (5), and **nothing else** — no links, headings, numbered
lists, blockquotes, tables or raw HTML — with the longest description at 659 characters.

`Core/Text/DescriptionMarkdown.cs` parses exactly that into blocks, and
`Components/Shared/MarkdownText.razor` renders them as elements through `AddContent`, which
escapes. Markdig was the alternative and was rejected on three counts: a new dependency, the
`THIRD-PARTY-NOTICES.txt` regeneration and CI gate that comes with one, and an HTML string the UI
would have to trust. Catalog content becoming markup the browser executes is the failure mode
worth designing out, and a tree of blocks cannot reach it.

Anything outside the subset degrades to the literal characters the author typed. That is the
contract: a description is content, and content must not disappear because the parser did not
recognise it.
