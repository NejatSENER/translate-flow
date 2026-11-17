using System.Net.Mail;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TranslateFlow.Core.Data;
using TranslateFlow.Core.Domain;
using TranslateFlow.Core.Messaging;
using TranslateFlow.Core.Storage;

namespace TranslateFlow.Api;

public record JobResponse(Guid Id, string Status, string SourceLanguage, string TargetLanguage, string? FileName,
    string? Model, int? CharacterCount, int? InputTokens, int? OutputTokens, decimal? Cost, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, bool Notified);

public record UsageResponse(string Email, int Jobs, int Completed, int Failed, long InputTokens, long OutputTokens, decimal Cost);

public record RouteResponse(string SourceLanguage, string TargetLanguage, string Model, decimal InputPricePerMillion, decimal OutputPricePerMillion);

public static class JobEndpoints
{
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".docx", ".pdf" };

    public static void MapJobEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapPost("/jobs", Submit).DisableAntiforgery();
        api.MapGet("/jobs/{id:guid}", Get);
        api.MapGet("/jobs/{id:guid}/result", Result);
        api.MapGet("/jobs", List);
        api.MapGet("/usage", Usage);
        api.MapGet("/routes", Routes);
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    }

    /// <summary>
    /// Stores the input, then saves the job and its "submitted" message in one transaction.
    /// The request returns 202 at once; the worker picks the job up from the queue.
    /// </summary>
    private static async Task<IResult> Submit(
        [FromForm] string email, [FromForm] string sourceLanguage, [FromForm] string targetLanguage,
        [FromForm] string? text, IFormFile? file,
        TranslateFlowDbContext db, IFileStore store, TimeProvider clock, CancellationToken ct)
    {
        if (!MailAddress.TryCreate(email, out _))
            return Problem(400, "invalid_email", "A valid email address is required.");
        if ((file is null) == string.IsNullOrWhiteSpace(text))
            return Problem(400, "invalid_input", "Send either a file or a text, not both.");

        sourceLanguage = sourceLanguage.Trim().ToLowerInvariant();
        targetLanguage = targetLanguage.Trim().ToLowerInvariant();
        if (!await db.ModelRoutes.AnyAsync(r => r.SourceLanguage == sourceLanguage && r.TargetLanguage == targetLanguage, ct))
            return Problem(422, "unsupported_language_pair", $"Translating from '{sourceLanguage}' to '{targetLanguage}' is not supported. See /api/routes.");

        var id = Guid.CreateVersion7();
        string key, fileName;
        if (file is not null)
        {
            var ext = Path.GetExtension(file.FileName);
            if (!Extensions.Contains(ext)) return Problem(415, "unsupported_file", "Upload a .txt, .docx or .pdf file.");
            if (file.Length is 0 or > MaxUploadBytes) return Problem(413, "file_too_large", "The file must be between 1 byte and 10 MB.");
            fileName = Path.GetFileName(file.FileName);
            key = $"inputs/{id}{ext.ToLowerInvariant()}";
            await using var upload = file.OpenReadStream();
            await store.SaveAsync(key, upload, ct);
        }
        else
        {
            fileName = "text.txt";
            key = $"inputs/{id}.txt";
            await store.SaveTextAsync(key, text!, ct);
        }

        var now = clock.GetUtcNow();
        var job = new TranslationJob
        {
            Id = id, Email = email.Trim(), SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage,
            FileName = file is null ? null : fileName, InputKey = key, CreatedAt = now, UpdatedAt = now,
        };
        db.Jobs.Add(job);
        db.Outbox.Add(Outbox.Message(Core.Messaging.Routes.JobSubmitted, new JobSubmitted(id), now));
        await db.SaveChangesAsync(ct);

        return Results.Accepted($"/api/jobs/{id}", ToResponse(job));
    }

    private static async Task<IResult> Get(Guid id, TranslateFlowDbContext db, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        return job is null ? Results.NotFound() : Results.Ok(ToResponse(job));
    }

    private static async Task<IResult> Result(Guid id, TranslateFlowDbContext db, IFileStore store, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return Results.NotFound();
        if (job.Status != JobStatus.Completed || job.ResultKey is null)
            return Problem(409, "not_ready", $"The job is {job.Status.ToString().ToLowerInvariant()}.");
        var text = await store.ReadTextAsync(job.ResultKey, ct);
        return Results.Text(text, "text/plain; charset=utf-8");
    }

    private static async Task<IResult> List(string email, TranslateFlowDbContext db, CancellationToken ct)
    {
        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => j.Email == email)
            .OrderByDescending(j => j.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
        return Results.Ok(jobs.Select(ToResponse));
    }

    /// <summary>Token usage and cost per user, the numbers a billing or quota system would read.</summary>
    private static async Task<IResult> Usage(string email, TranslateFlowDbContext db, CancellationToken ct)
    {
        var jobs = db.Jobs.AsNoTracking().Where(j => j.Email == email);
        var usage = new UsageResponse(
            email,
            await jobs.CountAsync(ct),
            await jobs.CountAsync(j => j.Status == JobStatus.Completed, ct),
            await jobs.CountAsync(j => j.Status == JobStatus.Failed, ct),
            await jobs.SumAsync(j => (long)(j.InputTokens ?? 0), ct),
            await jobs.SumAsync(j => (long)(j.OutputTokens ?? 0), ct),
            await jobs.SumAsync(j => j.Cost ?? 0, ct));
        return Results.Ok(usage);
    }

    private static async Task<IResult> Routes(TranslateFlowDbContext db, CancellationToken ct) =>
        Results.Ok(await db.ModelRoutes.AsNoTracking()
            .OrderBy(r => r.SourceLanguage).ThenBy(r => r.TargetLanguage)
            .Select(r => new RouteResponse(r.SourceLanguage, r.TargetLanguage, r.Model, r.InputPricePerMillion, r.OutputPricePerMillion))
            .ToListAsync(ct));

    private static JobResponse ToResponse(TranslationJob j) => new(
        j.Id, j.Status.ToString(), j.SourceLanguage, j.TargetLanguage, j.FileName, j.Model, j.CharacterCount,
        j.InputTokens, j.OutputTokens, j.Cost, j.Error, j.CreatedAt, j.CompletedAt, j.NotifiedAt is not null);

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
