using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Ez.Leitir.Services;
using Ez.Leitir.Shaping;
using Ez.Leitir.Middleware;

namespace Ez.Leitir.Functions;

/// <summary>
/// HTTP-triggered Azure Function for searching records.
/// GET /api/search?q=query&scope=scope&offset=0
/// </summary>
public class SearchFunction
{
    private readonly LeitirClient _client;
    private readonly ILogger<SearchFunction> _logger;

    public SearchFunction(LeitirClient client, ILogger<SearchFunction> logger)
    {
        _client = client;
        _logger = logger;
    }

    [Function("search")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "search")] HttpRequestData req)
    {
        try
        {
            // Validate API key
            var validationError = await ApiKeyValidator.ValidateApiKey(req, _logger).ConfigureAwait(false);
            if (validationError != null)
            {
                return validationError;
            }

            // Get and trim query parameters
            var q = req.Query["q"]?.ToString().Trim();
            var scope = req.Query["scope"]?.ToString().Trim();

            // Get offset parameter, default to 0
            var offsetStr = req.Query["offset"]?.ToString().Trim();
            int offset = 0;
            if (!string.IsNullOrEmpty(offsetStr) && int.TryParse(offsetStr, out var parsedOffset))
            {
                offset = Math.Max(0, parsedOffset);
            }

            // Validate required parameters
            if (string.IsNullOrEmpty(q) || string.IsNullOrEmpty(scope))
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await errorResponse.WriteAsJsonAsync(new { error = new { message = "missing q or scope" } }).ConfigureAwait(false);
                return errorResponse;
            }

            // Call the client
            var pnxs = await _client.SearchAsync(q, scope, offset).ConfigureAwait(false);

            // Create empty delivery
            var emptyDelivery = JsonDocument.Parse("{\"docs\":[]}").RootElement;

            // Shape the response
            var shaped = LeitirShaper.ShapeSearch(pnxs, emptyDelivery);

            // Return success
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(shaped).ConfigureAwait(false);
            return response;
        }
        catch (HttpRequestException ex)
        {
            return await HandleErrorResponse(req, ex, "search").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await HandleErrorResponse(req, ex, "search").ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseData> HandleErrorResponse(HttpRequestData req, Exception ex, string context)
    {
        _logger.LogError(ex, "Exception in SearchFunction ({Context})", context);
        var errorResponse = req.CreateResponse(HttpStatusCode.BadGateway);
        await errorResponse.WriteAsJsonAsync(new { error = new { message = "upstream failure" } }).ConfigureAwait(false);
        return errorResponse;
    }
}
