using Warehouse.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Warehouse.Orders;

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move an order forward. Each step runs only from the status it expects.</summary>
public class OrdersHandler(OrdersDbContext db, IOptions<OrderOptions> options) : IMessageHandler
{
    public const string Name = "orders";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<StockHeld>(d.Body).OrderId, ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<StockShort>(d.Body), ct),
        Topology.IssuedKey => MarkDispatchedAsync(d.MessageId, Json.Parse<SlipIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<SlipRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<StockReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Orders does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid orderId, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == orderId && b.Status == OrderStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Held)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, StockShort m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.Status == OrderStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Rejected)
                .SetProperty(b => b.Reason, $"Short: {m.RequestedUnits} units requested, {m.OnHandUnits} remaining.")
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The final mark. If a cancellation got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkDispatchedAsync(Guid messageId, SlipIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.Status == OrderStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Dispatched)
                .SetProperty(b => b.SlipNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            orderIdForOrderCheck: m.OrderId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, SlipRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && (b.Status == OrderStatus.Held || b.Status == OrderStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, StockReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.UnitsReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.UnitsReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? orderIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && orderIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not drop.
            var status = await db.Orders.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == OrderStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
