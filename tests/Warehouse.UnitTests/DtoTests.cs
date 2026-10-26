using Warehouse.Orders;
using Warehouse.Inventory;
using Xunit;

namespace Warehouse.UnitTests;

public class DtoTests
{
    [Fact]
    public void OrderDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            SkuId = Guid.NewGuid(),
            SkuCode = "S-1",
            Reference = "ORD-1",
            Units = 3,
            FeeCents = 277_500,
            Status = OrderStatus.Dispatched,
            SlipNumber = "PS-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = OrderDto.From(order);

        Assert.Equal(order.Id, dto.Id);
        Assert.Equal(order.OwnerId, dto.OwnerId);
        Assert.Equal("Dispatched", dto.Status);
        Assert.Equal(277_500, dto.FeeCents);
        Assert.Equal("PS-1", dto.SlipNumber);
    }

    [Fact]
    public void SkuDto_MapsTheScarceNumber()
    {
        var sku = new Sku { Id = Guid.NewGuid(), Code = "S-1", Name = "v", Category = "o", Bin = "d", ReceivedUnits = 10, OnHandUnits = 4 };
        var dto = SkuDto.From(sku);
        Assert.Equal(10, dto.ReceivedUnits);
        Assert.Equal(4, dto.OnHandUnits);
    }
}
