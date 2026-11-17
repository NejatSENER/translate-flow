namespace TranslateFlow.Core.Domain;

public enum JobStatus { Queued, Extracting, Translating, Completed, Failed }

public class TranslationJob
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Email { get; init; }
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public string? FileName { get; init; }
    public required string InputKey { get; init; }
    public string? ResultKey { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string? Error { get; set; }
    public int Attempts { get; set; }

    public string? Model { get; set; }
    public int? CharacterCount { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public decimal? Cost { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }
}

/// <summary>Which model translates a language pair, and what it costs per million tokens.</summary>
public class ModelRoute
{
    public int Id { get; init; }
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public required string Model { get; init; }
    public decimal InputPricePerMillion { get; init; }
    public decimal OutputPricePerMillion { get; init; }

    public decimal CostOf(int inputTokens, int outputTokens) =>
        Math.Round((inputTokens * InputPricePerMillion + outputTokens * OutputPricePerMillion) / 1_000_000m, 6);
}

public class OutboxMessage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Type { get; init; }
    public required string RoutingKey { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
