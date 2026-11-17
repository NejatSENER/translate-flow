using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace TranslateFlow.Core.Messaging;

/// <summary>One long-lived connection per process, created on first use.</summary>
public sealed class RabbitConnection(Uri uri) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken ct = default)
    {
        if (_connection is { IsOpen: true }) return _connection;
        await _lock.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;
            var factory = new ConnectionFactory { Uri = uri, ClientProvidedName = AppDomain.CurrentDomain.FriendlyName };
            _connection = await factory.CreateConnectionAsync(ct);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }
}

public static class RabbitTopology
{
    private static string DeadLetterExchange(string exchange) => exchange + ".dlx";

    /// <summary>
    /// Declares the exchange, both work queues and their dead-letter queues. Idempotent: every
    /// process can call it at startup. Messages that fail for good end up in "*.dead" for inspection.
    /// </summary>
    public static async Task DeclareAsync(IChannel channel, string exchange, CancellationToken ct)
    {
        var dlx = DeadLetterExchange(exchange);
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(dlx, ExchangeType.Direct, durable: true, cancellationToken: ct);

        foreach (var (queue, key) in new[] { (Routes.JobsQueue, Routes.JobSubmitted), (Routes.NotificationsQueue, Routes.JobFinished) })
        {
            var args = new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = dlx,
                ["x-dead-letter-routing-key"] = key,
            };
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: args, cancellationToken: ct);
            await channel.QueueBindAsync(queue, exchange, key, cancellationToken: ct);

            await channel.QueueDeclareAsync(queue + ".dead", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
            await channel.QueueBindAsync(queue + ".dead", dlx, key, cancellationToken: ct);
        }
    }
}

public interface IMessageHandler<in T>
{
    Task HandleAsync(T message, CancellationToken ct);
}

/// <summary>
/// Consumes one queue with manual acks. A handler that throws gets one more try (requeue);
/// a second failure, or a message that cannot be read, goes to the dead-letter queue.
/// </summary>
public sealed class RabbitConsumer<T>(
    string queue,
    RabbitConnection connection,
    IServiceScopeFactory scopes,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitConsumer<T>> logger) : BackgroundService
{
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var conn = await connection.GetAsync(stoppingToken);
        _channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);
        await RabbitTopology.DeclareAsync(_channel, options.Value.Exchange, stoppingToken);
        await _channel.BasicQosAsync(0, options.Value.Prefetch, false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, delivery) => OnMessageAsync(delivery, stoppingToken);
        await _channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task OnMessageAsync(BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var channel = _channel!;
        T? message;
        try
        {
            message = JsonSerializer.Deserialize<T>(delivery.Body.Span, Outbox.Json);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Unreadable message {MessageId} on {Queue}, dead-lettering", delivery.BasicProperties.MessageId, queue);
            await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: false, ct);
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IMessageHandler<T>>().HandleAsync(message!, ct);
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var requeue = !delivery.Redelivered;
            logger.LogError(ex, "Handling {MessageId} failed, {Action}", delivery.BasicProperties.MessageId, requeue ? "retrying once" : "dead-lettering");
            await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue, ct);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null) await _channel.DisposeAsync();
    }
}
