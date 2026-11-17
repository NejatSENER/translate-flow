using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TranslateFlow.Core.Data;
using TranslateFlow.Core.Messaging;
using TranslateFlow.Core.Storage;

namespace TranslateFlow.Core;

public static class CoreServices
{
    /// <summary>Database, file store, RabbitMQ connection and the outbox relay. Shared by every service.</summary>
    public static IServiceCollection AddTranslateFlowCore(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RabbitMqOptions>(config.GetSection("RabbitMq"));
        services.Configure<OutboxOptions>(config.GetSection("Outbox"));
        services.TryAddSingletonTimeProvider();

        services.AddDbContext<TranslateFlowDbContext>(o => o
            .UseNpgsql(config.GetConnectionString("Postgres"))
            .UseSnakeCaseNamingConvention());

        services.AddSingleton<IFileStore>(_ => new LocalFileStore(config["Storage:Root"] ?? Path.Combine(Path.GetTempPath(), "translateflow")));
        services.AddSingleton(_ => new RabbitConnection(new Uri(config.GetConnectionString("RabbitMq")!)));
        services.AddHostedService<OutboxRelay>();
        return services;
    }

    public static IServiceCollection AddConsumer<TMessage, THandler>(this IServiceCollection services, string queue)
        where THandler : class, IMessageHandler<TMessage>
    {
        services.AddScoped<IMessageHandler<TMessage>, THandler>();
        services.AddHostedService(sp => new RabbitConsumer<TMessage>(
            queue,
            sp.GetRequiredService<RabbitConnection>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<RabbitMqOptions>>(),
            sp.GetRequiredService<ILogger<RabbitConsumer<TMessage>>>()));
        return services;
    }

    /// <summary>Applies migrations when <c>Database:MigrateOnStartup</c> is true (development and compose).</summary>
    public static async Task MigrateIfEnabledAsync(this IHost host)
    {
        var config = host.Services.GetRequiredService<IConfiguration>();
        if (!config.GetValue("Database:MigrateOnStartup", false)) return;
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TranslateFlowDbContext>().Database.MigrateAsync();
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(TimeProvider))) services.AddSingleton(TimeProvider.System);
    }
}
