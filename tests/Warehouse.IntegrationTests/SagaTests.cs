using Warehouse.Orders;
using Warehouse.Dispatch;
using Warehouse.Shared;
using Warehouse.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Warehouse.IntegrationTests;

public class SagaTests
{
    private static OrdersHandler Handler(OrdersDbContext db) => new(db, Options.Create(new OrderOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(5);
        var order = await TestDatabase.AddOrderAsync(sku, "ORD-POISON-1", 2, OrderStatus.Held);
        var held = new StockHeld(order.Id, order.OwnerId, sku.Id, 2, order.Reference);

        await using var docs = TestDatabase.Dispatch();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new DispatchHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Dispatch();
        var doc = await check.PickSlips.SingleAsync(d => d.OrderId == order.Id);
        Assert.Equal(SlipStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(order.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Cancellation_ThenFinalMark_LeavesOrderCancelledAndSlipIssued()
    {
        await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(5);
        var owner = Guid.CreateVersion7();
        var order = await TestDatabase.AddOrderAsync(sku, "ORD-1", 2, OrderStatus.Held, owner);

        // The pick slip is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Dispatch())
            await new DispatchHandler(docs).IssueAsync(Guid.CreateVersion7(), new StockHeld(order.Id, owner, sku.Id, 2, order.Reference));

        // The owner cancels first.
        await using (var orders = TestDatabase.Orders())
            await new OrderService(orders, new FixedCatalog(sku), new TestUser(owner)).CancelAsync(order.Id, default);

        await using var late = TestDatabase.Orders();
        var mark = await Handler(late).MarkDispatchedAsync(Guid.CreateVersion7(), new SlipIssued(order.Id, "PS-TEST"));

        await using var check = TestDatabase.Orders();
        await using var checkDocs = TestDatabase.Dispatch();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(OrderStatus.Cancelled, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(SlipStatus.Issued, (await checkDocs.PickSlips.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(5);
        var held = await TestDatabase.AddOrderAsync(sku, "ORD-A", 1, OrderStatus.Held);
        var rejected = await TestDatabase.AddOrderAsync(sku, "ORD-B", 1, OrderStatus.Rejected);
        var requested = await TestDatabase.AddOrderAsync(sku, "ORD-C", 1, OrderStatus.Requested);

        await using (var a = TestDatabase.Orders())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkDispatchedAsync(Guid.CreateVersion7(), new SlipIssued(held.Id, "PS-1")));
        await using (var b = TestDatabase.Orders())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkDispatchedAsync(Guid.CreateVersion7(), new SlipIssued(rejected.Id, "PS-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Orders())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkDispatchedAsync(Guid.CreateVersion7(), new SlipIssued(requested.Id, "PS-3")));

        await using var check = TestDatabase.Orders();
        var statuses = (await check.Orders.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(OrderStatus.Dispatched, statuses["ORD-A"]);
        Assert.Equal(OrderStatus.Rejected, statuses["ORD-B"]);
        Assert.Equal(OrderStatus.Requested, statuses["ORD-C"]);
    }

    [Fact]
    public async Task Cancellation_OfARejectedOrder_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(5);
        var owner = Guid.CreateVersion7();
        var order = await TestDatabase.AddOrderAsync(sku, "ORD-1", 1, OrderStatus.Rejected, owner);

        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(sku), new TestUser(owner)).CancelAsync(order.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Cancellation_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(5);
        var order = await TestDatabase.AddOrderAsync(sku, "ORD-1", 1, OrderStatus.Held);

        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(sku), new TestUser(Guid.NewGuid())).CancelAsync(order.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Orders();
        Assert.Equal(OrderStatus.Held, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanCancelAnyonesOrder()
    {
        await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(5);
        var order = await TestDatabase.AddOrderAsync(sku, "ORD-1", 1, OrderStatus.Held);

        await using var db = TestDatabase.Orders();
        var cancelled = await new OrderService(db, new FixedCatalog(sku), new TestUser(Guid.NewGuid(), staff: true)).CancelAsync(order.Id, default);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Orders();
        var order = await new OrderService(db, new FixedCatalog(sku), new TestUser(owner))
            .CreateAsync(new CreateOrderRequest(sku.Code, "ORD-1", 2), default);

        await using var check = TestDatabase.Orders();
        Assert.Equal(owner, (await check.Orders.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveStock>(message.Payload).OwnerId);
        Assert.Equal(OrderStatus.Requested, order.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(5);
        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(sku), new TestUser(null)).CreateAsync(new CreateOrderRequest(sku.Code, "ORD-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownSku_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreateOrderRequest("NOPE", "ORD-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
