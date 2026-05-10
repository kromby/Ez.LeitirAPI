using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ez.Leitir.Services;
using Ez.Leitir.Middleware;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights()
    .AddHttpClient()
    .AddSingleton<LeitirJwtCache>()
    .AddScoped<LeitirClient>();

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();

app.Run();
