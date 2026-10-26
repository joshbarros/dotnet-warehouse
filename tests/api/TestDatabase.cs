using Warehouse.Api.Data;
using Warehouse.Api.Modules.Orders;
using Warehouse.Api.Modules.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Warehouse.Api.Tests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("WAREHOUSE_TEST_DB")
        ?? "Host=localhost;Port=5473;Database=warehouse_test;Username=warehouse;Password=warehouse";

    private static readonly DbContextOptions<WarehouseDbContext> Options =
        new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(ConnectionString).Options;

    public static WarehouseDbContext Create() => new(Options);

    public static async Task<WarehouseDbContext> OpenAsync()
    {
        var db = Create();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"InboxMessages\", \"OutboxMessages\", \"PickSlips\", \"StockHolds\", \"Orders\", \"Skus\" RESTART IDENTITY CASCADE;");
        return db;
    }

    public static async Task<Sku> AddSkuAsync(WarehouseDbContext db, int receivedUnits)
    {
        var sku = new Sku
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "MV Test",
            Category = "Hardware",
            Bin = "Bin A-01",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(7),
            ReceivedUnits = receivedUnits,
            OnHandUnits = receivedUnits,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Skus.Add(sku);
        await db.SaveChangesAsync();
        return sku;
    }

    public static async Task<Order> AddOrderAsync(WarehouseDbContext db, Sku sku, string reference, int units, OrderStatus status)
    {
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            SkuId = sku.Id,
            SkuCode = sku.Code,
            Reference = reference,
            Units = units,
            FeeCents = OrderRules.FeeCents(units),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }
}
