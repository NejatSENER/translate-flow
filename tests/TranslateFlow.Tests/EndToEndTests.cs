using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TranslateFlow.Api;
using TranslateFlow.Core.Data;
using TranslateFlow.Core.Domain;
using TranslateFlow.Core.Messaging;
using TranslateFlow.Notifier;
using TranslateFlow.Worker;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace TranslateFlow.Tests;

public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<Email> Sent { get; } = new();

    public Task SendAsync(Email email, CancellationToken ct)
    {
        Sent.Enqueue(email);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Real PostgreSQL and RabbitMQ in containers, the API in memory, and the worker and notifier
/// as two separate hosts, the same three processes that run in production.
/// </summary>
public sealed class SystemFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management").Build();
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "translateflow-tests-" + Guid.NewGuid().ToString("N"));
    private IHost? _worker, _notifier;

    public WebApplicationFactory<Program> Api { get; private set; } = null!;
    public CapturingEmailSender Emails { get; } = new();

    private Dictionary<string, string?> Settings => new()
    {
        ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
        ["ConnectionStrings:RabbitMq"] = _rabbit.GetConnectionString(),
        ["Storage:Root"] = _storage,
        ["Outbox:PollInterval"] = "00:00:00.200",
        ["Translation:Provider"] = "Pseudo",
        ["Worker:MaxChunkTokens"] = "50",
    };

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        var settings = Settings;

        Api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            foreach (var (k, v) in settings) b.UseSetting(k, v);
            b.UseSetting("Database:MigrateOnStartup", "true");
        });
        Api.CreateClient();

        _worker = await StartHostAsync(s => s.AddTranslationWorker(Configuration(settings)));
        _notifier = await StartHostAsync(s =>
        {
            s.AddNotifier(Configuration(settings));
            s.AddSingleton<IEmailSender>(Emails);
        });
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static async Task<IHost> StartHostAsync(Action<IServiceCollection> configure)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        configure(builder.Services);
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    public async Task<T> WithDbAsync<T>(Func<TranslateFlowDbContext, Task<T>> work)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TranslateFlowDbContext>());
    }

    public async Task DisposeAsync()
    {
        foreach (var host in new[] { _worker, _notifier })
        {
            if (host is null) continue;
            await host.StopAsync();
            host.Dispose();
        }
        await Api.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }
}

[CollectionDefinition("system")]
public class SystemCollection : ICollectionFixture<SystemFixture>;

