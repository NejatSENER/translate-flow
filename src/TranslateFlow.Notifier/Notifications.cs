using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using TranslateFlow.Core;
using TranslateFlow.Core.Data;
using TranslateFlow.Core.Domain;
using TranslateFlow.Core.Messaging;

namespace TranslateFlow.Notifier;

public record Email(string To, string Subject, string Body);

public interface IEmailSender
{
    Task SendAsync(Email email, CancellationToken ct);
}

public class SmtpOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string From { get; set; } = "TranslateFlow <no-reply@translateflow.local>";
    public string PublicUrl { get; set; } = "http://localhost:8080";
}

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(Email email, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(options.Value.From));
        message.To.Add(MailboxAddress.Parse(email.To));
        message.Subject = email.Subject;
        message.Body = new TextPart("plain") { Text = email.Body };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(options.Value.Host, options.Value.Port, MailKit.Security.SecureSocketOptions.None, ct);
        await smtp.SendAsync(new MailKit.SendRequest(message), ct);
        await smtp.DisconnectAsync(true, ct);
    }
}

/// <summary>
/// Emails the user when a job finishes. The job row is marked as notified before sending,
/// so a duplicate message does not send a second email; if sending fails the mark is removed
/// and the message is retried.
/// </summary>
public sealed class JobFinishedHandler(
    TranslateFlowDbContext db,
    IEmailSender sender,
    TimeProvider clock,
    IOptions<SmtpOptions> options,
    ILogger<JobFinishedHandler> logger) : IMessageHandler<JobFinished>
{
    public async Task HandleAsync(JobFinished message, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var claimed = await db.Jobs
            .Where(j => j.Id == message.JobId && j.NotifiedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.NotifiedAt, now), ct);
        if (claimed == 0)
        {
            logger.LogInformation("Job {JobId} was already notified, skipping duplicate delivery", message.JobId);
            return;
        }

        var job = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == message.JobId, ct);
        try
        {
            await sender.SendAsync(Compose(job), ct);
        }
        catch
        {
            await db.Jobs.Where(j => j.Id == job.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(j => j.NotifiedAt, (DateTimeOffset?)null), CancellationToken.None);
            throw;
        }
        logger.LogInformation("Notified {Email} about job {JobId}", job.Email, job.Id);
    }

    private Email Compose(TranslationJob job)
    {
        var baseUrl = options.Value.PublicUrl.TrimEnd('/');
        var download = $"{baseUrl}/api/jobs/{job.Id}/result";
        var status = $"{baseUrl}/?email={Uri.EscapeDataString(job.Email)}";
        var name = job.FileName ?? "your text";
        var subjectName = job.FileName is null ? "" : $" of {job.FileName}";
        return job.Status == JobStatus.Completed
            ? new Email(job.Email, $"Your translation{subjectName} is ready",
                $"""
                Your {job.SourceLanguage} → {job.TargetLanguage} translation is ready.

                Model: {job.Model}
                Tokens: {job.InputTokens} in, {job.OutputTokens} out
                Cost: ${job.Cost:0.000000}

                Download: {download}
                Your jobs: {status}
                """)
            : new Email(job.Email, $"Your translation{subjectName} failed",
                $"""
                We could not translate {name}.

                Reason: {job.Error}

                Your jobs: {status}
                """);
    }
}

public static class NotifierSetup
{
    public static IServiceCollection AddNotifier(this IServiceCollection services, IConfiguration config)
    {
        services.AddTranslateFlowCore(config);
        services.Configure<SmtpOptions>(config.GetSection("Smtp"));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddConsumer<JobFinished, JobFinishedHandler>(Routes.NotificationsQueue);
        return services;
    }
}
