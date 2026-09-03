using DiffusionNexus.Installer.SDK.Models.Installation;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>One per-type model folder ComfyUI knows: its YAML key, a display label and the standard name.</summary>
/// <param name="Key">The extra_model_paths.yaml key, e.g. <c>loras</c>.</param>
/// <param name="Label">What the folders page calls it, e.g. <c>LoRAs</c>.</param>
/// <param name="Standard">ComfyUI's default folder name for the type; always equal to <paramref name="Key"/> today.</param>
public sealed record FolderTypeDefinition(string Key, string Label, string Standard);

/// <summary>
/// Maps the ~20 per-type Default*Folder values in UserSettings onto the ComfyUI folder keys
/// extra_model_paths.yaml and ModelDestinationResolver use, in both directions.
/// <para>
/// Several keys have two settings fields: a newer plural one and a legacy singular one left over
/// from an earlier schema. Reading, the plural wins when both are set, which is the order the
/// Avalonia installer's own mapper used — dropping the fallback would silently ignore an older
/// user's stored folders. Writing fills both, as the classic Folder Settings window did, so the
/// 1.x apps that still read the singular field see the same value.
/// </para>
/// </summary>
public static class UserModelFolderMap
{
    private sealed record Mapping(
        FolderTypeDefinition Type,
        Func<UserSettings, string?>[] Sources,
        Action<UserSettings, string>[] Sinks);

    /// <summary>
    /// Folder key -> settings fields, most current first. Kept as data rather than a hundred lines
    /// of if/else so a new folder type is one line. The order is the classic dialog's: the types
    /// people actually customize first, the exotic ones last.
    /// </summary>
    private static readonly Mapping[] Mappings =
    [
        new(new("checkpoints", "Checkpoints", "checkpoints"),
            [s => s.DefaultCheckpointsFolder, s => s.DefaultCheckpointFolder],
            [(s, v) => s.DefaultCheckpointsFolder = v, (s, v) => s.DefaultCheckpointFolder = v]),
        new(new("diffusion_models", "Diffusion Models", "diffusion_models"),
            [s => s.DefaultDiffusionModelsFolder, s => s.DefaultDiffusionModelFolder],
            [(s, v) => s.DefaultDiffusionModelsFolder = v, (s, v) => s.DefaultDiffusionModelFolder = v]),
        new(new("loras", "LoRAs", "loras"),
            [s => s.DefaultLorasFolder, s => s.DefaultLoraFolder],
            [(s, v) => s.DefaultLorasFolder = v, (s, v) => s.DefaultLoraFolder = v]),
        new(new("vae", "VAE", "vae"),
            [s => s.DefaultVAEFolder],
            [(s, v) => s.DefaultVAEFolder = v]),
        new(new("embeddings", "Embeddings", "embeddings"),
            [s => s.DefaultEmbeddingsFolder, s => s.DefaultEmbeddingFolder],
            [(s, v) => s.DefaultEmbeddingsFolder = v, (s, v) => s.DefaultEmbeddingFolder = v]),
        new(new("text_encoders", "Text Encoders", "text_encoders"),
            [s => s.DefaultTextEncodersFolder, s => s.DefaultTextencoderFolder],
            [(s, v) => s.DefaultTextEncodersFolder = v, (s, v) => s.DefaultTextencoderFolder = v]),
        new(new("upscale_models", "Upscale Models", "upscale_models"),
            [s => s.DefaultUpscaleModelsFolder, s => s.DefaultUpscalerFolder],
            [(s, v) => s.DefaultUpscaleModelsFolder = v, (s, v) => s.DefaultUpscalerFolder = v]),
        new(new("controlnet", "ControlNet", "controlnet"),
            [s => s.DefaultControlNetFolder],
            [(s, v) => s.DefaultControlNetFolder = v]),
        new(new("audio_encoders", "Audio Encoders", "audio_encoders"),
            [s => s.DefaultAudioEncodersFolder],
            [(s, v) => s.DefaultAudioEncodersFolder = v]),
        new(new("clip", "Clip", "clip"),
            [s => s.DefaultClipFolder],
            [(s, v) => s.DefaultClipFolder = v]),
        new(new("clip_vision", "Clip Vision", "clip_vision"),
            [s => s.DefaultClipVisionFolder],
            [(s, v) => s.DefaultClipVisionFolder = v]),
        new(new("configs", "Configs", "configs"),
            [s => s.DefaultConfigsFolder],
            [(s, v) => s.DefaultConfigsFolder = v]),
        new(new("diffusers", "Diffusers", "diffusers"),
            [s => s.DefaultDiffusersFolder],
            [(s, v) => s.DefaultDiffusersFolder = v]),
        new(new("gligen", "Gligen", "gligen"),
            [s => s.DefaultGligenFolder],
            [(s, v) => s.DefaultGligenFolder = v]),
        new(new("hypernetworks", "Hypernetworks", "hypernetworks"),
            [s => s.DefaultHypernetworksFolder],
            [(s, v) => s.DefaultHypernetworksFolder = v]),
        new(new("latent_upscale_models", "Latent Upscale", "latent_upscale_models"),
            [s => s.DefaultLatentUpscaleModelsFolder],
            [(s, v) => s.DefaultLatentUpscaleModelsFolder = v]),
        new(new("model_patches", "Model Patches", "model_patches"),
            [s => s.DefaultModelPatchesFolder],
            [(s, v) => s.DefaultModelPatchesFolder = v]),
        new(new("photomaker", "Photomaker", "photomaker"),
            [s => s.DefaultPhotomakerFolder],
            [(s, v) => s.DefaultPhotomakerFolder = v]),
        new(new("style_models", "Style Models", "style_models"),
            [s => s.DefaultStyleModelsFolder],
            [(s, v) => s.DefaultStyleModelsFolder = v]),
        new(new("unet", "UNet", "unet"),
            [s => s.DefaultUnetFolder],
            [(s, v) => s.DefaultUnetFolder = v]),
        new(new("vae_approx", "VAE Approx", "vae_approx"),
            [s => s.DefaultVaeApproxFolder],
            [(s, v) => s.DefaultVaeApproxFolder = v]),
    ];

    /// <summary>Every folder type, in display order.</summary>
    public static IReadOnlyList<FolderTypeDefinition> FolderTypes { get; } =
        Mappings.Select(m => m.Type).ToArray();

    /// <summary>Reads the saved per-type folder names; types with no saved value are absent.</summary>
    public static Dictionary<string, string> Build(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in Mappings)
        {
            foreach (var source in mapping.Sources)
            {
                var value = source(settings);
                if (string.IsNullOrWhiteSpace(value)) continue;

                overrides[mapping.Type.Key] = value;
                break;
            }
        }

        return overrides;
    }

    /// <summary>
    /// Writes per-type folder names into the settings. A type missing from <paramref name="values"/>
    /// is blanked in every field that carries it: this is what makes "Reset to standard" stick.
    /// </summary>
    public static void Apply(UserSettings settings, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(values);

        foreach (var mapping in Mappings)
        {
            var value = values.TryGetValue(mapping.Type.Key, out var v) ? v : string.Empty;
            foreach (var sink in mapping.Sinks)
                sink(settings, value);
        }
    }
}
