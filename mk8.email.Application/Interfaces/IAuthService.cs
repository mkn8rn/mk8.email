using mk8.email.Contracts.DTOs;

namespace mk8.email.Application.Interfaces;

public interface IAuthService
{
    Task<LoginResultDTO> RegisterAsync(RegisterRequestDTO request);
    Task<LoginResultDTO> LoginAsync(LoginRequestDTO request);
}
