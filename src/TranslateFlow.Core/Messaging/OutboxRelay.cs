using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TranslateFlow.Core.Data;
using TranslateFlow.Core.Domain;

namespace TranslateFlow.Core.Messaging;

public class OutboxOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int BatchSize { get; set; } = 100;
}

/// <summary>
/// Publishes outbox rows to RabbitMQ and marks them sent. Delivery is at-least-once: if the
/// process dies after publishing but before committing, the row is sent again, so consumers
/// must be idempotent. <c>SKIP LOCKED</c> lets the API and the worker both run a relay.
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopes,
    RabbitConnection connection,
    IOptions<RabbitMqOptions> rabbit,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        using var timer = new PeriodicTimer(options.Value.PollInterval);
        do
        {
            try
            {
                if (channel is not { IsOpen: true })
                {
                    var conn = await connection.GetAsync(stoppingToken);
                    channel = await conn.CreateChannelAsync(
                        new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                        stoppingToken);
                    await RabbitTopology.DeclareAsync(channel, rabbit.Value.Exchange, stoppingToken);
                }
                while (await PublishBatchAsync(channel, stoppingToken) == options.Value.BatchSize) { }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Outbox relay iteration failed, will retry");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).AsTask().ContinueWith(t => !t.IsCanceled && t.Result, TaskScheduler.Default));

        if (channel is not null) await channel.DisposeAsync();
    }

    private async Task<int> PublishBatchAsync(IChannel channel, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslateFlowDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.Outbox.FromSql($"""
            SELECT * FROM outbox_messages
            WHERE published_at IS NULL
            ORDER BY created_at
            LIMIT {options.Value.BatchSize}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        foreach (var message in batch)
        {
            try
            {
                var props = new BasicProperties
                {
                    MessageId = message.Id.ToString(),
                    Type = message.Type,
                    ContentType = "application/json",
                    DeliveryMode = DeliveryModes.Persistent,
                };
                // With publisher confirms enabled this returns only after the broker accepted the message.
                await channel.BasicPublishAsync(rabbit.Value.Exchange, message.RoutingKey, mandatory: true, props,
                    Encoding.UTF8.GetBytes(message.Payload), ct);
                message.PublishedAt = clock.GetUtcNow();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                logger.LogWarning(ex, "Publishing outbox message {Id} failed", message.Id);
                break;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch.Count(m => m.PublishedAt is not null);
    }
}
