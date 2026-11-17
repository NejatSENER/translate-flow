using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TranslateFlow.Core.Data;
using TranslateFlow.Core.Domain;
using TranslateFlow.Core.Messaging;
using TranslateFlow.Core.Storage;
using TranslateFlow.Processing.Extraction;
using TranslateFlow.Processing.Translation;

namespace TranslateFlow.Worker;

public class WorkerOptions
{
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(10);
    public int MaxChunkTokens { get; set; } = 800;
}

/// <summary>
/// Extract, translate, count tokens, store the result, announce it. Messages can arrive more
/// than once, so the job is claimed with a conditional update before any work starts.
/// </summary>
public sealed class TranslationJobHandler(
    TranslateFlowDbContext db,
    IFileStore store,
    ITextExtractor extractor,
    ITranslator translator,
    ITokenCounter tokens,
    TextChunker chunker,
    TimeProvider clock,
    IOptions<WorkerOptions> options,
    ILogger<TranslationJobHandler> logger) : IMessageHandler<JobSubmitted>
{
    public async Task HandleAsync(JobSubmitted message, CancellationToken ct)
    {
        if (!await TryClaimAsync(message.JobId, ct))
        {
            logger.LogInformation("Job {JobId} is already taken or finished, skipping duplicate delivery", message.JobId);
            return;
        }

        var job = await db.Jobs.FirstAsync(j => j.Id == message.JobId, ct);
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id });
        try
        {
            await ProcessAsync(job, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Job {JobId} failed", job.Id);
            await FinishAsync(job, JobStatus.Failed, ex is UnsupportedFileException or JobRejectedException
                ? ex.Message
                : "Translation failed. Please try again later.", ct);
        }
    }

    private async Task ProcessAsync(TranslationJob job, CancellationToken ct)
    {
        string text;
        await using (var input = await store.OpenReadAsync(job.InputKey, ct))
            text = await extractor.ExtractAsync(input, job.FileName ?? "input.txt", ct);
        if (string.IsNullOrWhiteSpace(text)) throw new JobRejectedException("No text could be found in the file.");

        var route = await db.ModelRoutes.AsNoTracking()
            .FirstOrDefaultAsync(r => r.SourceLanguage == job.SourceLanguage && r.TargetLanguage == job.TargetLanguage, ct)
            ?? throw new JobRejectedException($"Translating from '{job.SourceLanguage}' to '{job.TargetLanguage}' is not supported.");

        job.Status = JobStatus.Translating;
        job.CharacterCount = text.Length;
        job.Model = route.Model;
        job.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var chunks = chunker.Split(text, options.Value.MaxChunkTokens);
        var translated = new List<string>(chunks.Count);
        int inputTokens = 0, outputTokens = 0;
        foreach (var chunk in chunks)
        {
            var result = await translator.TranslateAsync(chunk, job.SourceLanguage, job.TargetLanguage, route.Model, ct);
            translated.Add(result);
            inputTokens += tokens.Count(chunk);
            outputTokens += tokens.Count(result);
        }

        job.ResultKey = $"results/{job.Id}.txt";
        await store.SaveTextAsync(job.ResultKey, string.Join("\n\n", translated), ct);
        job.InputTokens = inputTokens;
        job.OutputTokens = outputTokens;
        job.Cost = route.CostOf(inputTokens, outputTokens);
        await FinishAsync(job, JobStatus.Completed, null, ct);
    }

    /// <summary>Status change and the "finished" message are saved together (outbox).</summary>
    private async Task FinishAsync(TranslationJob job, JobStatus status, string? error, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        job.Status = status;
        job.Error = error;
        job.CompletedAt = now;
        job.UpdatedAt = now;
        job.LeaseUntil = null;
        db.Outbox.Add(Outbox.Message(Routes.JobFinished, new JobFinished(job.Id, status), now));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Takes a queued job, or one whose previous worker died mid-way (its lease ran out).
    /// Two deliveries of the same message cannot both win this update.
    /// </summary>
    private async Task<bool> TryClaimAsync(Guid jobId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var leaseUntil = now + options.Value.Lease;
        var claimed = await db.Database.ExecuteSqlAsync($"""
            UPDATE translation_jobs
            SET status = 'Extracting', attempts = attempts + 1, started_at = COALESCE(started_at, {now}),
                lease_until = {leaseUntil}, updated_at = {now}
            WHERE id = {jobId}
              AND (status = 'Queued' OR (status IN ('Extracting', 'Translating') AND lease_until < {now}))
            """, ct);
        return claimed == 1;
    }
}

public class JobRejectedException(string message) : Exception(message);
