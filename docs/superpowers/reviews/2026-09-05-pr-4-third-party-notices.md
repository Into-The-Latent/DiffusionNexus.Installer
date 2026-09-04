# Code review — PR #4 `feat(licensing): ship third-party notices for the Electron installer`

- **PR:** https://github.com/Into-The-Latent/DiffusionNexus.Installer/pull/4
- **Branch:** `feature/third-party-notices` → `main` (head `282c8a3` + 3 follow-up commits)
- **Reviewed:** 2026-09-05 · 22 files, +3207 · nothing fixed, findings recorded only.

15 findings, most severe first. Nothing here undermines the *intent* of the PR — the notices
mechanism is sound — but #1 and #4 mean the shipped file is still incomplete, and #2/#6 mean the
CI gate can go red on PRs that touched nothing related.

---

## 1. Nested `node_modules` are invisible to the npm scan — `semver` ships unattributed

**`Scripts/Generate-ThirdPartyNotices.ps1:150`**

`Get-NpmInventory` walks only the top level of `node_modules` (plus one level of `@scope`
folders). `publish/app/node_modules/electron-updater/node_modules/semver` exists — semver is a
production dependency of electron-updater 6.8.9 (`"semver": "~7.7.3"`) — so electron-builder
packs it into `app.asar`, but the scan never descends into it.

Result: the committed inventory has 39 entries with no `semver`, `THIRD-PARTY-NOTICES.txt`
omits its ISC licence, and `-Check` cannot see the gap because it compares against the same
blind inventory.

**Fix:** recurse into any nested `node_modules`, and key the inventory by name+version so
duplicates collapse.

---

## 2. The notices gate will false-fail on a .NET SDK patch bump

**`.github/workflows/build.yml:49`**

`project.assets.json` carries `Microsoft.AspNetCore.App.Internal.Assets` with
`autoReferenced: true` at `[10.0.11, )` — the ASP.NET Core runtime version bundled with the
locally installed SDK. Line 17 requests `dotnet-version: '10.0.x'`.

When setup-dotnet's manifest advances to an SDK carrying ASP.NET Core 10.0.12, the generated
section-1 line becomes `MIT  Microsoft.AspNetCore.App.Internal.Assets  10.0.12`, `-Check` exits
1 with "notices are out of date", and CI blocks *every* PR until someone regenerates on a
machine with the matching SDK.

**Fix:** pin an exact SDK via `global.json`, or filter build-only `autoReferenced` /
`suppressParent=All` entries out of the inventory — this one is `suppressParent: All` and never
ships.

---

## 3. Drift comparisons are case-insensitive, so case-only changes pass the gate

**`Scripts/Generate-ThirdPartyNotices.ps1:388`** (and `:217`)

PowerShell's `-ne` is case-insensitive for strings (`'abc' -ne 'ABC'` → `False`, verified). So
`if ($committed -ne $expected)` and
`if ($npmFreshJson.TrimEnd() -ne $npmCommittedJson.TrimEnd())` both report "current" when the
only change is casing.

Real triggers: a nuspec `<copyright>` re-cased, an npm licence file re-casing its holder, a
NuGet id re-cased upstream (`SocketIOClient` → `SocketIoClient`). Stale notices ship.

**Fix:** use `-cne`.

---

## 4. The self-contained .NET runtime's own third-party notices are not reproduced

**`Scripts/license-data/supplements.json:19`**

The supplement reproduces only the MIT text for the runtime. But the csproj sets
`SelfContained=true` / `RuntimeIdentifier=win-x64`, and `publish/bin` carries 349 assemblies
plus native runtime binaries. Microsoft ships a *separate* `THIRD-PARTY-NOTICES.TXT` with the
runtime covering zlib, Brotli, the Unicode Character Database, RFC/IETF texts and more — none
of it MIT, none of it in our generated file.

We therefore redistribute code whose notice obligations are undischarged — exactly what this PR
set out to fix.

**Fix:** add the runtime pack's `THIRD-PARTY-NOTICES.TXT` (on disk after restore, in the
`Microsoft.NETCore.App` runtime pack) as a supplement text.

---

## 5. A plain generator run can silently regress the npm inventory from a stale publish

**`Scripts/Generate-ThirdPartyNotices.ps1:224`**

