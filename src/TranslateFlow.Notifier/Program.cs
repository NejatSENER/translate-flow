using TranslateFlow.Notifier;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddNotifier(builder.Configuration);
await builder.Build().RunAsync();
