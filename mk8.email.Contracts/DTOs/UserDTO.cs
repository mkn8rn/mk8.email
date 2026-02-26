using mk8.email.Contracts.Enums;

namespace mk8.email.Contracts.DTOs;

public record UserDTO(
Guid Id,
string Username,
UserRole Role,
Guid? CompanyId,
bool IsActive,
DateTime CreatedAt);
