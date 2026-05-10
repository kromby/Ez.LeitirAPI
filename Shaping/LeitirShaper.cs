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
    /// Transforms PNX search results into available and on-loan books.
    /// </summary>
    /// <param name="pnxs">JsonElement with docs array containing search results</param>
    /// <param name="delivery">JsonElement with docs array containing delivery info (may be empty)</param>
    /// <returns>SearchResponse with available[], onLoan[], total count, and optional didYouMean</returns>
    public static SearchResponse ShapeSearch(JsonElement pnxs, JsonElement delivery)
    {
        // Build delivery lookup map
        var deliveryByMms = new Dictionary<string, JsonElement>();
        if (delivery.TryGetProperty("docs", out var deliveryDocs) && deliveryDocs.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in deliveryDocs.EnumerateArray())
            {
                var id = FirstString(doc, "pnx/control/recordid");
                if (id != null)
                {
                    deliveryByMms[id] = doc;
                }
            }
        }

        var available = new List<Book>();
        var onLoan = new List<Book>();

        // Process each PNX document
        if (pnxs.TryGetProperty("docs", out var pnxDocs) && pnxDocs.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in pnxDocs.EnumerateArray())
            {
                var book = PnxToBook(doc, deliveryByMms);
                if (book == null)
                    continue;

                if (book.BranchesOnShelf.Length > 0)
                    available.Add(book);
                else
                    onLoan.Add(book);
            }
        }

        // Get total count
        int total = available.Count + onLoan.Count;
        if (pnxs.TryGetProperty("info", out var info) && info.TryGetProperty("total", out var totalElement))
        {
            if (totalElement.TryGetInt32(out var totalValue))
                total = totalValue;
        }

        // Get didYouMean if present
        string? didYouMean = null;
        if (pnxs.TryGetProperty("did_u_mean", out var didYouMeanElement) && didYouMeanElement.ValueKind == JsonValueKind.String)
        {
            didYouMean = didYouMeanElement.GetString();
        }

        return new SearchResponse(available.ToArray(), onLoan.ToArray(), total, didYouMean);
    }

    /// <summary>
    /// Transforms a detailed book record with physical availability information.
    /// </summary>
    /// <param name="pnxDoc">JsonElement with full record (may be array or object)</param>
    /// <param name="physical">JsonElement with availability info (may be array or object with records[])</param>
    /// <returns>BookResponse with BookDetail and BranchAvailability[] array</returns>
    public static BookResponse ShapeBook(JsonElement pnxDoc, JsonElement physical)
    {
        // Extract pnx from either parameter, handling both object and array formats
        JsonElement? pnxFromDoc = null;
        if (pnxDoc.ValueKind == JsonValueKind.Array && pnxDoc.GetArrayLength() > 0)
        {
            var firstDoc = pnxDoc[0];
            if (firstDoc.TryGetProperty("pnx", out var pnxElement))
                pnxFromDoc = pnxElement;
        }
        else if (pnxDoc.TryGetProperty("pnx", out var pnxElement))
        {
            pnxFromDoc = pnxElement;
        }

        JsonElement? pnxFromPhysical = null;
        if (physical.ValueKind == JsonValueKind.Array && physical.GetArrayLength() > 0)
        {
            var firstRecord = physical[0];
            if (firstRecord.TryGetProperty("pnx", out var pnxElement))
                pnxFromPhysical = pnxElement;
        }
        else if (physical.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array && records.GetArrayLength() > 0)
        {
            var firstRecord = records[0];
            if (firstRecord.TryGetProperty("pnx", out var pnxElement))
                pnxFromPhysical = pnxElement;
        }

        var pnx = pnxFromDoc ?? pnxFromPhysical;

        // Extract book details from pnx
        var mmsId = FirstString(pnx, "control/recordid") ?? "";
        var title = FirstString(pnx, "display/title") ?? "(óþekktur titill)";
        var author = FirstString(pnx, "display/creator") ?? FirstString(pnx, "display/contributor");
        var year = YearOf(FirstString(pnx, "display/creationdate"));
        var isbn = FirstString(pnx, "search/isbn");
        var summary = FirstString(pnx, "display/description");
        var genres = GetGenres(pnx);
        var pageCount = GetPageCount(FirstString(pnx, "display/format"));
        var coverSources = ToCoverSources(mmsId, isbn);

        // Process branch availability
        var branches = new List<BranchAvailability>();
        var onShelfNames = new List<string>();
        var returns = new List<string>();

        if (physical.TryGetProperty("records", out var physicalRecords) && physicalRecords.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in physicalRecords.EnumerateArray())
            {
                var branch = GetBranchName(record);
                var callNumber = GetCallNumber(record);
                var isAvailable = IsAvailable(record);

                if (isAvailable)
                {
                    branches.Add(new BranchAvailability(branch, "on-shelf", callNumber));
                    onShelfNames.Add(branch);
                }
                else
                {
                    var dueDate = GetDueDate(record);
                    branches.Add(new BranchAvailability(branch, "on-loan", callNumber, dueDate));
                    if (dueDate != null)
                        returns.Add(dueDate);
                }
            }
        }

        returns.Sort();

        var book = new BookDetail(
            mmsId,
            title,
            coverSources,
            onShelfNames.ToArray(),
            genres,
            author,
            year,
            isbn,
            onShelfNames.Count == 0 && returns.Count > 0 ? returns[0] : null,
            summary,
            pageCount
        );

        return new BookResponse(book, branches.ToArray());
    }

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
    /// Builds an array of cover source URLs for a book.
    /// </summary>
    /// <param name="mmsId">MMS identifier for the book</param>
    /// <param name="isbn">Optional ISBN for secondary cover source</param>
    /// <returns>Array of cover source URLs</returns>
    private static string[] ToCoverSources(string mmsId, string? isbn)
    {
        var sources = new List<string>
        {
            $"https://baekur.is/cover/tbn/{mmsId}"
        };

        if (!string.IsNullOrEmpty(isbn))
        {
            var encodedIsbn = Uri.EscapeDataString(isbn);
            sources.Add(
                $"https://proxy-euf.hosted.exlibrisgroup.com/exl_rewrite/syndetics.com/index.php?client=primo&isbn={encodedIsbn}/sc.jpg"
            );
        }

        return sources.ToArray();
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
    /// Extracts branch availability from a delivery record.
    /// </summary>
    /// <param name="deliveryDoc">JsonElement representing a delivery record</param>
    /// <returns>Tuple of on-shelf branches and earliest return date</returns>
    private static (string[] onShelf, string? earliestReturn) DeliveryToBranches(JsonElement? deliveryDoc)
    {
        var onShelf = new List<string>();
        var returns = new List<string>();

        if (deliveryDoc == null)
            return ([], null);

        if (!deliveryDoc.Value.TryGetProperty("delivery", out var delivery))
            return ([], null);

        if (!delivery.TryGetProperty("holding", out var holdings) || holdings.ValueKind != JsonValueKind.Array)
            return ([], null);

        var branchSet = new HashSet<string>();

        foreach (var holding in holdings.EnumerateArray())
        {
            var branch = GetBranchFromHolding(holding);
            if (string.IsNullOrEmpty(branch))
                continue;

            var isAvailable = holding.TryGetProperty("availabilityStatus", out var status) && status.GetString() == "available"
                || holding.TryGetProperty("availability", out var avail) && avail.GetString() == "available";

            if (isAvailable)
            {
                branchSet.Add(branch);
            }
            else if (holding.TryGetProperty("dueDate", out var dueDate) && dueDate.ValueKind == JsonValueKind.String)
            {
                returns.Add(dueDate.GetString() ?? "");
            }
        }

        onShelf.AddRange(branchSet);
        returns.Sort();

        var earliestReturn = returns.Count > 0 ? returns[0] : null;
        return (onShelf.ToArray(), earliestReturn);
    }

    /// <summary>
    /// Extracts branch name from a holding record.
    /// </summary>
    private static string GetBranchFromHolding(JsonElement holding)
    {
        if (holding.TryGetProperty("libraryName", out var libraryName) && libraryName.ValueKind == JsonValueKind.String)
            return libraryName.GetString() ?? "";

        if (holding.TryGetProperty("subLocation", out var subLocation) && subLocation.ValueKind == JsonValueKind.String)
            return subLocation.GetString() ?? "";

        if (holding.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.String)
            return location.GetString() ?? "";

        return "";
    }

    /// <summary>
    /// Extracts branch name from a physical record.
    /// </summary>
    private static string GetBranchName(JsonElement record)
    {
        if (record.TryGetProperty("libraryName", out var libraryName) && libraryName.ValueKind == JsonValueKind.String)
            return libraryName.GetString() ?? "(óþekkt útibú)";

        if (record.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.String)
            return location.GetString() ?? "(óþekkt útibú)";

        return "(óþekkt útibú)";
    }

    /// <summary>
    /// Gets the call number from a physical record.
    /// </summary>
    private static string? GetCallNumber(JsonElement record)
    {
        if (record.TryGetProperty("callNumber", out var callNumber) && callNumber.ValueKind == JsonValueKind.String)
            return callNumber.GetString();

        return null;
    }

    /// <summary>
    /// Determines if a physical record is available.
    /// </summary>
    private static bool IsAvailable(JsonElement record)
    {
        if (record.TryGetProperty("availabilityStatus", out var status) && status.ValueKind == JsonValueKind.String)
            return status.GetString() == "available";

        if (record.TryGetProperty("availability", out var avail) && avail.ValueKind == JsonValueKind.String)
            return avail.GetString() == "available";

        return false;
    }

    /// <summary>
    /// Gets the due date from a physical record.
    /// </summary>
    private static string? GetDueDate(JsonElement record)
    {
        if (record.TryGetProperty("dueDate", out var dueDate) && dueDate.ValueKind == JsonValueKind.String)
            return dueDate.GetString();

        return null;
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
    private static Book? PnxToBook(JsonElement doc, Dictionary<string, JsonElement> deliveryByMms)
    {
        var mmsId = FirstString(doc, "pnx/control/recordid");
        if (mmsId == null)
            return null;

        if (!IsPhysicalBook(doc))
            return null;

        var title = FirstString(doc, "pnx/display/title") ?? "(óþekktur titill)";
        var author = FirstString(doc, "pnx/display/creator") ?? FirstString(doc, "pnx/display/contributor");
        var yearStr = FirstString(doc, "pnx/display/creationdate");
        var year = YearOf(yearStr);
        var isbn = FirstString(doc, "pnx/search/isbn");
        var coverSources = ToCoverSources(mmsId, isbn);

        // Get delivery info for this book
        var branches = deliveryByMms.TryGetValue(mmsId, out var delivery)
            ? DeliveryToBranches(delivery)
            : ([], null);

        return new Book(
            mmsId,
            title,
            author,
            year,
            isbn,
            coverSources,
            branches.onShelf,
            branches.onShelf.Length == 0 ? branches.earliestReturn : null
        );
    }
}
