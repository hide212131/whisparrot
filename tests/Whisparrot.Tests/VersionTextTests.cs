using Whisparrot.Core;
using Xunit;

namespace Whisparrot.Tests;

public class VersionTextTests
{
    [Theory]
    [InlineData("0.2.0", "v0.2.0")]
    [InlineData("0.2.0+562e757abc", "v0.2.0")]
    [InlineData("0.2.0-rc.1+build.5", "v0.2.0-rc.1")]
    [InlineData("v0.2.0", "v0.2.0")]
    [InlineData("  0.2.0  ", "v0.2.0")]
    public void Informational_version_is_trimmed_and_prefixed(string input, string expected)
        => Assert.Equal(expected, VersionText.Format(input, new Version(9, 9, 9, 0)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+abc")]
    public void Falls_back_to_assembly_version(string? input)
        => Assert.Equal("v0.2.0", VersionText.Format(input, new Version(0, 2, 0, 0)));

    [Fact]
    public void Falls_back_to_zero_when_nothing_is_available()
        => Assert.Equal("v0.0.0", VersionText.Format(null, null));
}
