using DiffusionNexus.Installer.SDK.Services.Installation.Existing;

namespace DiffusionNexus.Installer.Core.Existing;

/// <summary>What to tell someone whose folder can't be updated.</summary>
public static class SupportMessages
{
    public static string For(ExistingInstallSupport support) => support switch
    {
        ExistingInstallSupport.NotComfyUI => "This folder does not contain ComfyUI (no main.py).",
        ExistingInstallSupport.Portable => "Portable ComfyUI is not supported yet. Do a fresh install instead.",
        ExistingInstallSupport.Desktop => "ComfyUI Desktop (the download from comfy.org) is not supported yet. Do a fresh install instead.",
        ExistingInstallSupport.NoVenv => "No Python virtual environment found in this folder (conda and similar are not supported yet). Do a fresh install instead.",
        ExistingInstallSupport.NoPip => "This ComfyUI's Python environment has no pip (uv makes them that way), so nothing can be installed into it. Add pip to it, or do a fresh install.",
        ExistingInstallSupport.Supported => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(support), support, null),
    };
}
