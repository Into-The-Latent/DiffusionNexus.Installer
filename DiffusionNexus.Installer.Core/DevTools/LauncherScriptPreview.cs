using DiffusionNexus.Installer.SDK.Models.Compatibility;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.DevTools;

/// <summary>One exported script and what its bytes actually look like.</summary>
/// <param name="LineEnding">"CRLF", "LF", or "mixed" — the thing that broke every generated
/// launcher and could only be seen by running a full install.</param>
public sealed record ExportedScript(string WorkloadName, string FileName, int Bytes, string LineEnding)
{
    /// <summary>Batch files need CRLF, shell scripts need LF. Anything else is a bug.</summary>
    public bool IsCorrect =>
        FileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
        FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? LineEnding == "CRLF"
            : LineEnding == "LF";
}

/// <summary>
/// Writes the launcher scripts a workload would get, without running an install.
/// <para>
/// This exists because verifying a one-line change to script generation previously cost a full
/// A1111 or Forge install — minutes to hours — just to read ~35 lines of batch file. The CRLF bug
/// took two complete installs to confirm.
/// </para>
/// <para>
/// <b>Scope, deliberately honest:</b> this is a PREVIEW of the generated scripts, not a promise of
/// exactly what an install writes. The authoritative mapping lives across four SDK step handlers
/// (PostInstall, AceStepPostInstall, AIToolkitPostInstall, RunInitialSetup) plus a portable-install
/// branch that skips our launchers entirely, and it is not reachable without an InstallationContext.
/// The duplication here is therefore real and can drift — see the follow-up issue about lifting a
/// single CreateScriptsFor into the SDK. What it IS reliable for is anything shared by every
/// generated script: the branding header, line endings, ANSI/window sequences, cmd escaping.
/// </para>
/// </summary>
public sealed class LauncherScriptPreview
{
    public IReadOnlyList<ExportedScript> Export(InstallationConfiguration workload, string targetFolder)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);

        // One folder per workload: names collide across workloads (every ComfyUI pack writes
        // run_nvidia.bat) and a flat dump would silently overwrite all but the last.
        var folder = Path.Combine(targetFolder, Sanitize(workload.Name));
        Directory.CreateDirectory(folder);

        var written = new List<ExportedScript>();

        foreach (var script in ScriptsFor(workload))
        {
            // Through WriteScript, never File.WriteAllText: that method owns line-ending
            // normalization, and bypassing it would make this tool report success on exactly the
            // bug it exists to catch.
            LauncherScriptFactory.WriteScript(script, folder);

            var path = Path.Combine(folder, script.FileName);
            var bytes = File.ReadAllBytes(path);
            written.Add(new ExportedScript(workload.Name, script.FileName, bytes.Length, Describe(bytes)));
        }

        return written;
    }

    /// <summary>
    /// Mirrors the SDK step handlers' per-type choices. CUDA comes from GetEffectiveTorch() rather
    /// than a UI field so the output matches what the pipeline would resolve.
    /// </summary>
    private static IEnumerable<GeneratedScript> ScriptsFor(InstallationConfiguration workload)
    {
        var cuda = CudaVersionNormalizer.ToDigits(workload.GetEffectiveTorch().CudaVersion);

        switch (workload.Repository.Type)
        {
            case RepositoryType.Forge:
                yield return LauncherScriptFactory.CreateForgeLauncherScript();
                yield return LauncherScriptFactory.CreateForgeWebuiUserScript(cuda);
                yield return LauncherScriptFactory.CreateForgeUpdateScript();
                break;

            case RepositoryType.A1111:
                yield return LauncherScriptFactory.CreateA1111LauncherScript();
                yield return LauncherScriptFactory.CreateA1111WebuiUserScript(cuda);
                yield return LauncherScriptFactory.CreateA1111UpdateScript();
                break;

            case RepositoryType.Fooocus:
                yield return LauncherScriptFactory.CreateFooocusLauncherScript();
                yield return LauncherScriptFactory.CreateFooocusUpdateScript();
                yield return LauncherScriptFactory.CreateFooocusPatchXformersScript();
                break;

            case RepositoryType.AIToolkit:
                foreach (var script in LauncherScriptFactory.CreateAIToolkitScripts()) yield return script;
                break;

            case RepositoryType.AceStep:
                foreach (var script in LauncherScriptFactory.CreateAceStepScripts()) yield return script;
                break;

            default:
                // ComfyUI and anything new. Output folder and CPU mode are left at their defaults:
                // both change the emitted command line, so a preview that guessed at them would be
                // misleading in a way the caller could not see.
                yield return LauncherScriptFactory.CreateComfyUILauncherScript("ComfyUI");
                yield return LauncherScriptFactory.CreateComfyUIUpdateScript("ComfyUI", workload.Repository.Type);
                break;
        }
    }

    /// <summary>Reads the bytes rather than the string: the whole point is what reached disk.</summary>
    private static string Describe(byte[] bytes)
    {
        int crlf = 0, bareLf = 0;

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;

            if (i > 0 && bytes[i - 1] == (byte)'\r') crlf++;
            else bareLf++;
        }

        return (crlf, bareLf) switch
        {
            (0, 0) => "none",
            ( > 0, 0) => "CRLF",
            (0, > 0) => "LF",
            _ => "mixed",
        };
    }

    private static string Sanitize(string name)
    {
        var cleaned = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length == 0 ? "workload" : cleaned.Trim();
    }
}