Non-`-Check` runs overwrite the committed inventory from whatever is in the Release publish
folder, with no check that the publish is current.

Scenario: a developer adds an npm dependency, forgets to re-publish, then runs the generator to
refresh notices after a NuGet change. `Test-Path $NpmModulesDir` is true (a weeks-old publish),
so the inventory is rewritten from the stale tree — potentially deleting correct entries — and
the notices regenerate to match. CI's `-Check` has no packaged app, so it compares against the
same regressed inventory and passes. The gap surfaces at release time, or never.

**Fix:** stamp the inventory with the publish timestamp / `package.json` hash it came from and
refuse to write when the publish predates `package.json`; or gate the refresh behind an explicit
`-RefreshNpm` switch.

---

## 6. The committed notices come from a restore graph CI never reproduces

**`docs/third-party-licenses.md:24`**

`Directory.Build.targets` auto-enables `UseLocalSDK` when `E:\Repos\DiffusionNexus.Installer.SDK`
exists, so the developer's `project.assets.json` lists the four SDK entries as `type: project`
and pulls transitives from the develop checkout's csproj files. CI restores with
`-p:UseLocalSDK=false` and pulls them from the `2.0.0-preview.4` nuspecs.

They agree today only by coincidence: the one divergence (Shared on develop added
`System.Net.Http.Json` 10.0.10, absent from preview.4) happens to be framework-pruned. The next
SDK dependency that is *not* framework-provided makes CI fail "notices are out of date" on an
unrelated PR, unreproducible locally.

**Fix:** generate from a `-p:UseLocalSDK=false` restore, or run the generator against a
dedicated restore rather than the ambient graph.

---

## 7. `ThirdPartyNotices.Load()` re-reads 87 KB on every render

**`DiffusionNexus.Installer.Electron/Services/ThirdPartyNotices.cs:14`**

`THIRD-PARTY-NOTICES.txt` is 86,961 bytes, and
`<pre class="notices">@ThirdPartyNotices.Load()</pre>` sits in the render tree of both
`Licenses.razor` and `LicensesModal.razor`. Under `InteractiveServer`, every Confirm-stage
re-render while the dialog is open (a disclaimer checkbox toggle, any `_moduleChanged` from a
sibling module) re-runs the stream read + UTF-8 decode + `TrimEnd` copy — roughly 260 KB of
transient allocation per interaction, on the circuit's sync context.

**Fix:** `private static readonly Lazy<string> Text = new(ReadResource);` and return
`Text.Value`.

---

## 8. The modal scroll fix is opt-in, so `MismatchModal` keeps the bug it was written for

**`DiffusionNexus.Installer.Electron/wwwroot/app.css:784`**

`.modal-backdrop` is already fully defined at line 579 (`display:flex; align-items:center;
padding:1.5rem`); line 784 appends a duplicate selector adding only `overflow:auto`. With
`align-items:center` untouched, a card taller than the viewport still overflows *above* the
scroll origin and its top is unreachable — `.modal-card-scroll` is what actually fixes it, and
only `LicensesModal` opts in.

`MismatchModal` renders an unbounded `<table class="report">` of every size-mismatched file:
with 30 mismatches on a 900px window, the header and top rows scroll off the top with no way
back, and "Cancel installation" / "Continue" are pushed off the bottom.

**Fix:** fold `overflow:auto` + `max-height: calc(100vh - 3rem)` + the flex-column body into
`.modal-backdrop` / `.modal-card` themselves (with `align-items: flex-start`, or `margin:auto`
on the card) so every modal gets it.

---

## 9. Backdrop-click dismissal eats text selections

**`DiffusionNexus.Installer.Electron/Components/Shared/LicensesModal.razor:11`**

Drag-selecting a licence paragraph inside the 1,800-line `<pre>` and releasing outside the card
(easy — the card is capped at `calc(100vh - 3rem)` and backdrop padding is 1.5rem) dispatches
`click` on the nearest common ancestor of mousedown/mouseup: the backdrop. `@onclick="Close"`
runs, the dialog vanishes, the selection is lost.

**Fix:** dismiss on `@onmousedown` with a target check, or record the mousedown target and close
only when both ends were on the backdrop.

---

## 10. The dialog is not keyboard- or screen-reader-operable

