namespace AI.MacroSystem.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

using AI.MacroSystem.Infrastructure.Persistence;
using AI.MacroSystem.Infrastructure.Persistence.Repositories;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        return services;
    }

    public static IServiceCollection AddSqlitePersistence(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<ISqliteConnectionFactory>(_ => new SqliteConnectionFactory(connectionString));
        services.AddSingleton<DatabaseMigrator>();
        services.AddScoped<IWorkflowRepository, WorkflowRepository>();
        services.AddScoped<IExecutionRepository, ExecutionRepository>();
        services.AddScoped<ISystemLockRepository, SystemLockRepository>();
        return services;
    }
}
