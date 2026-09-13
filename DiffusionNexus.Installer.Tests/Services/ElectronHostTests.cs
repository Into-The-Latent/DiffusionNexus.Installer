using DiffusionNexus.Installer.Electron.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Services;

/// <summary>
/// The single Electron-detection probe the UI uses. Was three hand-copied try/catch blocks with
/// doc comments that had already drifted apart.
/// </summary>
public class ElectronHostTests
{
    [Fact]
    public void Reports_not_active_rather_than_throwing_when_the_host_cannot_be_detected()
    {
        // HybridSupport.IsElectronActive reads an AssemblyMetadataAttribute the ElectronNET build
        // stamps onto the Electron project's output. Under a test host the entry assembly carries
        // no such attribute and the property THROWS. Without the catch this line is an exception,
        // and every component that probes the host takes its screen down with it.
        ElectronHost.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Answers_repeatedly_without_poisoning_itself()
    {
        // A throwing static constructor poisons the type for the whole process, so the first
        // caller to hit the gap must not break every later one.
        ElectronHost.IsActive.Should().BeFalse();
        ElectronHost.IsActive.Should().BeFalse();
    }
}
