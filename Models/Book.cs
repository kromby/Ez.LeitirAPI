namespace Ez.Leitir.Models;

public record Book(
    string MmsId,
    string Title,
    string? Author = null,
    int? Year = null,
    string? Isbn = null,
    string[] CoverSources = default!,
    string[] BranchesOnShelf = default!,
    string? EarliestReturn = null
);
