using System.Collections.Concurrent;
using Warehouse.Inventory;
using Warehouse.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Warehouse.IntegrationTests;

public class InventoryConcurrencyTests
{
    [Fact]
    public async Task LastUnit_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(receivedUnits: 1);

        var outcomes = new ConcurrentBag<(Guid Order, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var orderId = Guid.CreateVersion7();
            await using var db = TestDatabase.Inventory();
            var outcome = await new InventoryHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveStock(orderId, Guid.NewGuid(), sku.Id, 1, $"ORD-{n}"));
            outcomes.Add((orderId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Inventory();
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
        await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(receivedUnits: 4);
        var orderId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Inventory())
            await new InventoryHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveStock(orderId, Guid.NewGuid(), sku.Id, 3, "ORD-1"));

        // Two different commands (poison compensation and a cancellation) both ask for the same units back.
        await using var first = TestDatabase.Inventory();
        await using var second = TestDatabase.Inventory();
        var one = await new InventoryHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseStock(orderId, "poison"));
        var two = await new InventoryHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseStock(orderId, "cancellation"));

        await using var check = TestDatabase.Inventory();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Skus.Where(s => s.Id == sku.Id).Select(s => s.OnHandUnits).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(receivedUnits: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveStock(Guid.CreateVersion7(), Guid.NewGuid(), sku.Id, 2, "ORD-1");

        await using var a = TestDatabase.Inventory();
        await using var b = TestDatabase.Inventory();
        var first = await new InventoryHandler(a).ReserveAsync(messageId, command);
        var second = await new InventoryHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Inventory();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Skus.Where(s => s.Id == sku.Id).Select(s => s.OnHandUnits).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var sku = await TestDatabase.AddSkuAsync(receivedUnits: 2);
        await using var db = TestDatabase.Inventory();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Skus.Where(s => s.Id == sku.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OnHandUnits, x => x.OnHandUnits - 3)));
    }
}
