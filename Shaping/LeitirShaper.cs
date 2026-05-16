using System.Text.RegularExpressions;
using System.Text.Json;
using Ez.Leitir.Models;

namespace Ez.Leitir.Shaping;

/// <summary>
/// Transforms PNX JSON responses from the Leitir API into clean C# models.
/// Provides methods for shaping suggest, search, and book detail responses.
/// </summary>
public static class LeitirShaper
{
    // ─── Public Methods ──────────────────────────────────────────────────────

    /// <summary>
    /// Transforms a raw suggest response into a deduplicated list of suggestions.
    /// </summary>
    /// <param name="raw">JsonElement with response.docs[].text fields</param>
    /// <returns>SuggestResponse with deduplicated suggestions array</returns>
    public static SuggestResponse ShapeSuggest(JsonElement raw)
    {
        var seen = new HashSet<string>();
        var suggestions = new List<string>();

        if (raw.TryGetProperty("docs", out var docsElement) && docsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in docsElement.EnumerateArray())
            {
                var text = FirstString(doc, "text");
                if (text != null && !seen.Contains(text))
                {
                    seen.Add(text);
                    suggestions.Add(text);
                }
            }
        }

        return new SuggestResponse(suggestions.ToArray());
    }

    /// <summary>
    /// Transforms PNX search results into available and on-loan books. A book is considered
    /// available when the target institution (e.g., 354ILC_ALM) appears in its
    /// delivery.almaInstitutionsList with status "available_in_institution". This is the
    /// institution-level summary leitir.is itself surfaces on search results — branch-level
    /// detail is only available on the book detail endpoint.
    /// </summary>
    /// <param name="pnxs">JsonElement with docs[] from /pub/pnxs</param>
    /// <param name="delivery">Top-level array from /pub/delivery POST</param>
    /// <param name="institutionFilter">Institution code (e.g. "354ILC_ALM") to surface in BranchesOnShelf</param>
    public static SearchResponse ShapeSearch(JsonElement pnxs, JsonElement delivery, string institutionFilter)
    {
        // Index delivery records by mmsId. The response is a top-level array of records
        // shaped like a doc (with pnx + delivery), not a {docs:[]} envelope.
        var deliveryByMms = new Dictionary<string, JsonElement>();
        if (delivery.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in delivery.EnumerateArray())
            {
                var id = FirstString(doc, "pnx/control/recordid");
                if (id != null)
                    deliveryByMms[id] = doc;
            }
        }

        var available = new List<Book>();
        var onLoan = new List<Book>();

        if (pnxs.TryGetProperty("docs", out var pnxDocs) && pnxDocs.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in pnxDocs.EnumerateArray())
            {
                var book = PnxToBook(doc, deliveryByMms, institutionFilter);
                if (book == null)
                    continue;

                if (book.BranchesOnShelf.Length > 0)
                    available.Add(book);
                else
                    onLoan.Add(book);
            }
        }

        int total = available.Count + onLoan.Count;
        if (pnxs.TryGetProperty("info", out var info) && info.TryGetProperty("total", out var totalElement))
        {
            if (totalElement.TryGetInt32(out var totalValue))
                total = totalValue;
        }

        string? didYouMean = null;
        if (pnxs.TryGetProperty("did_u_mean", out var didYouMeanElement) && didYouMeanElement.ValueKind == JsonValueKind.String)
        {
            didYouMean = didYouMeanElement.GetString();
        }

        return new SearchResponse(available.ToArray(), onLoan.ToArray(), total, didYouMean);
    }

    /// <summary>
    /// Builds a BookResponse from a consortium record (for metadata + FRBR id) and one or
    /// more institution-scoped records (for branch holdings). Holdings are aggregated
    /// across all editions and deduplicated by branch name, keeping the best status.
    /// </summary>
    /// <param name="consortiumRecord">Response from /pub/pnxs/L/{mmsId} (array or object with pnx)</param>
    /// <param name="institutionRecords">Per-edition responses from /priv/nz/pnx/P/{mmsId}?record-institution=...</param>
    public static BookResponse ShapeBook(JsonElement consortiumRecord, JsonElement[] institutionRecords)
    {
        var consortiumDoc = ExtractDoc(consortiumRecord);
        var pnx = ExtractPnx(consortiumRecord);

        var mmsId = FirstString(pnx, "control/recordid") ?? "";
        if (mmsId.StartsWith("alma", StringComparison.OrdinalIgnoreCase))
            mmsId = mmsId[4..];
        var title = FirstString(pnx, "display/title") ?? "(óþekktur titill)";
        var author = FirstString(pnx, "addata/au")
            ?? FirstString(pnx, "display/creator")
            ?? FirstString(pnx, "display/contributor");
        var year = YearOf(FirstString(pnx, "display/creationdate"));
        var isbn = FirstString(pnx, "search/isbn");
        var summary = FirstString(pnx, "display/description");
        var genres = GetGenres(pnx);
        var pageCount = GetPageCount(FirstString(pnx, "display/format"));
        var coverSources = ToCoverSources(consortiumDoc, mmsId, isbn);

        // Aggregate holdings across all institution records, keyed by branch name.
        // If the same branch appears in multiple editions, keep the entry with the best status.
        var byBranch = new Dictionary<string, BranchAvailability>();

        foreach (var record in institutionRecords)
        {
            foreach (var holding in EnumerateHoldings(record))
            {
                var branch = GetHoldingBranch(holding);
                if (string.IsNullOrEmpty(branch))
                    continue;

                var callNumber = GetHoldingCallNumber(holding);
                var status = MapHoldingStatus(GetHoldingAvailability(holding));
                var entry = new BranchAvailability(branch, status, callNumber);

                if (!byBranch.TryGetValue(branch, out var existing) || StatusPriority(status) > StatusPriority(existing.Status))
                    byBranch[branch] = entry;
            }
        }

        var branches = byBranch.Values
            .OrderBy(b => b.Branch, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var onShelfNames = branches.Where(b => b.Status == "on-shelf").Select(b => b.Branch).ToArray();

        var book = new BookDetail(
            mmsId,
            title,
            coverSources,
            onShelfNames,
            genres,
            author,
            year,
            isbn,
            null,
            summary,
            pageCount
        );

        return new BookResponse(book, branches);
    }

    /// <summary>
    /// Extracts the FRBR group id from a consortium record so the caller can fetch
    /// sibling editions. Returns null if absent.
    /// </summary>
    public static string? ExtractFrbrGroupId(JsonElement consortiumRecord)
    {
        var pnx = ExtractPnx(consortiumRecord);
        return FirstString(pnx, "facets/frbrgroupid");
    }

    /// <summary>
    /// Extracts Alma mmsIds from a pnxs search response. Non-Alma records (e.g., Primo
    /// Central articles with ids like "cdi_crossref_...") are excluded — the delivery
    /// endpoint 500s when sent non-Alma ids, and downstream endpoints don't handle them.
    /// By default strips the "alma" prefix (the form expected by the institution-scoped
    /// pnx/P/ endpoint). Pass withAlmaPrefix=true when calling /delivery, which keys
    /// records by the full "alma..." id.
    /// </summary>
    public static string[] ExtractMmsIds(JsonElement pnxsResponse, bool withAlmaPrefix = false)
    {
        var ids = new List<string>();
        if (pnxsResponse.TryGetProperty("docs", out var docs) && docs.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in docs.EnumerateArray())
            {
                var id = FirstString(doc, "pnx/control/recordid");
                if (id == null) continue;
                if (!id.StartsWith("alma", StringComparison.OrdinalIgnoreCase)) continue;
                ids.Add(withAlmaPrefix ? id : id[4..]);
            }
        }
        return ids.ToArray();
    }

    private static JsonElement? ExtractPnx(JsonElement record)
    {
        if (record.ValueKind == JsonValueKind.Array && record.GetArrayLength() > 0)
        {
            var first = record[0];
            if (first.TryGetProperty("pnx", out var pnxElement))
                return pnxElement;
        }
        else if (record.ValueKind == JsonValueKind.Object && record.TryGetProperty("pnx", out var pnxElement))
        {
            return pnxElement;
        }
        return null;
    }

    private static JsonElement? ExtractDoc(JsonElement record)
    {
        if (record.ValueKind == JsonValueKind.Array && record.GetArrayLength() > 0)
            return record[0];
        if (record.ValueKind == JsonValueKind.Object)
            return record;
        return null;
    }

    private static IEnumerable<JsonElement> EnumerateHoldings(JsonElement institutionRecord)
    {
        JsonElement? delivery = null;
        if (institutionRecord.ValueKind == JsonValueKind.Array && institutionRecord.GetArrayLength() > 0)
        {
            if (institutionRecord[0].TryGetProperty("delivery", out var d))
                delivery = d;
        }
        else if (institutionRecord.ValueKind == JsonValueKind.Object && institutionRecord.TryGetProperty("delivery", out var d))
        {
            delivery = d;
        }

        if (delivery == null || !delivery.Value.TryGetProperty("holding", out var holding) || holding.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var entry in holding.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object)
                yield return entry;
        }
    }

    private static string GetHoldingBranch(JsonElement holding)
    {
        if (holding.TryGetProperty("mainLocation", out var ml) && ml.ValueKind == JsonValueKind.String)
            return ml.GetString() ?? "";
        if (holding.TryGetProperty("subLocation", out var sl) && sl.ValueKind == JsonValueKind.String)
            return sl.GetString() ?? "";
        return "";
    }

    private static string? GetHoldingCallNumber(JsonElement holding)
    {
        if (holding.TryGetProperty("callNumber", out var cn) && cn.ValueKind == JsonValueKind.String)
            return cn.GetString();
        return null;
    }

    private static string? GetHoldingAvailability(JsonElement holding)
    {
        if (holding.TryGetProperty("availabilityStatus", out var s) && s.ValueKind == JsonValueKind.String)
            return s.GetString();
        return null;
    }

    private static string MapHoldingStatus(string? primoStatus) => primoStatus switch
    {
        "available" => "on-shelf",
        "unavailable" => "on-loan",
        "check_holdings" => "on-loan",
        _ => "unavailable",
    };

    private static int StatusPriority(string status) => status switch
    {
        "on-shelf" => 2,
        "on-loan" => 1,
        _ => 0,
    };

    // ─── Private Helper Methods ──────────────────────────────────────────────

    /// <summary>
    /// Navigates a JSON path using '/' separator and returns the first string value.
    /// Handles both direct strings and arrays of strings.
    /// </summary>
    /// <param name="element">JsonElement to navigate</param>
    /// <param name="path">Path with '/' separator (e.g., "pnx/display/title")</param>
    /// <returns>First string value or null if not found</returns>
    private static string? FirstString(JsonElement? element, string path)
    {
        if (element == null)
            return null;

        var current = element.Value;
        var parts = path.Split('/');

        foreach (var part in parts)
        {
            if (current.TryGetProperty(part, out var next))
            {
                current = next;
            }
            else
            {
                return null;
            }
        }

        // Handle array or direct string
        if (current.ValueKind == JsonValueKind.Array && current.GetArrayLength() > 0)
        {
            var first = current[0];
            return first.ValueKind == JsonValueKind.String ? first.GetString() : null;
        }

        if (current.ValueKind == JsonValueKind.String)
            return current.GetString();

        return null;
    }

    /// <summary>
    /// Extracts a 4-digit year from a string using regex.
    /// </summary>
    /// <param name="s">String to search</param>
    /// <returns>Year as integer or null if not found</returns>
    private static int? YearOf(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return null;

        var match = Regex.Match(s, @"\b(\d{4})\b");
        return match.Success && int.TryParse(match.Groups[1].Value, out var year) ? year : null;
    }

    /// <summary>
    /// Builds an array of cover source URLs for a book. URLs Primo advertises in
    /// <c>doc.delivery.link[]</c> (where <c>linkType == "thumbnail"</c>) come first — these
    /// are the URLs leitir.is itself renders, e.g. <c>https://thumbs.leitir.is/{isbn}.jpg</c>.
    /// JSONP endpoints (Google Books bibkeys callback) are skipped because they don't load
    /// as &lt;img&gt; sources. baekur.is + Syndetics are appended as fallbacks for older
    /// records where Primo doesn't advertise a working thumbnail.
    /// </summary>
    /// <param name="doc">The top-level doc JsonElement (contains delivery + pnx siblings), or null</param>
    /// <param name="mmsId">MMS identifier for the book (alma-prefix stripped)</param>
    /// <param name="isbn">Optional ISBN for the Syndetics fallback</param>
    private static string[] ToCoverSources(JsonElement? doc, string mmsId, string? isbn)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<string>();

        foreach (var url in ExtractThumbnailUrlsFromDoc(doc))
        {
            if (seen.Add(url))
                sources.Add(url);
        }

        var baekurUrl = $"https://baekur.is/cover/tbn/{mmsId}";
        if (seen.Add(baekurUrl))
            sources.Add(baekurUrl);

        if (!string.IsNullOrEmpty(isbn))
        {
            var encodedIsbn = Uri.EscapeDataString(isbn);
            var syndeticsUrl =
                $"https://proxy-euf.hosted.exlibrisgroup.com/exl_rewrite/syndetics.com/index.php?client=primo&isbn={encodedIsbn}/sc.jpg";
            if (seen.Add(syndeticsUrl))
                sources.Add(syndeticsUrl);
        }

        return sources.ToArray();
    }

    /// <summary>
    /// Extracts thumbnail URLs from a Primo doc's <c>delivery.link[]</c> array, in the order
    /// Primo returns them. JSONP endpoints (contain <c>callback=</c> or <c>jscmd=</c>) are
    /// skipped — they don't load as &lt;img&gt; sources and would just cause failed image
    /// requests in the consuming UI.
    /// </summary>
    private static IEnumerable<string> ExtractThumbnailUrlsFromDoc(JsonElement? doc)
    {
        if (doc is null || doc.Value.ValueKind != JsonValueKind.Object)
            yield break;
        if (!doc.Value.TryGetProperty("delivery", out var delivery) || delivery.ValueKind != JsonValueKind.Object)
            yield break;
        if (!delivery.TryGetProperty("link", out var links) || links.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var link in links.EnumerateArray())
        {
            if (link.ValueKind != JsonValueKind.Object)
                continue;

            var linkType = link.TryGetProperty("linkType", out var lt) && lt.ValueKind == JsonValueKind.String
                ? lt.GetString()
                : null;
            if (!string.Equals(linkType, "thumbnail", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = link.TryGetProperty("linkURL", out var lu) && lu.ValueKind == JsonValueKind.String
                ? lu.GetString()
                : null;
            if (string.IsNullOrEmpty(url))
                continue;

            // Skip JSONP endpoints — Google Books bibkeys callback returns JS, not an image.
            if (url.Contains("callback=", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("jscmd=", StringComparison.OrdinalIgnoreCase))
                continue;

            yield return url;
        }
    }

    /// <summary>
    /// Determines if a document represents a physical book.
    /// </summary>
    /// <param name="doc">JsonElement to check</param>
    /// <returns>True if document is a book, journal, newspaper, or manuscript</returns>
    private static bool IsPhysicalBook(JsonElement? doc)
    {
        var type = FirstString(doc, "pnx/display/type")?.ToLowerInvariant() ?? "";
        return new[] { "book", "journal", "newspaper", "manuscript" }.Contains(type);
    }

    /// <summary>
    /// Extracts genres from pnx.display.subject array.
    /// </summary>
    private static string[] GetGenres(JsonElement? pnx)
    {
        if (pnx == null)
            return [];

        if (pnx.Value.TryGetProperty("display", out var display) &&
            display.TryGetProperty("subject", out var subject) &&
            subject.ValueKind == JsonValueKind.Array)
        {
            var genres = new List<string>();
            foreach (var item in subject.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    genres.Add(item.GetString() ?? "");
                }
            }
            return genres.ToArray();
        }

        return [];
    }

    /// <summary>
    /// Extracts page count from format string.
    /// </summary>
    private static int? GetPageCount(string? formatStr)
    {
        if (string.IsNullOrEmpty(formatStr))
            return null;

        var match = Regex.Match(formatStr, @"\d+");
        return match.Success && int.TryParse(match.Value, out var pages) ? pages : null;
    }

    /// <summary>
    /// Converts a PNX document to a Book record.
    /// </summary>
    private static Book? PnxToBook(JsonElement doc, Dictionary<string, JsonElement> deliveryByMms, string institutionFilter)
    {
        var mmsId = FirstString(doc, "pnx/control/recordid");
        if (mmsId == null)
            return null;

        if (!IsPhysicalBook(doc))
            return null;

        var title = FirstString(doc, "pnx/display/title") ?? "(óþekktur titill)";
        var author = FirstString(doc, "pnx/addata/au")
            ?? FirstString(doc, "pnx/display/creator")
            ?? FirstString(doc, "pnx/display/contributor");
        var year = YearOf(FirstString(doc, "pnx/display/creationdate"));
        var isbn = FirstString(doc, "pnx/search/isbn");

        var almaStrippedId = mmsId.StartsWith("alma", StringComparison.OrdinalIgnoreCase) ? mmsId[4..] : mmsId;
        var coverSources = ToCoverSources(doc, almaStrippedId, isbn);

        var onShelf = deliveryByMms.TryGetValue(mmsId, out var delivery)
            ? InstitutionLabelIfAvailable(delivery, institutionFilter)
            : Array.Empty<string>();

        return new Book(
            almaStrippedId,
            title,
            author,
            year,
            isbn,
            coverSources,
            onShelf,
            null
        );
    }

    /// <summary>
    /// Returns the institution's display name in a single-entry array if the requested
    /// institution has the book available; otherwise empty. Used to populate
    /// Book.BranchesOnShelf at the search level without per-branch detail.
    /// </summary>
    private static string[] InstitutionLabelIfAvailable(JsonElement deliveryDoc, string institutionFilter)
    {
        if (!deliveryDoc.TryGetProperty("delivery", out var delivery))
            return Array.Empty<string>();
        if (!delivery.TryGetProperty("almaInstitutionsList", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        foreach (var entry in list.EnumerateArray())
        {
            if (!entry.TryGetProperty("instCode", out var instCodeEl) || instCodeEl.ValueKind != JsonValueKind.String)
                continue;
            if (instCodeEl.GetString() != institutionFilter)
                continue;

            var status = entry.TryGetProperty("availabilityStatus", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            if (status != "available_in_institution")
                return Array.Empty<string>();

            var instName = entry.TryGetProperty("instName", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : institutionFilter;
            return new[] { instName ?? institutionFilter };
        }

        return Array.Empty<string>();
    }
}
