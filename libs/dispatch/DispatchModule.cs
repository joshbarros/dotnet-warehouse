using Warehouse.Shared;
using Warehouse.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Warehouse.Dispatch;

public static class DispatchModule
{
    public static IServiceCollection AddDispatchModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Warehouse") ?? throw new InvalidOperationException("Connection string 'Warehouse' is not configured.");
        services.AddDbContext<DispatchDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", DispatchDbContext.SchemaName)));
        services.AddScoped<DispatchHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<DispatchDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<DispatchDbContext>>("dispatch-db", tags: ["ready"]);

        messaging.AddOutbox<DispatchDbContext>()
            .AddConsumer<DispatchHandler>(Topology.DispatchQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
