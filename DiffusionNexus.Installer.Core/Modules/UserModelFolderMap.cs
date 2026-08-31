using DiffusionNexus.Installer.SDK.Models.Installation;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// Maps the ~20 per-type Default*Folder values in UserSettings onto the ComfyUI folder keys
/// extra_model_paths.yaml and ModelDestinationResolver use.
/// <para>
/// Several keys have two settings fields: a newer plural one and a legacy singular one left over
/// from an earlier schema. The plural wins when both are set, which is the order the Avalonia
/// installer's own mapper used — dropping the fallback would silently ignore an older user's
/// stored folders.
/// </para>
/// </summary>
public static class UserModelFolderMap
{
    /// <summary>
    /// Folder key -> the settings values to try, most current first. Kept as data rather than a
    /// hundred lines of if/else so a new folder type is one line.
    /// </summary>
    private static readonly (string Key, Func<UserSettings, string?>[] Sources)[] Mappings =
    [
        ("audio_encoders",        [s => s.DefaultAudioEncodersFolder]),
        ("checkpoints",           [s => s.DefaultCheckpointsFolder, s => s.DefaultCheckpointFolder]),
        ("clip",                  [s => s.DefaultClipFolder]),
        ("clip_vision",           [s => s.DefaultClipVisionFolder]),
        ("configs",               [s => s.DefaultConfigsFolder]),
        ("controlnet",            [s => s.DefaultControlNetFolder]),
        ("diffusers",             [s => s.DefaultDiffusersFolder]),
        ("diffusion_models",      [s => s.DefaultDiffusionModelsFolder, s => s.DefaultDiffusionModelFolder]),
        ("embeddings",            [s => s.DefaultEmbeddingsFolder, s => s.DefaultEmbeddingFolder]),
        ("gligen",                [s => s.DefaultGligenFolder]),
        ("hypernetworks",         [s => s.DefaultHypernetworksFolder]),
        ("latent_upscale_models", [s => s.DefaultLatentUpscaleModelsFolder]),
        ("loras",                 [s => s.DefaultLorasFolder, s => s.DefaultLoraFolder]),
        ("model_patches",         [s => s.DefaultModelPatchesFolder]),
        ("photomaker",            [s => s.DefaultPhotomakerFolder]),
        ("style_models",          [s => s.DefaultStyleModelsFolder]),
        ("text_encoders",         [s => s.DefaultTextEncodersFolder, s => s.DefaultTextencoderFolder]),
        ("unet",                  [s => s.DefaultUnetFolder]),
        ("upscale_models",        [s => s.DefaultUpscaleModelsFolder, s => s.DefaultUpscalerFolder]),
        ("vae",                   [s => s.DefaultVAEFolder]),
        ("vae_approx",            [s => s.DefaultVaeApproxFolder]),
    ];

    public static Dictionary<string, string> Build(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, sources) in Mappings)
        {
            foreach (var source in sources)
            {
                var value = source(settings);
                if (string.IsNullOrWhiteSpace(value)) continue;

                overrides[key] = value;
                break;
            }
        }

        return overrides;
    }
}
