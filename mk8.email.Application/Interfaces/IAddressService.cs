using mk8.email.Contracts.DTOs;

namespace mk8.email.Application.Interfaces;

public interface IAddressService
{
    Task<AddressDTO?> CreateAddressAsync(Guid userId, CreateAddressRequestDTO request);
    Task<IReadOnlyList<AddressDTO>> GetAllAddressesAsync();
}
