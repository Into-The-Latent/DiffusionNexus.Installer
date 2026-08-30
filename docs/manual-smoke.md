# Manual smoke checklist — Installer 3.x wizard (slice 1)

Automated tests cover the module logic, the gate and the session. These are the things only a
real run can prove. Use a scratch install folder, never a real one.

## 1. Gallery

1. Launch with no catalog installed (delete `%LocalAppData%\DiffusionNexus\catalog`).
   **Expect:** the gallery populates from the embedded seed; no error, no empty state.
2. **Expect:** exactly six cards are enabled — Stable Diffusion web UI, Forge, Fooocus,
   ACE-Step, AI-Toolkit, Blanck-ComfyUI. Every other card is visible but disabled with a
   "Coming soon" note.
3. Filter by type Audio. **Expect:** ACE-Step appears; the Image and Video cards do not.
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
   not the wizard's first screen. If you instead see the wizard's first screen, that means
   you reconnected to a different workload — this is a known limitation: only one install
   runs at a time, and opening a second workload's wizard renders the first one's session
   under the second workload's UI.

## 4. Known limitations

Only one install can run at a time. If you open a second workload's wizard while one is
running, the first workload's session state renders under the second workload's UI. The install
itself never restarts — the limitation is UI-only.

## 5. Known gap

Launching the packaged Electron exe directly still exits instantly — only the .NET entry point
under `resources/bin` works. This blocks any Start Menu shortcut and must be fixed before a
public 3.x release. Slice 1 is run from a dev build.
