using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class SerialNumbersTests
{
    [Theory]
    [InlineData("ZL2ABCDE", "…BCDE")]
    [InlineData("  WD-WCC4N1234567 ", "…4567")]
    // Too short to show a tail without showing all of it.
    [InlineData("AB12", "…")]
    [InlineData("", "…")]
    public void KeepsOnlyTheTail(string serial, string expected)
    {
        Assert.Equal(expected, SerialNumbers.Mask(serial));
    }

    [Fact]
    public void MaskingTwiceChangesNothingAndNoSerialStaysNone()
    {
        // The database comes to hold masked serials, and they are masked again on the way out.
        Assert.Equal("…BCDE", SerialNumbers.Mask(SerialNumbers.Mask("ZL2ABCDE")));
        Assert.Equal("…", SerialNumbers.Mask("…"));
        Assert.Null(SerialNumbers.Mask((string?)null));
    }
}
