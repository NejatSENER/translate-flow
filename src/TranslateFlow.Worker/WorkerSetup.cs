using Microsoft.Extensions.Http.Resilience;
using TranslateFlow.Core;
using TranslateFlow.Core.Messaging;
using TranslateFlow.Processing.Extraction;
using TranslateFlow.Processing.Translation;

namespace TranslateFlow.Worker;

public static class WorkerSetup
{
    public static IServiceCollection AddTranslationWorker(this IServiceCollection services, IConfiguration config)
    {
        services.AddTranslateFlowCore(config);
        services.Configure<WorkerOptions>(config.GetSection("Worker"));
        services.AddSingleton<ITextExtractor, TextExtractor>();
        services.AddSingleton<ITokenCounter, TiktokenCounter>();
        services.AddSingleton<TextChunker>();

        if (string.Equals(config["Translation:Provider"], "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<ITranslator, OllamaTranslator>(c => c.BaseAddress = new Uri(config["Translation:OllamaUrl"] ?? "http://localhost:11434"))
                .AddStandardResilienceHandler(o =>
                {
                    // A 7B model can take a while on long chunks; the defaults (10 s per attempt) are for APIs.
                    o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
                    o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(5);
                    o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(6);
                });
        }
        else
        {
            services.AddSingleton<ITranslator, PseudoTranslator>();
        }

        services.AddConsumer<JobSubmitted, TranslationJobHandler>(Routes.JobsQueue);
        return services;
    }
}
