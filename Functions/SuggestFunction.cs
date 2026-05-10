using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Ez.Leitir.Services;
using Ez.Leitir.Shaping;

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
            // Get and trim query parameters
            var q = req.Query["q"]?.ToString().Trim();
            var scope = req.Query["scope"]?.ToString().Trim();

            // Validate required parameters
            if (string.IsNullOrEmpty(q) || string.IsNullOrEmpty(scope))
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await errorResponse.WriteAsJsonAsync(new { error = new { message = "missing q or scope" } });
                return errorResponse;
            }

            // Call the client
            var raw = await _client.SuggestAsync(q, scope);

            // Shape the response
            var shaped = LeitirShaper.ShapeSuggest(raw);

            // Return success
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(shaped);
            return response;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request exception in SuggestFunction");
            var errorResponse = req.CreateResponse(HttpStatusCode.BadGateway);
            await errorResponse.WriteAsJsonAsync(new { error = new { message = "upstream failure" } });
            return errorResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected exception in SuggestFunction");
            var errorResponse = req.CreateResponse(HttpStatusCode.BadGateway);
            await errorResponse.WriteAsJsonAsync(new { error = new { message = "upstream failure" } });
            return errorResponse;
        }
    }
}
