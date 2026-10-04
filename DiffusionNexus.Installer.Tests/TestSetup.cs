using System.Runtime.CompilerServices;
using Bunit;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests;

/// <summary>Settings for the whole test assembly, applied before any test runs.</summary>
internal static class TestSetup
{
    /// <summary>
    /// bUnit's WaitForAssertion gives up after 1 second by default. Tests that wait for work the
    /// page starts off the render thread (the model presence scan, the server message banner)
    /// failed now and then on a busy CI runner while passing locally. A passing assertion returns
    /// as soon as it passes, so only a test that is failing anyway waits longer.
    /// </summary>
#pragma warning disable CA2255 // A test assembly is the place this is meant for.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);
}

public class TestSetupTests
{
    [Fact]
    public void The_bunit_wait_timeout_is_raised_for_every_test()
        => BunitContext.DefaultWaitTimeout.Should().Be(TimeSpan.FromSeconds(10));
}
