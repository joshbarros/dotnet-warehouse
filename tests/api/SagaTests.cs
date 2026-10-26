using Warehouse.Api.Messaging;
using Warehouse.Api.Modules.Orders;
using Warehouse.Api.Modules.Inventory;
using Warehouse.Api.Modules.Dispatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Warehouse.Api.Tests;

public class SagaTests
{
    private static OrdersHandler Orders() =>
        new(TestDatabase.Create(), Options.Create(new OrderOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        var db = await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(db, 5);
        var order = await TestDatabase.AddOrderAsync(db, sku, "ORD-POISON-1", 2, OrderStatus.Held);
        var held = new StockHeld(order.Id, sku.Id, 2, order.Reference);

        var handler = new DispatchHandler(TestDatabase.Create());
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            handler.HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        var check = TestDatabase.Create();
        var doc = await check.Slips.SingleAsync(d => d.OrderId == order.Id);
        Assert.Equal(SlipStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Cancellation_ThenFinalMark_LeavesOrderCancelledAndSlipIssued()
    {
        var db = await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(db, 5);
        var order = await TestDatabase.AddOrderAsync(db, sku, "ORD-1", 2, OrderStatus.Held);

        // The pick slip is issued; the final mark has not landed yet.
        var number = "PS-TEST";
        await new DispatchHandler(TestDatabase.Create()).IssueAsync(
            Guid.CreateVersion7(), new StockHeld(order.Id, sku.Id, 2, order.Reference));

        // The cancellation gets there first.
        await new OrderService(TestDatabase.Create()).CancelAsync(order.Id, default);

        var mark = await Orders().MarkDispatchedAsync(Guid.CreateVersion7(), new SlipIssued(order.Id, number));

        var check = TestDatabase.Create();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(OrderStatus.Cancelled, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(SlipStatus.Issued, (await check.Slips.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        var db = await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(db, 5);
        var held = await TestDatabase.AddOrderAsync(db, sku, "ORD-A", 1, OrderStatus.Held);
        var rejected = await TestDatabase.AddOrderAsync(db, sku, "ORD-B", 1, OrderStatus.Rejected);
        var requested = await TestDatabase.AddOrderAsync(db, sku, "ORD-C", 1, OrderStatus.Requested);

        Assert.Equal(MarkOutcome.Marked, await Orders().MarkDispatchedAsync(Guid.CreateVersion7(), new SlipIssued(held.Id, "PS-1")));
        Assert.Equal(MarkOutcome.Skipped, await Orders().MarkDispatchedAsync(Guid.CreateVersion7(), new SlipIssued(rejected.Id, "PS-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await Assert.ThrowsAsync<OutOfOrderException>(() =>
            Orders().MarkDispatchedAsync(Guid.CreateVersion7(), new SlipIssued(requested.Id, "PS-3")));

        var statuses = (await TestDatabase.Create().Orders.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(OrderStatus.Dispatched, statuses["ORD-A"]);
        Assert.Equal(OrderStatus.Rejected, statuses["ORD-B"]);
        Assert.Equal(OrderStatus.Requested, statuses["ORD-C"]);
    }

    [Fact]
    public async Task Cancellation_OfARejectedOrder_Conflicts()
    {
        var db = await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(db, 5);
        var order = await TestDatabase.AddOrderAsync(db, sku, "ORD-1", 1, OrderStatus.Rejected);

        var ex = await Assert.ThrowsAsync<DomainException>(() => new OrderService(TestDatabase.Create()).CancelAsync(order.Id, default));
        Assert.Equal(409, ex.Status);
    }
}
