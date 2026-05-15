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
/// HTTP-triggered Azure Function for retrieving book details with public-library
/// branch availability aggregated across all FRBR-related editions.
/// GET /api/book/{mmsId}
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
            var validationError = await ApiKeyValidator.ValidateApiKey(req, _logger).ConfigureAwait(false);
            if (validationError != null)
                return validationError;

            var trimmedMmsId = mmsId?.Trim();
            if (string.IsNullOrEmpty(trimmedMmsId))
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await errorResponse.WriteAsJsonAsync(new { error = new { message = "missing mmsId" } }).ConfigureAwait(false);
                return errorResponse;
            }

            if (trimmedMmsId.StartsWith("alma", StringComparison.OrdinalIgnoreCase))
                trimmedMmsId = trimmedMmsId[4..];

            var institutionFilter = Environment.GetEnvironmentVariable("LEITIR_INSTITUTION_FILTER") ?? "354ILC_ALM";

            // Step 1: consortium record — metadata + FRBR group id.
            // The leitir consortium endpoint expects the id with the "alma" prefix.
            var consortium = await _client.GetConsortiumRecordAsync($"alma{trimmedMmsId}").ConfigureAwait(false);

            // Step 2: discover FRBR sibling editions (if any).
            var siblings = await ResolveSiblingsAsync(consortium, trimmedMmsId).ConfigureAwait(false);

            // Step 3: fetch each edition's institution-scoped record in parallel.
            // The /priv/nz/pnx/P/ endpoint expects the bare mmsId (no "alma" prefix).
            var institutionTasks = siblings.Select(id => _client.GetInstitutionRecordAsync(id, institutionFilter));
            var institutionRecords = await Task.WhenAll(institutionTasks).ConfigureAwait(false);

            // Step 4: aggregate.
            var shaped = LeitirShaper.ShapeBook(consortium, institutionRecords);

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

    private async Task<string[]> ResolveSiblingsAsync(JsonElement consortium, string fallbackMmsId)
    {
        var frbrGroupId = LeitirShaper.ExtractFrbrGroupId(consortium);
        if (string.IsNullOrEmpty(frbrGroupId))
            return new[] { fallbackMmsId };

        var editions = await _client.GetFrbrEditionsAsync(frbrGroupId).ConfigureAwait(false);
        var siblings = LeitirShaper.ExtractMmsIds(editions);

        if (siblings.Length == 0)
            return new[] { fallbackMmsId };

        // Always include the original mmsId — it may not appear in the FRBR facet result
        // if it doesn't match the q=any,contains,a fallback term.
        return siblings.Contains(fallbackMmsId) ? siblings : siblings.Append(fallbackMmsId).ToArray();
    }

    private async Task<HttpResponseData> HandleErrorResponse(HttpRequestData req, Exception ex, string context)
    {
        _logger.LogError(ex, "Exception in BookFunction ({Context})", context);
        var errorResponse = req.CreateResponse(HttpStatusCode.BadGateway);
        await errorResponse.WriteAsJsonAsync(new { error = new { message = "upstream failure" } }).ConfigureAwait(false);
        return errorResponse;
    }
}
