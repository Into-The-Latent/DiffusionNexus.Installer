# Manual smoke checklist — Installer 3.x wizard and welcome screen

Automated tests cover the module logic, the gate and the session. These are the things only a
real run can prove. Use a scratch install folder, never a real one.

## 0. Before anything else

1. **Expect:** the welcome screen is *styled* — dark background, teal accents, cards in a grid.
   If it renders as plain serif text on white, static web assets are not being served: check the
   console for `StaticAssetsInvoker` warnings. That failure also kills `blazor.web.js`, so no
   button on any page will respond — the app looks alive but is completely inert.

## 1. Catalog and the workload lists

There is no single gallery of every workload any more. `/` (§6) asks which software; the workload
list lives behind each multi-workload software card, at `/software/{type}`. These steps check that
the catalog reaches those lists correctly.

1. Launch with no catalog installed (delete `%LocalAppData%\DiffusionNexus\catalog`).
   **Expect:** the welcome screen populates from the embedded seed — six software cards, no error,
   no empty state.
2. Click ComfyUI. **Expect:** 16 cards, every one enabled except Config535 — 15 of 16. The catalog
   holds 25 workloads but only 21 target the installer: the four DiffusionNexusCore ones
   (Captioning, Inpainting, Outpainting, Upscaling-Z-Image-Turbo) are ComfyUI-typed and must not
   appear here or anywhere else in the app.
   **Config535 is the exception:** it is disabled with a torch message, not a "Coming soon" one —
   its catalog entry pairs torch 2.8.0 with CUDA 13.0, for which no wheel exists, so the pipeline
   would refuse it before step 1. That is a catalog data fix, not a missing module.
3. Filter by type Video. **Expect:** LTX-2-3-GGUF, LTX-2-3-V1.1-Director-GGUF, MiniMax H3, and
   Wan 2.2 - GGUF appear (all enabled now — LTX-2-3-GGUF, LTX-2-3-V1.1-Director-GGUF, MiniMax H3 and
   Wan 2.2 - GGUF are Content-stage workloads); the Image cards do not.
   The embedded seed carries ACE-Step-1.5 as Audio, **which you only see because step 1 told you to
   delete the installed catalog first, and only until the first update check** — see steps 4 and 5.
   Note that no Audio filter appears on the
   ComfyUI workload screen, and that is correct — the only Audio workload belongs to ACE-Step,
   which is a single-workload software and goes straight to setup. The filter is catalog-derived,
   so an Audio button appears there by itself the day a ComfyUI workload declares it.
4. **The upgrade path. Step 1 can never show you this, because it deletes the catalog first.**
   Leave the catalog the previous steps installed in place — do NOT delete
   `%LocalAppData%\DiffusionNexus\catalog` — and relaunch. Go to ACE-Step.
   **Expect:** ACE-Step-1.5 still reads **Image**, not Audio.

   That is expected, and it is the honest reading of commit `8ad4db6`: the refreshed seed fixes the
   typing **on a cold start with no installed catalog, and only there**. `CatalogLocator` re-seeds
   from the embedded archive only when the embedded `catalogVersion` is strictly greater than the
   installed one, and both are `1`, so `1 > 1` is false and the new seed is never applied to an
   existing install. Anyone who has run a 3.x build before keeps the old snapshot.

5. **And the cold-start fix does not survive an update check either. Check this too.** On the
   machine from step 1 — the one that *did* get ACE-Step-1.5 as Audio — let the app run an update
   check against the stable channel and apply what it offers. Relaunch and look at ACE-Step again.
   **Expect:** it has reverted to `Image`.

   That is not a bug in the update path; it is the seed being ahead of the tag. The embedded seed is
   packed from catalog commit `3847a24`, which is **newer than the `v1` tag** (`8dcff11`), and `v1`
   is what the stable channel actually serves. `CatalogDiff` compares item hashes and is deliberately
   version-agnostic, so the older remote copy still reads as an update and overwrites the newer
   local one.

