using System.Globalization;
using Capstone.Core;
using Xunit;

namespace Capstone.UnitTests;

public sealed class TransferRequestTests
{
    private static readonly TransferRequest Valid =
        new(Guid.NewGuid(), Guid.NewGuid(), 1, 2, 30.00m, "NZD");

    [Theory]
    [InlineData("30.00", true)]
    [InlineData("0.01", true)]
    [InlineData("0.001", false)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("100000000000000000", false)]
    public void ValidatesExactAmountAndRange(string amount, bool expected)
    {
        var request = Valid with { Amount = decimal.Parse(amount, CultureInfo.InvariantCulture) };
        Assert.Equal(expected, request.IsValid);
    }

    [Fact]
    public void ValidatesIdentifiersAndCurrency()
    {
        Assert.True(Valid.IsValid);
        Assert.False((Valid with { SourceAccount = 2 }).IsValid);
        Assert.False((Valid with { DestinationAccount = 0 }).IsValid);
        Assert.False((Valid with { Currency = "USD" }).IsValid);
        Assert.False((Valid with { AttemptId = Guid.Empty }).IsValid);
        Assert.False((Valid with { LogicalRequestId = Guid.Empty }).IsValid);
    }
}
