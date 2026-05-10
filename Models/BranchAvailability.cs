namespace Ez.Leitir.Models;

public record BranchAvailability(
    string Branch,
    string Status,
    string? CallNumber = null,
    string? EarliestReturn = null
);
