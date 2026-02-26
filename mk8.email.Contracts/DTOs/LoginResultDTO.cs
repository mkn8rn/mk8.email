namespace mk8.email.Contracts.DTOs;

public record LoginResultDTO(bool Success, UserDTO? User, string? Error);
