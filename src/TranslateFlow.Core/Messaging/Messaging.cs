using System.Text.Json;
using TranslateFlow.Core.Domain;

namespace TranslateFlow.Core.Messaging;

public record JobSubmitted(Guid JobId);

public record JobFinished(Guid JobId, JobStatus Status);

public static class Routes
{
    public const string JobSubmitted = "job.submitted";
    public const string JobFinished = "job.finished";

    public const string JobsQueue = "translation-jobs";
    public const string NotificationsQueue = "notifications";
}

public class RabbitMqOptions
{
    public string Exchange { get; set; } = "translateflow";
    public ushort Prefetch { get; set; } = 4;
}

public static class Outbox
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates a message to be saved in the same transaction as the business change. The relay
    /// publishes it later, so a crash between "saved" and "published" cannot lose the message.
    /// </summary>
    public static OutboxMessage Message<T>(string routingKey, T payload, DateTimeOffset now) => new()
    {
        Type = typeof(T).Name,
        RoutingKey = routingKey,
        Payload = JsonSerializer.Serialize(payload, Json),
        CreatedAt = now,
    };
}