**`DiffusionNexus.Installer.Electron/Components/Shared/LicensesModal.razor:13`**

It declares `role="dialog" aria-modal="true"` but has no Escape dismissal, no initial focus and
no focus trap. A keyboard user who activates "Third-party licences" keeps focus on the trigger
behind the overlay: Escape does nothing, Tab walks into the wizard controls underneath (still in
the tab order despite `aria-modal`), and a screen reader announces nothing because focus never
entered the dialog.

**Fix:** `@onkeydown` for Escape on the backdrop with `tabindex="-1"`, move focus to the close
button on open, restore it to the trigger on close.

---

## 11. Modal shell markup is now hand-copied three times

**`DiffusionNexus.Installer.Electron/Components/Shared/LicensesModal.razor:1`**

`PromptModal.razor:7-16` and `MismatchModal.razor:8-34` already open
`<div class="modal-backdrop"><div class="modal-card" role="dialog" aria-modal="true">…
<div class="modal-actions">`. Every future modal fix has to land in three places — which is
exactly why the scroll/close fix in this PR reached only one of them (finding #8).

**Fix:** extract a `Modal.razor` taking `Title`, `Wide`, `Scrollable`, `ChildContent` and
`Actions` RenderFragments plus a `Closed` callback; rewrite all three on it.

---

## 12. The npm licence-file regex misses real filenames, and the fallback drops the copyright line

**`Scripts/Generate-ThirdPartyNotices.ps1:174`**

`^(LICEN[CS]E|COPYING)(\..*)?$` rejects `LICENSE-MIT`, `LICENSE.APACHE2`, `LICENSE.BSD` and
`NOTICE`. Such a package falls through to `Scripts/license-data/texts/<spdx>.txt`, and
`texts/MIT.txt` contains no `Copyright (c) …` line — the notice literally says "The above
copyright notice … shall be included" with no notice above it.

Already live for `lazy-val 1.0.5` (`THIRD-PARTY-NOTICES.txt:1206-1230`): only
`Author: Vladimir Krivosheev` plus holder-less MIT boilerplate.

**Fix:** broaden to `^(LICEN[CS]E|COPYING|NOTICE)([-.].*)?$`, and on the fallback path
synthesise `Copyright (c) <author>` from `package.json` above the standard text.

---

## 13. `Generate-ThirdPartyNotices.ps1` lower-cases the package id but not the version

**`Scripts/Generate-ThirdPartyNotices.ps1:98`**

NuGet's global-packages layout lower-cases *both* segments. A dependency resolved at e.g.
`SomePkg/1.0.0-Beta2` makes `Join-Path $nugetRoot 'somepkg' '1.0.0-Beta2'` miss, and line 100
throws "Package … is not in the NuGet cache. Run 'dotnet restore' first." — right after a
successful restore, sending the developer down the wrong path.

**Fix:** lower-case `$version` too.

---

## 14. `DisclaimerPanelTests` asserts a bUnit implementation detail

**`DiffusionNexus.Installer.Tests/Components/DisclaimerPanelTests.cs:47`**

`insideCard.Should().Throw<MissingEventHandlerException>(…)` passes today only because bUnit
refuses to bubble a click to a handler behind `@onclick:stopPropagation`. If bUnit ever models
stopPropagation as "don't invoke the ancestor handler" (the actual browser behaviour the comment
describes), the click becomes a silent no-op, nothing throws, and the test fails while
production behaviour is unchanged and correct.

**Fix:** assert the observable outcome — click inside the card, then assert `.modal-backdrop` is
still present.

---

## 15. `StylesheetTests`' `depth <= 2` heuristic rejects legal CSS

**`DiffusionNexus.Installer.Tests/Components/StylesheetTests.cs:38`**

`@media (prefers-reduced-motion: no-preference) { @keyframes spin { from { … } } }` is valid and
reaches depth 3 → test fails "a '}' is missing above" against a balanced stylesheet. Same for
`@supports` inside `@media`, or native CSS nesting (`.card { &:hover { … } }`). Meanwhile the
failure this test exists to catch (a rebase dropping a brace) already leaves `depth != 0` at EOF
and is caught by line 41.

**Fix:** drop the upper bound, or raise it and make the message advisory.
