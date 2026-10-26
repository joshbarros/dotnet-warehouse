using Warehouse.Api.Messaging;
using Warehouse.Api.Modules.Orders;
using Warehouse.Api.Modules.Inventory;
using Warehouse.Api.Modules.Dispatch;
using Microsoft.EntityFrameworkCore;

namespace Warehouse.Api.Data;

public class WarehouseDbContext(DbContextOptions<WarehouseDbContext> options) : DbContext(options)
{
    public DbSet<Sku> Skus => Set<Sku>();
    public DbSet<StockHold> Holds => Set<StockHold>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<PickSlip> Slips => Set<PickSlip>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Sku>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_Skus_Remaining", "\"OnHandUnits\" >= 0 AND \"OnHandUnits\" <= \"ReceivedUnits\""));
        });

        b.Entity<StockHold>(e =>
        {
            e.ToTable("StockHolds");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Sku>().WithMany().HasForeignKey(x => x.SkuId);
        });

        b.Entity<Order>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(OrderRules.MaxReferenceLength);
            e.HasIndex(x => new { x.SkuId, x.Reference }).IsUnique();
            e.HasOne<Sku>().WithMany().HasForeignKey(x => x.SkuId);
        });

        b.Entity<PickSlip>(e =>
        {
            e.ToTable("PickSlips");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
    }
}
