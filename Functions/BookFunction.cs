using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Ez.Leitir.Services;
using Ez.Leitir.Shaping;

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
            // Get and trim mmsId from route param
            var trimmedMmsId = mmsId?.Trim();

            // Validate required parameter
            if (string.IsNullOrEmpty(trimmedMmsId))
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await errorResponse.WriteAsJsonAsync(new { error = new { message = "missing mmsId" } });
                return errorResponse;
            }

            // Remove "alma" prefix if present
            if (trimmedMmsId.StartsWith("alma", StringComparison.OrdinalIgnoreCase))
            {
                trimmedMmsId = trimmedMmsId[4..];
            }

            // Parallel calls to both endpoints
            var pnxTask = _client.GetFullRecordAsync(trimmedMmsId);
            var physicalTask = _client.GetPhysicalServiceAsync(trimmedMmsId);

            await Task.WhenAll(pnxTask, physicalTask);

            var pnxDoc = pnxTask.Result;
            var physical = physicalTask.Result;

            // Shape the response
            var shaped = LeitirShaper.ShapeBook(pnxDoc, physical);

            // Return success
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(shaped);
            return response;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request exception in BookFunction");
            var errorResponse = req.CreateResponse(HttpStatusCode.BadGateway);
            await errorResponse.WriteAsJsonAsync(new { error = new { message = "upstream failure" } });
            return errorResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected exception in BookFunction");
            var errorResponse = req.CreateResponse(HttpStatusCode.BadGateway);
            await errorResponse.WriteAsJsonAsync(new { error = new { message = "upstream failure" } });
            return errorResponse;
        }
    }
}
