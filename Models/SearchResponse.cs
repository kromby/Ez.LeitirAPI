namespace Ez.Leitir.Models;

public record SearchResponse(
    Book[] Available,
    Book[] OnLoan,
    int Total,
    string? DidYouMean = null
);
