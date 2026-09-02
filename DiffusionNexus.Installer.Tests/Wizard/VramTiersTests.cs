using DiffusionNexus.Installer.Core.Wizard;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

public class VramTiersTests
{
    [Theory]
    [InlineData("8,12,16,24,32", new[] { 8, 12, 16, 24, 32 })]
    [InlineData("24,32", new[] { 24, 32 })]
    [InlineData("8,16,24,24+", new[] { 8, 16, 24 })]
    [InlineData(" 32 , 8 ,8", new[] { 8, 32 })]
    [InlineData("16GB,24+GB", new[] { 16, 24 })]
    [InlineData("-8,0,12", new[] { 12 })]
    public void Parses_the_tiers_a_workload_declares_and_nothing_else(string profiles, int[] expected)
        => VramTiers.Parse(profiles).Should().Equal(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    [InlineData("abc")]
    public void Junk_yields_no_tiers_rather_than_throwing(string? profiles)
        => VramTiers.Parse(profiles).Should().BeEmpty();
}