[Collection("system")]
public class EndToEndTests(SystemFixture system)
{
    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private async Task<JobResponse> SubmitAsync(string email, string source, string target, string? text = null, (byte[] Bytes, string Name)? file = null)
    {
        var response = await PostAsync(email, source, target, text, file);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JobResponse>())!;
    }

    private Task<HttpResponseMessage> PostAsync(string email, string source, string target, string? text = null, (byte[] Bytes, string Name)? file = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(email), "email" },
            { new StringContent(source), "sourceLanguage" },
            { new StringContent(target), "targetLanguage" },
        };
        if (text is not null) form.Add(new StringContent(text), "text");
        if (file is { } f) form.Add(new ByteArrayContent(f.Bytes), "file", f.Name);
        return system.Api.CreateClient().PostAsync("/api/jobs", form);
    }

    private async Task<JobResponse> WaitForAsync(Guid id, Func<JobResponse, bool> done)
    {
        var client = system.Api.CreateClient();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var job = (await client.GetFromJsonAsync<JobResponse>($"/api/jobs/{id}"))!;
            if (done(job)) return job;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Job {id} stuck in {job.Status}");
            await Task.Delay(100);
        }
    }

    private Email[] EmailsTo(string address) => system.Emails.Sent.Where(e => e.To == address).ToArray();

    [Fact]
    public async Task Text_job_is_translated_costed_and_emailed()
    {
        var email = NewEmail();
        var submitted = await SubmitAsync(email, "tr", "en", text: "Merhaba dünya.\n\nİkinci paragraf.");
        Assert.Equal("Queued", submitted.Status);

        var job = await WaitForAsync(submitted.Id, j => j.Notified);

        Assert.Equal("Completed", job.Status);
        Assert.Equal("qwen2.5:7b", job.Model);
        Assert.True(job.InputTokens > 0 && job.OutputTokens > 0);
        Assert.True(job.Cost > 0);
        var result = await system.Api.CreateClient().GetStringAsync($"/api/jobs/{job.Id}/result");
        Assert.Equal("[EN] Merhaba dünya.\n\n[EN] İkinci paragraf.", result);
        var mail = Assert.Single(EmailsTo(email));
        Assert.Contains("is ready", mail.Subject);
    }

    [Fact]
    public async Task Docx_upload_is_extracted_and_long_text_is_chunked()
    {
        var paragraphs = Enumerable.Range(1, 12).Select(i => $"Paragraf {i}: kargo yarın sabah teslim edilecek ve şubeye bırakılabilir.").ToArray();
        var submitted = await SubmitAsync(NewEmail(), "tr", "en", file: (Samples.Docx(paragraphs), "kargo.docx"));

        var job = await WaitForAsync(submitted.Id, j => j.Status is "Completed" or "Failed");

        Assert.Equal("Completed", job.Status);
        Assert.Equal("kargo.docx", job.FileName);
        var result = await system.Api.CreateClient().GetStringAsync($"/api/jobs/{job.Id}/result");
        Assert.Contains("Paragraf 12:", result);
        Assert.Contains("[EN] ", result);
    }

    [Fact]
    public async Task A_pdf_without_text_fails_and_the_user_is_told_why()
    {
        var email = NewEmail();
        var submitted = await SubmitAsync(email, "en", "tr", file: (Samples.Pdf(), "blank.pdf"));

        var job = await WaitForAsync(submitted.Id, j => j.Notified);

        Assert.Equal("Failed", job.Status);
        Assert.Equal("No text could be found in the file.", job.Error);
        Assert.Contains("failed", Assert.Single(EmailsTo(email)).Subject);
    }

    [Fact]
    public async Task Invalid_requests_are_rejected_before_queueing()
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(NewEmail(), "tr", "ja", text: "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("not-an-email", "tr", "en", text: "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(NewEmail(), "tr", "en")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await PostAsync(NewEmail(), "tr", "en", file: ([1, 2], "photo.png"))).StatusCode);
    }

    [Fact]
    public async Task Duplicate_messages_do_not_translate_or_email_twice()
    {
        var email = NewEmail();
        var submitted = await SubmitAsync(email, "en", "de", text: "Hello.");
        await WaitForAsync(submitted.Id, j => j.Notified);

        // The relay delivers at least once; simulate it publishing both messages again.
        await system.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Outbox.Add(Outbox.Message(Routes.JobSubmitted, new JobSubmitted(submitted.Id), now));
            db.Outbox.Add(Outbox.Message(Routes.JobFinished, new JobFinished(submitted.Id, JobStatus.Completed), now));
            return await db.SaveChangesAsync();
        });
        await WaitUntilOutboxEmptyAsync();
        await Task.Delay(1000);

        var attempts = await system.WithDbAsync(db => db.Jobs.Where(j => j.Id == submitted.Id).Select(j => j.Attempts).FirstAsync());
        Assert.Equal(1, attempts);
        Assert.Single(EmailsTo(email));
    }

    [Fact]
    public async Task A_job_left_behind_by_a_crashed_worker_is_picked_up_after_its_lease()
    {
        var email = NewEmail();
        var id = Guid.CreateVersion7();
        await system.WithDbAsync(async db =>
        {
            var past = DateTimeOffset.UtcNow.AddMinutes(-30);
            await File.WriteAllTextAsync(Path.Combine(StorageRoot(), "inputs", $"{id}.txt"), "Stuck job.");
            db.Jobs.Add(new TranslationJob
            {
                Id = id, Email = email, SourceLanguage = "en", TargetLanguage = "tr", InputKey = $"inputs/{id}.txt",
                Status = JobStatus.Translating, Attempts = 1, LeaseUntil = past.AddMinutes(10), CreatedAt = past, UpdatedAt = past,
            });
            db.Outbox.Add(Outbox.Message(Routes.JobSubmitted, new JobSubmitted(id), DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });

        var job = await WaitForAsync(id, j => j.Status == "Completed");

        Assert.Equal("Completed", job.Status);
        Assert.Equal(2, await system.WithDbAsync(db => db.Jobs.Where(j => j.Id == id).Select(j => j.Attempts).FirstAsync()));
    }

    [Fact]
    public async Task Usage_sums_tokens_and_cost_per_user()
    {
        var email = NewEmail();
        var a = await SubmitAsync(email, "tr", "en", text: "Bir.");
        var b = await SubmitAsync(email, "tr", "en", text: "İki cümle var burada.");
        await WaitForAsync(a.Id, j => j.Status == "Completed");
        var last = await WaitForAsync(b.Id, j => j.Status == "Completed");
        var first = (await system.Api.CreateClient().GetFromJsonAsync<JobResponse>($"/api/jobs/{a.Id}"))!;

        var usage = (await system.Api.CreateClient().GetFromJsonAsync<UsageResponse>($"/api/usage?email={Uri.EscapeDataString(email)}"))!;

        Assert.Equal(2, usage.Completed);
        Assert.Equal((long)(first.InputTokens!.Value + last.InputTokens!.Value), usage.InputTokens);
        Assert.Equal(first.Cost!.Value + last.Cost!.Value, usage.Cost);
    }

    private string StorageRoot()
    {
        var root = system.Api.Services.GetRequiredService<IConfiguration>()["Storage:Root"]!;
        Directory.CreateDirectory(Path.Combine(root, "inputs"));
        return root;
    }

    private async Task WaitUntilOutboxEmptyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await system.WithDbAsync(db => db.Outbox.AnyAsync(o => o.PublishedAt == null)))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Outbox did not drain");
            await Task.Delay(100);
        }
    }
}