6. **Do not "fix" either of the two steps above by bumping `catalogVersion` in
   `DiffusionNexus.Installer.Electron/Assets/Catalog/manifest.json`.** On the stable channel that
   number comes from the catalog repo's own git tag (`VERSION=${GITHUB_REF_NAME#v}` in its
   `release.yml`) and the latest stable tag is still `v1`, so a local `2` would be claiming a version
   the remote does not have. Applying a remote v1 stamps `SectionState(remote.CatalogVersion, ...)`,
   writing the installed state back down to `1`; the next launch then sees embedded `2 > 1` and
   re-seeds; the update check offers v1 again. That is a reseed/update ping-pong in which every
   apply undoes the ACE-Step fix, and it is worse than the fix simply not arriving.

   **The real fix is a release action in another repo:** tag `v2` in
   `Into-The-Latent/DiffusionNexus.Catalog` — which publishes the content the seed was already
   packed from — then regenerate the embedded seed with `--version 2`. Note that this is needed not
   only to reach upgraders but to stop the fix being undone on the machines that *did* get it.
   Until then, both steps above are expected to "fail".
7. **Expect:** the type filter is the only filter on this screen. There is deliberately no software
   filter — the welcome screen already answered which software, so a second control for it would be
   a dead one.
8. Set `DIFFUSIONNEXUS_CATALOG_PATH` to a catalog checkout and relaunch.
   **Expect:** the software cards and the workload lists behind them reflect that checkout.
9. Set `DIFFUSIONNEXUS_CATALOG_PATH` to a folder that does not exist and relaunch.
   **Expect:** the app still starts and falls back to the installed catalog. It must not crash.
10. Navigate to `/updates`. **Expect:** the version/updater screen appears (version string,
   "Check for updates" button, updater log). **Expect:** the version it shows matches the one in
   the top bar exactly — no `+<commit sha>` suffix on either. Click "Back to all software".
   **Expect:** the welcome screen returns.

## 2. Wizard stages

1. Pick Fooocus. **Expect:** Location → System → Confirm → Install. No Content screen (VRAM,
   models, workflows) appears.
2. Pick Blanck-ComfyUI. **Expect:** the Install location panel says "Where the software gets
   installed" and, once a folder is typed, a grey "Will be created: <folder>\ComfyUI" line under
   the box. The folders panel shows only the Output folder box, empty, with grey
   `<folder>\ComfyUI\output` text inside, and below it a full-width closed "Advanced settings · custom model folders"
   bar. No "saved model folders" checkbox, no library box outside Advanced. Click the bar.
   **Expect:** it opens with the Model library folder box first (grey `<folder>\ComfyUI\models`
   inside when empty), the overwrite checkbox (only when a library is set), 21 folder-name boxes
   prefilled with ComfyUI's standard names (or your saved custom ones), "Reset to standard", and
   an empty "Additional folders" list with "+ Add folder". Type `MyLoras` into LoRAs, press Next,
   then Back. **Expect:** the closed line now says "custom folders in use". Cancel the wizard and
   pick Blanck-ComfyUI again. **Expect:** LoRAs still reads `MyLoras` (saved on Next). Reset to
   standard and press Next to clean up.
3. Pick AI-Toolkit. **Expect:** the model-library field is present, the output folder field is not.
4. Clear the install folder. **Expect:** Next is disabled and the validation message shows.
5. Click Browse. **Expect:** a native folder dialog opens. Dismiss it. **Expect:** the field is
   unchanged and nothing crashes.
6. Pick Krea-2-Turbo. **Expect:** after Location comes a Content screen showing ONLY the
   "Graphics card memory" panel with a VRAM dropdown offering exactly 8, 12, 16, 24, 32 GB with
   8 preselected, and below it a closed "Advanced settings · models and workflows" bar whose
   right side reads "N of N models, N of N workflows". Open it. **Expect:** every model ticked
   and grouped by folder, every workflow ticked, and a disk-space line that updates when you untick a model or
   change the tier.
