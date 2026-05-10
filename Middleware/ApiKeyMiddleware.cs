using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ez.Leitir.Middleware;

public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var apiKey = Environment.GetEnvironmentVariable("LEITIR_API_KEY");

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("LEITIR_API_KEY not configured");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = new { message = "Server misconfigured" } });
            return;
        }

        // Skip validation for health endpoints
        if (context.Request.Path == "/" || context.Request.Path == "/health")
        {
            await _next(context);
            return;
        }

        // Check X-Api-Key header
        var providedKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();

        if (string.IsNullOrEmpty(providedKey) || providedKey != apiKey)
        {
            _logger.LogWarning("Invalid API key provided");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = new { message = "Unauthorized" } });
            return;
        }

        await _next(context);
    }
}
