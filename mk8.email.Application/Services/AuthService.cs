using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Services;

public class AuthService(EmailDbContext db) : IAuthService
{
    public async Task<LoginResultDTO> LoginAsync(LoginRequestDTO request)
    {
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == request.Username);

        if (user is null || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            return new LoginResultDTO(false, null, "Invalid username or password.");

        return new LoginResultDTO(true, ToDTO(user), null);
    }

    public async Task<LoginResultDTO> RegisterAsync(RegisterRequestDTO request)
    {
        if (await db.Users.AnyAsync(u => u.Username == request.Username))
            return new LoginResultDTO(false, null, "An account with this username already exists.");

        var user = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = request.Username,
            PasswordHash = PasswordHasher.Hash(request.Password),
            Role = nameof(UserRole.User),
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return new LoginResultDTO(true, ToDTO(user), null);
    }

    private static UserDTO ToDTO(UserDB u) => new(
        u.Id, u.Username, Enum.Parse<UserRole>(u.Role), u.CompanyId, u.IsActive, u.CreatedAt);
}
