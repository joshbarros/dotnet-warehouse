using Warehouse.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Warehouse.Inventory;

public class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "stock";

    public DbSet<Sku> Skus => Set<Sku>();
    public DbSet<StockHold> Holds => Set<StockHold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

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
    }
}