7. Pick Ideogram-4.0. **Expect:** the dropdown offers exactly 24 and 32 GB, 24 preselected.
8. On the Content screen, point the install folder (Back, then edit) at a folder that already
   holds one of the listed models. **Expect:** that row shows "already downloaded".

**Confirm stage:** the primary button reads "Start installation" (every earlier stage says
"Next").

## 3. A real install

1. Install Fooocus into a scratch folder and let it finish.
   **Expect:** live log streams, the step counter advances, the report table renders, and the
   launcher script and shortcuts exist on disk afterwards.
2. Start again and press Cancel mid-clone.
   **Expect:** the run ends as Cancelled, not Failed, and no bug-report prompt appears.
3. Start an install, then resize/minimise and restore the window several times to force a circuit
   reconnect. **Expect:** the install keeps running and the log continues where it left off.
4. While an install is running, reconnect by navigating away and back to the welcome screen, then
   reopen the same workload's wizard. **Expect:** you return to the install's report stage,
   not the wizard's first screen.
5. Start an install, let it finish, then reconnect to the same workload by navigating away and
   back, then opening the wizard again. **Expect:** the wizard restarts from the first screen,
   not the finished report. The install itself never runs again. This is a known limitation:
   reconnecting after an install has completed returns to the wizard's start, not the result.
6. Install Wan 2.2 - GGUF at tier 8 into a scratch folder — the heaviest case, 10 models and 26
   links. **Expect:** files land under the right `models\...` folders, the report shows no
   unexplained skips, and no row says "Requires more VRAM" for a model you expected.
7. Re-run the same install over that folder after truncating one downloaded Hugging Face model
   file (not the Civitai `Krea 2 Identity Edit` download, whose name only comes from the server)
   to a few bytes. **Expect:** pressing Next on Confirm shows ONE dialog listing that file;
   Continue with it ticked re-downloads it; Cancel installation leaves you on Confirm with a notice.

## 4. Known limitations

Only one install can run at a time. If you open a second workload's wizard while one is running
and walk it through to the Install stage, the first workload's progress and result render under
the second workload's header. Opening the second wizard alone does not cause this — the Location,
System, and Confirm stages all render correctly. The mixing appears only if you reach Install.
The install itself never restarts; the first workload's session continues running in the background.

## 5. Known gap

Launching the packaged Electron exe directly still exits instantly — only the .NET entry point
under `resources/bin` works. This blocks any Start Menu shortcut and must be fixed before a
public 3.x release. Slice 1 is run from a dev build.

## 6. Welcome screen

1. Launch. **Expect:** a top bar with the version on the left and Feedback, Licences,
   Check for Updates on the right (plus Developer tools in a Debug build only); the Into The
   Latent banner as a wide strip, not a 16:9 block; "Easy Workload Installer" in the gradient
   wordmark; six software cards in **one row** with their logos; and a "Join the Community" footer
   with YouTube, Patreon and Civitai.
   **Look at the banner crop specifically** — it is cropped from a 16:9 source. The crop was re-cut
   to 3.4:1 with the focus below centre, so the runner's head, the portal ring under it and both
   lines of the wordmark should all be whole.
   **Resize the window from its 900×650 minimum up to maximised** and watch the screen follow:
   the banner, the title and the tiles all scale with it, and the content stays vertically centred
   rather than stacking at the top over a void. The community pills stay on screen throughout
   (the 900×650 minimum shows a few px of scroll for the bottom padding, nothing more).
   **Maximised on a 1080p display, all six tiles are visible at once and both arrows are dim.**
   That was the shape of the first attempt's failure — everything pinned to the size that fitted a
   720p window, adrift in a big one — so it is worth looking at deliberately.
2. **Expect:** the ComfyUI card reads "16 workloads"; the other five read "straight to setup".
   A card whose single workload is blocked would read "1 workload" instead, never "straight to
   setup" — that card has no link behind it at all.
