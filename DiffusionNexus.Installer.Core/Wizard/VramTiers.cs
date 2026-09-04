using System.Globalization;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>
/// The one parser for a workload's comma-separated VRAM tier list ("8,12,16,24,32", "24,32",
/// "8,16,24,24+"). Shared by the installability gate (WorkloadCapabilities.Detect) and
/// VramProfileModule so the two can never disagree about whether a workload has tiers.
/// </summary>
public static class VramTiers
{
    /// <summary>Distinct, ascending, in GB. Unparseable entries are dropped; "24+" means 24.</summary>
    public static IReadOnlyList<int> Parse(string? profiles)
    {
        if (string.IsNullOrWhiteSpace(profiles)) return [];

        var tiers = new SortedSet<int>();

        foreach (var raw in profiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw;
            if (token.EndsWith("GB", StringComparison.OrdinalIgnoreCase)) token = token[..^2];
            token = token.TrimEnd().TrimEnd('+').TrimEnd();
            if (token.EndsWith("GB", StringComparison.OrdinalIgnoreCase)) token = token[..^2].TrimEnd();

            if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var gb) && gb > 0)
                tiers.Add(gb);
        }

        return [.. tiers];
    }
}
