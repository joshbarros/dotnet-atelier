using Atelier.Orders;
using Xunit;

namespace Atelier.UnitTests;

public class OrderRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("ATT-1", 0)]
    [InlineData("ATT-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long seats) =>
        Assert.NotNull(OrderRules.Validate(reference, seats));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(OrderRules.Validate("ATT-1", OrderRules.MaxSeatsPerOrder));
        Assert.NotNull(OrderRules.Validate("ATT-1", OrderRules.MaxSeatsPerOrder + 1));
        Assert.NotNull(OrderRules.Validate(new string('x', OrderRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)] // (int) of this wraps to 1; it must not pass as one seat
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long seats)
    {
        Assert.NotNull(OrderRules.Validate("ATT-1", seats));
        Assert.Equal(1, unchecked((int)4_294_967_297L)); // documents what the naive cast would have done
    }

    [Fact]
    public void HappyTotal_IsFaceValuePlusATwelvePercentServiceFee()
    {
        Assert.Null(OrderRules.Validate("ATT-1", 2));
        Assert.Equal(10_000L + 1_200L, OrderRules.TotalCents(5_000, 2)); // $50.00 x 2 + $12.00 fee
        Assert.Equal(1L, OrderRules.TotalCents(1, 1)); // the fee truncates to zero
        Assert.Equal(7L, OrderRules.TotalCents(7, 1)); // 12% of 7 is 0.84 cents, truncated to 0
        Assert.Equal(112L, OrderRules.TotalCents(100, 1)); // 100 + 12
    }

    [Fact]
    public void ABundle_PaysSeatAndKitPerSeat_ThenTheFee()
    {
        // 2 seats at $50.00 with a $15.00 kit each: 2 x 6500 = 13000, plus 12% = 14560
        Assert.Equal(13_000L + 1_560L, OrderRules.TotalCents(5_000 + 1_500, 2));
    }

    [Fact]
    public void TotalArithmetic_ThrowsInsteadOfWrapping()
    {
        Assert.Throws<OverflowException>(() => OrderRules.TotalCents(long.MaxValue, 2));
        Assert.Throws<OverflowException>(() => OrderRules.TotalCents(long.MaxValue / 2, 2)); // the fee multiplication overflows
    }
}
