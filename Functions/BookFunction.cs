using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Ez.Leitir.Services;
using Ez.Leitir.Shaping;
using Ez.Leitir.Middleware;

namespace Ez.Leitir.Functions;

/// <summary>
/// HTTP-triggered Azure Function for retrieving book details.
/// GET /api/book/{mmsId}?lib=optional
/// </summary>
public class BookFunction
{
    private readonly LeitirClient _client;
    private readonly ILogger<BookFunction> _logger;

    public BookFunction(LeitirClient client, ILogger<BookFunction> logger)
    {
        _client = client;
        _logger = logger;
    }

    [Function("book")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "book/{mmsId}")] HttpRequestData req,
        string mmsId)
    {
        try
        {
            // Validate API key
            var validationError = await ApiKeyValidator.ValidateApiKey(req, _logger).ConfigureAwait(false);
            if (validationError != null)
            {
                return validationError;
            }

            // Get and trim mmsId from route param
            var trimmedMmsId = mmsId?.Trim();

            // Validate required parameter
            if (string.IsNullOrEmpty(trimmedMmsId))
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await errorResponse.WriteAsJsonAsync(new { error = new { message = "missing mmsId" } }).ConfigureAwait(false);
                return errorResponse;
            }

            // Remove "alma" prefix if present
            if (trimmedMmsId.StartsWith("alma", StringComparison.OrdinalIgnoreCase))
            {
                trimmedMmsId = trimmedMmsId[4..];
            }

            // Parallel calls to both endpoints
            var results = await Task.WhenAll(
                _client.GetFullRecordAsync(trimmedMmsId),
                _client.GetPhysicalServiceAsync(trimmedMmsId)
            ).ConfigureAwait(false);

            var pnxDoc = results[0];
            var physical = results[1];

            // Shape the response
            var shaped = LeitirShaper.ShapeBook(pnxDoc, physical);

            // Return success
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(shaped).ConfigureAwait(false);
            return response;
        }
        catch (HttpRequestException ex)
        {
            return await HandleErrorResponse(req, ex, "book").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await HandleErrorResponse(req, ex, "book").ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseData> HandleErrorResponse(HttpRequestData req, Exception ex, string context)
    {
        _logger.LogError(ex, "Exception in BookFunction ({Context})", context);
        var errorResponse = req.CreateResponse(HttpStatusCode.BadGateway);
        await errorResponse.WriteAsJsonAsync(new { error = new { message = "upstream failure" } }).ConfigureAwait(false);
        return errorResponse;
    }
}
