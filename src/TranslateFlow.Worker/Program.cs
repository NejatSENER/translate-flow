using TranslateFlow.Core;
using TranslateFlow.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddTranslationWorker(builder.Configuration);

var host = builder.Build();
await host.MigrateIfEnabledAsync();
await host.RunAsync();
