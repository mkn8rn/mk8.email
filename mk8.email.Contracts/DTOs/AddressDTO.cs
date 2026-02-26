namespace mk8.email.Contracts.DTOs;

public record AddressDTO(
Guid Id,
string Domain,
Guid CompanyId,
bool IsActive,
DateTime CreatedAt);
