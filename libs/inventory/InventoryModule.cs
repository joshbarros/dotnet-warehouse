using Warehouse.Shared;
using Warehouse.Shared.Contracts;
using Warehouse.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Warehouse.Inventory;

public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Warehouse") ?? throw new InvalidOperationException("Connection string 'Warehouse' is not configured.");
        services.AddDbContext<InventoryDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", InventoryDbContext.SchemaName)));
        services.AddScoped<InventoryHandler>();
        services.AddScoped<ISkuCatalog, SkuCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<InventoryDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<InventoryDbContext>>("stock-db", tags: ["ready"]);

        messaging.AddOutbox<InventoryDbContext>()
            .AddConsumer<InventoryHandler>(Topology.InventoryQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
