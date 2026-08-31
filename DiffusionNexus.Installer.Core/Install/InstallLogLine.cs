using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;

namespace DiffusionNexus.Installer.Core.Install;

public sealed record InstallLogLine(DateTimeOffset Timestamp, string Message, SdkLogLevel Level);
