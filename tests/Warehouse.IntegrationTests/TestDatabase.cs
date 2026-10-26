using Warehouse.Orders;
using Warehouse.Inventory;
using Warehouse.Dispatch;
using Warehouse.Shared.Auth;
using Warehouse.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Warehouse.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("WAREHOUSE_TEST_DB")
        ?? "Host=localhost;Port=5473;Database=warehouse_test;Username=warehouse;Password=warehouse";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static OrdersDbContext Orders() => new(Options<OrdersDbContext>(OrdersDbContext.SchemaName));
    public static InventoryDbContext Inventory() => new(Options<InventoryDbContext>(InventoryDbContext.SchemaName));
    public static DispatchDbContext Dispatch() => new(Options<DispatchDbContext>(DispatchDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var orders = Orders();
        await using var stock = Inventory();
        await using var dispatch = Dispatch();
        await orders.Database.MigrateAsync();
        await stock.Database.MigrateAsync();
        await dispatch.Database.MigrateAsync();
        await orders.Database.ExecuteSqlRawAsync(
            "TRUNCATE orders.\"InboxMessages\", orders.\"OutboxMessages\", orders.\"Orders\", " +
            "stock.\"InboxMessages\", stock.\"OutboxMessages\", stock.\"StockHolds\", stock.\"Skus\", " +
            "dispatch.\"InboxMessages\", dispatch.\"OutboxMessages\", dispatch.\"PickSlips\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Sku> AddSkuAsync(int receivedUnits)
    {
        await using var db = Inventory();
        var sku = new Sku
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "MV Test",
            Category = "Hardware",
            Bin = "A-01",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(7),
            ReceivedUnits = receivedUnits,
            OnHandUnits = receivedUnits,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Skus.Add(sku);
        await db.SaveChangesAsync();
        return sku;
    }

    public static async Task<Order> AddOrderAsync(Sku sku, string reference, int units, OrderStatus status, Guid? ownerId = null)
    {
        await using var db = Orders();
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
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

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Sku[] skus) : ISkuCatalog
{
    public Task<SkuInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(skus.Where(s => s.Code == code).Select(s => new SkuInfo(s.Id, s.Code)).FirstOrDefault());
}
