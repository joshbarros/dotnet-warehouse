using System.Collections.Concurrent;
using Warehouse.Api.Messaging;
using Warehouse.Api.Modules.Inventory;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Warehouse.Api.Tests;

public class InventoryConcurrencyTests
{
    [Fact]
    public async Task LastUnit_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        var db = await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(db, receivedUnits: 1);

        var outcomes = new ConcurrentBag<(Guid Order, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var orderId = Guid.CreateVersion7();
            var handler = new InventoryHandler(TestDatabase.Create());
            var outcome = await handler.ReserveAsync(
                Guid.CreateVersion7(), new ReserveStock(orderId, sku.Id, 1, $"ORD-{n}"));
            outcomes.Add((orderId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        var check = TestDatabase.Create();
        var remaining = await check.Skus.Where(s => s.Id == sku.Id).Select(s => s.OnHandUnits).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Order;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.OrderId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheUnitBackExactlyOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(db, receivedUnits: 4);
        var orderId = Guid.CreateVersion7();
        var handler = new InventoryHandler(TestDatabase.Create());
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveStock(orderId, sku.Id, 3, "ORD-1"));

        // Two different commands (poison compensation and a cancellation) both ask for the same units back.
        var first = await new InventoryHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseStock(orderId, "poison"));
        var second = await new InventoryHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseStock(orderId, "cancellation"));

        var remaining = await TestDatabase.Create().Skus.Where(s => s.Id == sku.Id).Select(s => s.OnHandUnits).FirstAsync();
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(4, remaining);
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(db, receivedUnits: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveStock(Guid.CreateVersion7(), sku.Id, 2, "ORD-1");

        var first = await new InventoryHandler(TestDatabase.Create()).ReserveAsync(messageId, command);
        var second = await new InventoryHandler(TestDatabase.Create()).ReserveAsync(messageId, command);

        var remaining = await TestDatabase.Create().Skus.Where(s => s.Id == sku.Id).Select(s => s.OnHandUnits).FirstAsync();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, remaining);
    }
}
