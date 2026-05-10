namespace Ez.Leitir.Models;

public record BookResponse(
    BookDetail Book,
    BranchAvailability[] Branches
);
