using Warehouse.Orders;
using Xunit;

namespace Warehouse.UnitTests;

public class OrderRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("ORD-1", 0)]
    [InlineData("ORD-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long units) =>
        Assert.NotNull(OrderRules.Validate(reference, units));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(OrderRules.Validate("ORD-1", OrderRules.MaxUnitsPerOrder));
        Assert.NotNull(OrderRules.Validate("ORD-1", OrderRules.MaxUnitsPerOrder + 1));
        Assert.NotNull(OrderRules.Validate(new string('x', OrderRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)] // (int) of this wraps to 1; it must not pass as one Units
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long units)
    {
        Assert.NotNull(OrderRules.Validate("ORD-1", units));
        Assert.Equal(1, unchecked((int)4_294_967_297L)); // dispatch what the naive cast would have done
    }

    [Fact]
    public void HappyFee_IsWarehousePerUnitsPlusDocumentationFee()
    {
        Assert.Null(OrderRules.Validate("ORD-1", 3));
        Assert.Equal(3 * 150L + 400L, OrderRules.FeeCents(3));
        Assert.Equal(150L + 400L, OrderRules.FeeCents(1));
    }
}
