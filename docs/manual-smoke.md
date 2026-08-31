# Manual smoke checklist — Installer 3.x wizard (slice 1)

Automated tests cover the module logic, the gate and the session. These are the things only a
real run can prove. Use a scratch install folder, never a real one.

## 0. Before anything else

1. **Expect:** the gallery is *styled* — dark background, teal accents, cards in a grid.
   If it renders as plain serif text on white, static web assets are not being served: check the
   console for `StaticAssetsInvoker` warnings. That failure also kills `blazor.web.js`, so no
   button on any page will respond — the app looks alive but is completely inert.

## 1. Gallery

1. Launch with no catalog installed (delete `%LocalAppData%\DiffusionNexus\catalog`).
   **Expect:** the gallery populates from the embedded seed; no error, no empty state.
2. **Expect:** exactly nine cards are enabled — Stable Diffusion web UI, Stable Diffusion WebUI Forge,
   Fooocus, ACE-Step-1.5, AI-Toolkit, Blanck-ComfyUI, Base-install-Triton-SageAttention-Manager,
   ComfyUI Llama Cpp test, and FlashVSR-Video&Image Upscale. Every other card is visible but
   disabled, most with a "Coming soon" note (they need ModelDownloads or VramProfile, neither of
   which is in slice 1).
   **Config535 is the exception:** it is disabled with a torch message, not a "Coming soon" one —
   its catalog entry pairs torch 2.8.0 with CUDA 13.0, for which no wheel exists, so the pipeline
   would refuse it before step 1. That is a catalog data fix, not a missing module.
3. Filter by type Video. **Expect:** LTX-2-3-GGUF, LTX-2-3-V1.1-Director-GGUF, MiniMax H3, and
   Wan 2.2 - GGUF appear (all disabled/"coming soon" — none are in the slice-1 installable set);
   the Image cards do not.
   Note: the embedded seed predates the catalog's Audio workflow type, so no workload in it is
   tagged Audio yet (ACE-Step-1.5 is still `Image` in this snapshot) and no Audio filter button
   renders. Re-check this step once an Audio-tagged workload ships in the embedded catalog.
4. Filter by software ComfyUI. **Expect:** only ComfyUI-based cards remain, and the software
   filter offers exactly the software the catalog actually contains — no empty options.
5. Set `DIFFUSIONNEXUS_CATALOG_PATH` to a catalog checkout and relaunch.
   **Expect:** the gallery reflects that checkout.
6. Set `DIFFUSIONNEXUS_CATALOG_PATH` to a folder that does not exist and relaunch.
   **Expect:** the app still starts and falls back to the installed catalog. It must not crash.
7. Navigate to `/updates`. **Expect:** the version/updater screen appears (version string,
   "Check for updates" button, updater log). Click "Back to workloads". **Expect:** the
   gallery returns.

## 2. Wizard stages

1. Pick Fooocus. **Expect:** Location → System → Confirm → Install. No model, VRAM,
   workflow or accelerator screen appears.
2. Pick Blanck-ComfyUI. **Expect:** the Location stage also shows the model-library and output
   folder fields.
3. Pick AI-Toolkit. **Expect:** the model-library field is present, the output folder field is not.
4. Clear the install folder. **Expect:** Next is disabled and the validation message shows.
5. Click Browse. **Expect:** a native folder dialog opens. Dismiss it. **Expect:** the field is
   unchanged and nothing crashes.

## 3. A real install

1. Install Fooocus into a scratch folder and let it finish.
   **Expect:** live log streams, the step counter advances, the report table renders, and the
   launcher script and shortcuts exist on disk afterwards.
2. Start again and press Cancel mid-clone.
   **Expect:** the run ends as Cancelled, not Failed, and no bug-report prompt appears.
3. Start an install, then resize/minimise and restore the window several times to force a circuit
   reconnect. **Expect:** the install keeps running and the log continues where it left off.
4. While an install is running, reconnect by navigating away and back to the gallery, then
   reopen the same workload's wizard. **Expect:** you return to the install's report stage,
   not the wizard's first screen.
5. Start an install, let it finish, then reconnect to the same workload by navigating away and
   back, then opening the wizard again. **Expect:** the wizard restarts from the first screen,
   not the finished report. The install itself never runs again. This is a known limitation:
   reconnecting after an install has completed returns to the wizard's start, not the result.

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
