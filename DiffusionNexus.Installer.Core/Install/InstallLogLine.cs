using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;

namespace DiffusionNexus.Installer.Core.Install;

public sealed record InstallLogLine(DateTimeOffset Timestamp, string Message, SdkLogLevel Level);

/// <summary>
/// The log as it was at one instant: the lines the bounded buffer still holds and how many older
/// ones it has dropped, read under ONE lock. Two separate reads can disagree during a pip flood --
/// forty lines arrive and forty are dropped between them -- and the copied text then claims a
/// different start than it has.
/// </summary>
public sealed record InstallLogSnapshot(IReadOnlyList<InstallLogLine> Lines, int TruncatedLines);
