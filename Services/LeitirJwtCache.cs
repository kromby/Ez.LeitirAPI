using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ez.Leitir.Services;

/// <summary>
/// Manages cached JWT tokens from leitir.is with proactive refresh.
/// Maintains a cached token, tracks expiry, and refreshes 30 minutes before expiry.
/// Handles in-flight deduplication to avoid multiple simultaneous fetch requests.
/// </summary>
public class LeitirJwtCache
{
    private const long RefreshThresholdMs = 30 * 60 * 1000; // 30 minutes in milliseconds
    private const int FetchTimeoutSeconds = 10;

    private string? _cachedToken;
    private long _cachedExpMs;
    private Task<string>? _inFlight;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly ILogger<LeitirJwtCache> _logger;

    public LeitirJwtCache(HttpClient httpClient, ILogger<LeitirJwtCache> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _cachedExpMs = 0;
    }

    /// <summary>
    /// Gets a valid JWT token, refreshing if necessary.
    /// If multiple callers request simultaneously, they share the same in-flight request.
    /// </summary>
    public async Task<string> GetJwtAsync()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Check if token is still valid (not stale)
        if (_cachedToken != null && nowMs + RefreshThresholdMs <= _cachedExpMs)
        {
            return _cachedToken;
        }

        // If a fetch is already in flight, return the same promise
        if (_inFlight != null)
        {
            return await _inFlight;
        }

        // Acquire the semaphore to prevent multiple simultaneous fetches
        await _semaphore.WaitAsync();
        try
        {
            // Double-check pattern: verify token is still stale after acquiring lock
            nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_cachedToken != null && nowMs + RefreshThresholdMs <= _cachedExpMs)
            {
                return _cachedToken;
            }

            // Set the in-flight task and return it
            _inFlight = FetchFreshAsync("expiry");
            var token = await _inFlight;
            _inFlight = null;
            return token;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Fetches a fresh JWT from leitir.is.
    /// </summary>
    private async Task<string> FetchFreshAsync(string reason = "expiry")
    {
        var baseUrl = Environment.GetEnvironmentVariable("LEITIR_BASE_URL")
                      ?? throw new InvalidOperationException("LEITIR_BASE_URL environment variable is not set");
        var inst = Environment.GetEnvironmentVariable("LEITIR_INST")
                  ?? throw new InvalidOperationException("LEITIR_INST environment variable is not set");
        var vid = Environment.GetEnvironmentVariable("LEITIR_VID")
                 ?? throw new InvalidOperationException("LEITIR_VID environment variable is not set");

        var url = $"{baseUrl}/primaws/rest/pub/institution/{inst}/guestJwt?isGuest=true&lang=is&viewId={vid}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(FetchTimeoutSeconds));
        var response = await _httpClient.GetAsync(url, cts.Token);
        response.EnsureSuccessStatusCode();

        // Read response as text and clean it
        var responseText = await response.Content.ReadAsStringAsync(cts.Token);
        responseText = responseText.Trim();

        // Remove quotes if present
        if (responseText.StartsWith('"') && responseText.EndsWith('"'))
        {
            responseText = responseText[1..^1];
        }

        // Decode the JWT to get expiry
        var expMs = DecodeExp(responseText);

        // Cache the token and expiry
        _cachedToken = responseText;
        _cachedExpMs = expMs;

        _logger.LogInformation("JWT token refreshed (reason: {Reason}). Expires at {ExpiryMs}", reason, _cachedExpMs);

        return responseText;
    }

    /// <summary>
    /// Decodes the JWT payload and extracts the expiry time.
    /// </summary>
    /// <param name="jwt">The JWT token string</param>
    /// <returns>Expiry time in milliseconds since epoch</returns>
    private static long DecodeExp(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("Invalid JWT format: expected 3 parts");
        }

        var payload = parts[1];
        var decoded = Base64UrlDecode(payload);
        var json = Encoding.UTF8.GetString(decoded);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("exp", out var expElement))
        {
            throw new InvalidOperationException("JWT payload missing 'exp' claim");
        }

        var expSeconds = expElement.GetInt64();
        return expSeconds * 1000; // Convert to milliseconds
    }

    /// <summary>
    /// Decodes a base64url-encoded string.
    /// </summary>
    private static byte[] Base64UrlDecode(string base64Url)
    {
        // Convert base64url to base64
        var base64 = base64Url
            .Replace('-', '+')
            .Replace('_', '/');

        // Add padding if necessary
        var paddingNeeded = (4 - (base64.Length % 4)) % 4;
        base64 += new string('=', paddingNeeded);

        return Convert.FromBase64String(base64);
    }
}
