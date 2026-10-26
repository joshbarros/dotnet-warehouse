using Warehouse.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Warehouse.Inventory;

public enum ReserveOutcome { Held, Short, Duplicate }

/// <summary>
/// The only writer of Sku.OnHandUnits and of stock holds.
/// The reservation is one conditional UPDATE: it matches only while the remaining Units still covers the request.
/// </summary>
public class InventoryHandler(InventoryDbContext db) : IMessageHandler
{
    public const string Name = "inventory";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveStock>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseStock>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Inventory does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveStock command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var updated = await db.Skus
            .Where(s => s.Id == command.SkuId && s.OnHandUnits >= command.Units)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OnHandUnits, x => x.OnHandUnits - command.Units), ct);

        ReserveOutcome outcome;
        if (updated == 1)
        {
            db.Holds.Add(new StockHold
            {
                OrderId = command.OrderId,
                SkuId = command.SkuId,
                Units = command.Units,
                Status = HoldStatus.Held,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("inventory", Topology.HeldKey, command.OrderId,
                new StockHeld(command.OrderId, command.OwnerId, command.SkuId, command.Units, command.Reference));
            outcome = ReserveOutcome.Held;
        }
        else
        {
            var remaining = await db.Skus.Where(s => s.Id == command.SkuId)
                .Select(s => s.OnHandUnits).FirstOrDefaultAsync(ct);
            db.Enqueue("inventory", Topology.ShortKey, command.OrderId,
                new StockShort(command.OrderId, command.Reference, command.Units, remaining));
            outcome = ReserveOutcome.Short;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Gives a held unit back exactly once. A second release for the same order changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseStock command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.OrderId == command.OrderId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.OrderId == command.OrderId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Skus
                    .Where(s => s.Id == hold.SkuId && s.OnHandUnits + hold.Units <= s.ReceivedUnits)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.OnHandUnits, x => x.OnHandUnits + hold.Units), ct);
                db.Enqueue("inventory", Topology.ReleasedKey, command.OrderId,
                    new StockReleased(command.OrderId, hold.Units, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