2a. **The strip.** **First: it must open showing ComfyUI whole, at the left-hand end.** A packaged
   build once opened it scrolled to the far end with the first tile sliced in half, and no headless
   render reproduced it — it needs contents that settle after the first layout. Then click `>`.
   **Expect:** it scrolls one page, smoothly, and `<` becomes usable.
   Keep clicking. **Expect:** at the far end `>` dims and stops responding; `<` walks back the same
   way and dims at the start. Widen the window until all six tiles are visible at once.
   **Expect:** both arrows dim — there is nowhere left to go in either direction. Also scroll the
   strip with the mouse wheel over it: it must move even though the arrows were not touched.
3. Click a community link. **Expect:** it opens in your normal browser. The installer window must
   NOT navigate to it — if the app itself turns into a web page, that is the bug this was written
   to catch.
4. Click Licences, then come back. Click Check for Updates, then come back. **Expect:** both pages
   still work from the top bar.
5. Click ComfyUI. **Expect:** the Select workload screen, with 16 cards showing their artwork,
   an "All / Image / Video" filter and no software filter. Filter to Video. **Expect:** four
   cards. Click "← All software", then pick ComfyUI again. **Expect:** the filter is back on All.
   Now the filter-persistence half: from ComfyUI's screen with Video selected, use the browser/window
   Back and Forward controls (or navigate to `/software/Fooocus` and back to `/software/ComfyUI` by
   hand). **Expect:** re-entering ComfyUI's screen shows All — filter state is per software — and at
   no point does a screen you are already on flash back through "Loading the catalog...". There is no
   manual gesture that re-supplies parameters to the *same* screen (Blazor Server has no resize hook
   and this app registers no resize interop), so that half of the guard is covered by
   `SoftwareWorkloadsPageTests.Keeps_the_chosen_filter_when_the_same_parameters_are_supplied_again`
   rather than by hand.
   Hover a workload card. **Expect:** a tooltip with the catalog's description for that workload —
   the tile has no room for it and this is now the only place in the app it appears.
6. Click Fooocus. **Expect:** the wizard opens directly — no workload screen — and its first stage
   opens with the hero: the same tile artwork at size, "Fooocus" beside it, the catalog's
   description under that with **bold** actually bold and the bullets as bullets (no stray `**` or
   `-` characters anywhere), and the install-location box below it, all without scrolling.
   Press Next. **Expect:** the hero is gone from every later stage.
   Now go back to `/` and click **Automatic 1111**. **Expect:** the same hero, with the heading
   "Stable Diffusion web UI" and a small teal "AUTOMATIC 1111" above it — that line is what ties
   the screen back to the tile you clicked, and it appears only when the two names differ (Fooocus
   has no such line).
   Finally click **ComfyUI** and open any workload from its list. **Expect:** no hero — that screen
   already showed you the card, its artwork and its description.
7. Navigate to `/software/Nonsense` by hand. **Expect:** "That software is not in the catalog"
   and a link back, not an error page.
8. Click Feedback, send a report with a summary and details. **Expect:** a GitHub issue URL comes
   back. Check the issue exists in the Feedback repo and is labelled as coming from the installer.
9. Disconnect from the network and click Feedback again. **Expect:** the dialog stays open, shows
   the failure reason, and your typed text is still there.

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
   Then make `%LocalAppData%\DiffusionNexus\user_settings.json` read-only and pick the other channel.
   **Expect:** "The channel could not be saved: …" and the dot jumps back to the channel still
   in effect (not provable in bUnit — the browser keeps its own checked state). Clear the
   read-only flag afterwards. While a check runs (press **Check for updates** on `/updates`,
   switch back quickly) the radios are disabled.
7. Editor: press **Release**, confirm. About a minute later launch on Stable. **Expect:** the
   same update offered and applied.
8. Start an install of any workload, then open `/updates` while it runs with an update pending.
   **Expect:** no Apply button, instead "It can be applied once <workload> has finished." Let the
   install finish. **Expect:** the button appears without leaving the page.
9. Disconnect the network and press Check for updates. **Expect:** "The catalog check failed:
   …" on `/updates`, nothing on the welcome screen, no dot.
