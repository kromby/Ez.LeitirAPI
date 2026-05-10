using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ez.Leitir.Services;

/// <summary>
/// HTTP client for making authenticated requests to the leitir.is API.
/// Handles JWT authentication, request formatting, and error logging.
/// </summary>
public class LeitirClient
{
    private readonly HttpClient _httpClient;
    private readonly LeitirJwtCache _jwtCache;
    private readonly ILogger<LeitirClient> _logger;
    private readonly string _baseUrl;
    private readonly string _vid;
    private readonly string _inst;

    public LeitirClient(HttpClient httpClient, LeitirJwtCache jwtCache, ILogger<LeitirClient> logger)
    {
        _httpClient = httpClient;
        _jwtCache = jwtCache;
        _logger = logger;
        _baseUrl = Environment.GetEnvironmentVariable("LEITIR_BASE_URL") ?? "https://www.leitir.is";
        _vid = Environment.GetEnvironmentVariable("LEITIR_VID") ?? "354ILC_NETWORK:10000_UNION";
        _inst = Environment.GetEnvironmentVariable("LEITIR_INST") ?? "354ILC_NETWORK";
    }

    /// <summary>
    /// Suggests search terms based on a query.
    /// GET /primaws/rest/pub/suggest?q=&scope=&vid=&lang=is
    /// </summary>
    public async Task<JsonElement> SuggestAsync(string q, string scope, CancellationToken cancellationToken = default)
    {
        var encodedQ = Uri.EscapeDataString(q);
        var encodedScope = Uri.EscapeDataString(scope);
        var url = $"{_baseUrl}/primaws/rest/pub/suggest?q={encodedQ}&scope={encodedScope}&vid={Uri.EscapeDataString(_vid)}&lang=is";
        
        return await AuthedJsonAsync(url, "GET", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches for records in the library.
    /// GET /primaws/rest/pub/pnxs?q=any,contains,&inst=&scope=&vid=&tab=MyLibrary&limit=20&offset=&sort=rank&lang=is
    /// </summary>
    public async Task<JsonElement> SearchAsync(string q, string scope, int offset, CancellationToken cancellationToken = default)
    {
        var encodedQ = Uri.EscapeDataString($"any,contains,{q}");
        var encodedScope = Uri.EscapeDataString(scope);
        var url = $"{_baseUrl}/primaws/rest/pub/pnxs?q={encodedQ}&inst={Uri.EscapeDataString(_inst)}&scope={encodedScope}&vid={Uri.EscapeDataString(_vid)}&tab=MyLibrary&limit=20&offset={offset}&sort=rank&lang=is";
        
        return await AuthedJsonAsync(url, "GET", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets delivery information for search results.
    /// POST /primaws/rest/pub/delivery?q=any,contains,&inst=&scope=&vid=&lang=is&limit=20&offset=
    /// </summary>
    public async Task<JsonElement> DeliveryAsync(string q, string scope, int offset, CancellationToken cancellationToken = default)
    {
        var encodedQ = Uri.EscapeDataString($"any,contains,{q}");
        var encodedScope = Uri.EscapeDataString(scope);
        var url = $"{_baseUrl}/primaws/rest/pub/delivery?q={encodedQ}&inst={Uri.EscapeDataString(_inst)}&scope={encodedScope}&vid={Uri.EscapeDataString(_vid)}&lang=is&limit=20&offset={offset}";
        
        return await AuthedJsonAsync(url, "POST", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets delivery information for specific MMS IDs.
    /// POST /primaws/rest/pub/delivery with { mmsIds } body
    /// </summary>
    public async Task<JsonElement> DeliveryByMmsIdsAsync(string[] mmsIds, string scope, CancellationToken cancellationToken = default)
    {
        var encodedScope = Uri.EscapeDataString(scope);
        var url = $"{_baseUrl}/primaws/rest/pub/delivery?inst={Uri.EscapeDataString(_inst)}&scope={encodedScope}&vid={Uri.EscapeDataString(_vid)}&lang=is&limit=20&offset=0";
        
        var body = JsonSerializer.Serialize(new { mmsIds });
        
        return await AuthedJsonAsync(url, "POST", body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets physical service information for a record.
    /// GET /primaws/rest/pub/getPhysicalService/{mmsId}?vid=&recordOwner=&sourceRecordId=&resource_type=book
    /// </summary>
    public async Task<JsonElement> GetPhysicalServiceAsync(string mmsId, CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/primaws/rest/pub/getPhysicalService/{Uri.EscapeDataString(mmsId)}?vid={Uri.EscapeDataString(_vid)}&recordOwner=&sourceRecordId=&resource_type=book";
        
        return await AuthedJsonAsync(url, "GET", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the full record for a specific MMS ID.
    /// GET /primaws/rest/priv/nz/pnx/P/{mmsId}?record-institution=&lang=is
    /// </summary>
    public async Task<JsonElement> GetFullRecordAsync(string mmsId, CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/primaws/rest/priv/nz/pnx/P/{Uri.EscapeDataString(mmsId)}?record-institution=&lang=is";
        
        return await AuthedJsonAsync(url, "GET", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Authenticates and makes an HTTP request, returning the response as a JsonElement.
    /// </summary>
    private async Task<JsonElement> AuthedJsonAsync(string url, string method = "GET", string? body = null, CancellationToken cancellationToken = default)
    {
        var jwt = await _jwtCache.GetJwtAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.Add("Authorization", $"Bearer {jwt}");
        request.Headers.Add("Accept", "application/json");

        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var snippet = responseContent.Length > 200 ? responseContent[..200] : responseContent;
            _logger.LogError(
                "HTTP request failed: {Method} {Url} returned {StatusCode}. Response: {ResponseSnippet}",
                method,
                url,
                (int)response.StatusCode,
                snippet
            );
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
        }

        var jsonElement = JsonDocument.Parse(responseContent).RootElement;
        var dataSnippet = responseContent.Length > 300 ? responseContent[..300] : responseContent;
        
        _logger.LogInformation(
            "HTTP request succeeded: {Method} {Url} returned {StatusCode}. Data: {DataSnippet}",
            method,
            url,
            (int)response.StatusCode,
            dataSnippet
        );

        return jsonElement;
    }
}
