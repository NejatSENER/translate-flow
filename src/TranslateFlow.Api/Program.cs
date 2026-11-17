using Microsoft.AspNetCore.Http.Features;
using TranslateFlow.Api;
using TranslateFlow.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTranslateFlowCore(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = JobEndpoints.MaxUploadBytes + 64 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = JobEndpoints.MaxUploadBytes + 64 * 1024);

var app = builder.Build();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapJobEndpoints();

await app.MigrateIfEnabledAsync();
app.Run();

public partial class Program;
