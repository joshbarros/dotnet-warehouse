using Warehouse.Shared;
using Warehouse.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Warehouse.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Warehouse") ?? throw new InvalidOperationException("Connection string 'Warehouse' is not configured.");
        services.AddDbContext<OrdersDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", OrdersDbContext.SchemaName)));
        services.Configure<OrderOptions>(config.GetSection("Orders"));
        services.AddScoped<OrderService>();
        services.AddScoped<OrdersHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<OrdersDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<OrdersDbContext>>("orders-db", tags: ["ready"]);

        messaging.AddOutbox<OrdersDbContext>()
            .AddConsumer<OrdersHandler>(Topology.OrdersQueue,
                [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], prefetch: 4);
        return services;
    }
}
