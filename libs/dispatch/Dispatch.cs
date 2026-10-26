using Warehouse.Shared;
using Warehouse.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Warehouse.Dispatch;

public enum SlipStatus { Issued, Rejected }

public class PickSlip
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public SlipStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class DispatchDbContext(DbContextOptions<DispatchDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "dispatch";

    public DbSet<PickSlip> PickSlips => Set<PickSlip>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<PickSlip>(e =>
        {
            e.ToTable("PickSlips");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the pick slip once the stock is held. A poison reference is committed as rejected.</summary>
public class DispatchHandler(DispatchDbContext db) : IMessageHandler
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
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.PickSlips.Add(new PickSlip
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
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
            db.PickSlips.Add(new PickSlip
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
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
