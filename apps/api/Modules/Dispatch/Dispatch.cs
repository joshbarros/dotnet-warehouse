using Warehouse.Api.Data;
using Warehouse.Api.Messaging;
using Warehouse.Api.Modules.Orders;
using Microsoft.EntityFrameworkCore;

namespace Warehouse.Api.Modules.Dispatch;

public enum SlipStatus { Issued, Rejected }

public class PickSlip
{
    public Guid OrderId { get; set; }
    public string? Number { get; set; }
    public SlipStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the pick slip once the stock is held. A poison reference is committed as rejected.</summary>
public class DispatchHandler(WarehouseDbContext db) : IMessageHandler
{
    public const string Name = "dispatch";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Dispatch does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<StockHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, StockHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (OrderRules.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Slips.Add(new PickSlip
            {
                OrderId = held.OrderId,
                Status = SlipStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("dispatch", Topology.ReleaseKey, held.OrderId, new ReleaseStock(held.OrderId, "poison"));
            db.Enqueue("dispatch", Topology.RejectedKey, held.OrderId, new SlipRejected(held.OrderId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"PS-{held.OrderId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Slips.Add(new PickSlip
            {
                OrderId = held.OrderId,
                Number = number,
                Status = SlipStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("dispatch", Topology.IssuedKey, held.OrderId, new SlipIssued(held.OrderId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