10. Run with `DIFFUSIONNEXUS_CATALOG_PATH` pointing at a catalog checkout. **Expect:** "Update
    check skipped: a local catalog override is active at <path>." and no Apply button.

## 8. App updates on the Preview channel

The app follows the same channel as the catalog (issue #19). Needs two **installed** copies of
the current release, or one machine run twice: the updater does nothing under `dotnet run`.
Call the installed version `A` and the test version `B` (next patch number).

1. Publish a Preview build: `.\Scripts\New-Release.ps1 -Version B -Notes "smoke" -Prerelease`.
   **Expect:** the release page shows `vB` with a **Pre-release** badge, and `vA` still carries
   **Latest**.
2. Launch the installed app on Stable (no variable, setting untouched). Open `/updates`.
   **Expect:** "App updates: following **Stable**"; the updater log says "Following Stable app
   releases." then "No update available". `vB` is **not** offered.
3. Quit. Launch with `DIFFUSIONNEXUS_CATALOG_CHANNEL=preview` set for the process.
   **Expect:** "App updates: following **Preview**" with the testing hint, the catalog line
   below says Preview too, and the log says "Following Preview app releases.", "Update
   available: B. Downloading...", then **Restart and install** appears. Do not press it yet.
4. Still on that page, nothing should say `latest`, `beta` or `prerelease` anywhere.
5. Promote it: `gh release edit vB --repo Into-The-Latent/DiffusionNexus.Installer --prerelease=false --latest`.
   Wait a minute (the `releases/latest` redirect lags). Launch the **Stable** copy again.
   **Expect:** `vB` is now offered and downloads.
6. Press **Restart and install** on either copy. **Expect:** the app comes back as `B`.
7. On the copy that is now `B`, publish nothing new and launch on Preview, then on Stable.
   **Expect:** "No update available" both times -- switching back to Stable never reinstalls
   an older build.
8. Debug build, Developer tools. **Expect:** the panel is titled "Update channel" and its hint
   says the setting covers the catalog and the app.
9. The hotfix case (PR #21 review). With an installed copy on `A`: publish pre-release `C`
   (a minor bump, e.g. 3.1.0), **then** a Stable release `B` (next patch), so `B` is the newest
   release but `C` the highest version. Launch on Preview. **Expect:** the log says "A newer
   release was published after vC; checking vC directly." and `C` is offered -- not `B`. The
   download is the full installer (no differential) on this path. Launch a second copy on
   Stable. **Expect:** `B`. Then, in one Debug session, check on Preview and switch to Stable in
   Developer tools and check again. **Expect:** the Stable check behaves as in step 2 (the
   updater was pointed back at its own config), not "no update" against `C` alone.

## 9. Announcements banner

The banner shows operator messages from `messages.json` in the shared Gist (issue #12), read as
app id `installer`. It needs a row whose `targets` is empty or contains `installer`; dismissals
are remembered in `%LocalAppData%\DiffusionNexus\dismissed_messages.json`, the same file the 1.x
installer writes. Delete that file first for a clean run.

1. With no applicable row in the Gist, launch. **Expect:** no banner, and no empty stripe under
   the top bar on any screen.
2. Add an `info` row for `installer` with a `title`, an https `actionUrl` and `actionLabel`.
   Relaunch. **Expect:** a teal-edged banner under the top bar within a moment of the welcome
   screen painting; it is still there after picking a software and on every wizard stage.
3. Click the action button. **Expect:** it opens in your normal browser; the installer window
   does not navigate.
4. Click the X. **Expect:** the banner goes at once. Relaunch. **Expect:** it stays gone.
5. Set the row to `"severity": "critical", "dismissible": false`, with a new `id`. Relaunch.
   **Expect:** a red-edged banner with no X.
6. With that banner showing, run an install to the two-column install screen. **Expect:** the
   buttons and the community footer are still inside the window, and both columns scroll.
7. Disconnect the network and launch. **Expect:** no banner, no error, no delay to the window.
