using System.Text.Json;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ez.Leitir.Services;
using Ez.Leitir.Middleware;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights()
    .AddLogging(loggingBuilder =>
    {
        loggingBuilder.AddConsole();
    })
    .AddHttpClient()
    .AddSingleton<LeitirJwtCache>()
    .AddScoped<LeitirClient>();

builder.Services.Configure<WorkerOptions>(options =>
{
    options.Serializer = new JsonObjectSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web));
});

// CORS configuration
var allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS") ?? "";
if (!string.IsNullOrEmpty(allowedOrigins))
{
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowSpecificOrigins", policy =>
        {
            var origins = allowedOrigins.Split(',').Select(o => o.Trim()).ToArray();
            policy.WithOrigins(origins)
                .AllowAnyMethod()
                .AllowAnyHeader();
        });
    });
}

var app = builder.Build();

app.Run();
