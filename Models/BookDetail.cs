namespace Ez.Leitir.Models;

public record BookDetail(
    string MmsId,
    string Title,
    string[] CoverSources,
    string[] BranchesOnShelf,
    string[] Genres,
    string? Author = null,
    int? Year = null,
    string? Isbn = null,
    string? EarliestReturn = null,
    string? Summary = null,
    int? PageCount = null
);
