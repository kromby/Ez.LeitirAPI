using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Ez.Leitir.Services;
using Ez.Leitir.Shaping;
using Ez.Leitir.Middleware;

namespace Ez.Leitir.Functions;

/// <summary>
/// HTTP-triggered Azure Function for suggesting search terms.
/// GET /api/suggest?q=query&scope=scope
/// </summary>
public class SuggestFunction
{
    private readonly LeitirClient _client;
    private readonly ILogger<SuggestFunction> _logger;

    public SuggestFunction(LeitirClient client, ILogger<SuggestFunction> logger)
    {
        _client = client;
        _logger = logger;
    }

    [Function("suggest")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "suggest")] HttpRequestData req)
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

            // Validate required parameters
            if (string.IsNullOrEmpty(q) || string.IsNullOrEmpty(scope))
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await errorResponse.WriteAsJsonAsync(new { error = new { message = "missing q or scope" } }).ConfigureAwait(false);
                return errorResponse;
            }

            // Call the client
            var raw = await _client.SuggestAsync(q, scope).ConfigureAwait(false);

            // Shape the response
            var shaped = LeitirShaper.ShapeSuggest(raw);

            // Return success
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(shaped).ConfigureAwait(false);
            return response;
        }
        catch (HttpRequestException ex)
        {
            return await HandleErrorResponse(req, ex, "suggest").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await HandleErrorResponse(req, ex, "suggest").ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseData> HandleErrorResponse(HttpRequestData req, Exception ex, string context)
    {
        _logger.LogError(ex, "Exception in SuggestFunction ({Context})", context);
        var errorResponse = req.CreateResponse(HttpStatusCode.BadGateway);
        await errorResponse.WriteAsJsonAsync(new { error = new { message = "upstream failure" } }).ConfigureAwait(false);
        return errorResponse;
    }
}
