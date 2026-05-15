using System.Net;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Ez.Leitir.Middleware;

public static class ApiKeyValidator
{
    public static async Task<HttpResponseData?> ValidateApiKey(HttpRequestData req, ILogger logger)
    {
        var apiKey = Environment.GetEnvironmentVariable("LEITIR_API_KEY");

        if (string.IsNullOrEmpty(apiKey))
        {
            logger.LogError("LEITIR_API_KEY not configured");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = new { message = "Server misconfigured" } }).ConfigureAwait(false);
            return errorResponse;
        }

        // Check X-Api-Key header
        var providedKey = req.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.FirstOrDefault() : null;

        if (string.IsNullOrEmpty(providedKey) || providedKey != apiKey)
        {
            logger.LogWarning("Invalid API key provided");
            var errorResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
            await errorResponse.WriteAsJsonAsync(new { error = new { message = "Unauthorized" } }).ConfigureAwait(false);
            return errorResponse;
        }

        return null;
    }
}
