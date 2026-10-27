using Atelier.Shared;
using Atelier.Shared.Contracts;
using Atelier.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atelier.Workshops;

public static class WorkshopsModule
{
    public static IServiceCollection AddWorkshopsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Atelier") ?? throw new InvalidOperationException("Connection string 'Atelier' is not configured.");
        services.AddDbContext<WorkshopsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", WorkshopsDbContext.SchemaName)));
        services.AddScoped<WorkshopsHandler>();
        services.AddScoped<IWorkshopCatalog, WorkshopCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<WorkshopsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<WorkshopsDbContext>>("seats-db", tags: ["ready"]);

        messaging.AddOutbox<WorkshopsDbContext>()
            .AddConsumer<WorkshopsHandler>(Topology.WorkshopsQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
