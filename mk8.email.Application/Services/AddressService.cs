using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public class AddressService(EmailDbContext db) : IAddressService
{
    public async Task<AddressDTO?> CreateAddressAsync(Guid userId, CreateAddressRequestDTO request)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
            return null;

        var role = Enum.Parse<UserRole>(user.Role);
        switch (role)
        {
            case UserRole.SuperAdmin:
                break;

            case UserRole.CompanyAdmin:
                if (user.CompanyId is null || request.CompanyId != user.CompanyId)
                    return null;
                break;

            default:
                return null;
        }

        if (await db.Addresses.AnyAsync(a => a.Domain == request.Domain))
            return null;

        var companyLimits = await db.CompanyLimits.AsNoTracking()
            .FirstOrDefaultAsync(l => l.CompanyId == request.CompanyId);
        var globalLimits = await db.GlobalLimits.AsNoTracking().FirstAsync();

        var maxDomains = companyLimits?.MaxDomains ?? globalLimits.DefaultMaxDomainsPerCompany;
        if (maxDomains > 0 &&
            await db.Addresses.CountAsync(a => a.CompanyId == request.CompanyId) >= maxDomains)
            return null;

        var address = new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = request.Domain,
            CompanyId = request.CompanyId,
        };

        db.Addresses.Add(address);
        await db.SaveChangesAsync();

        return new AddressDTO(address.Id, address.Domain, address.CompanyId, address.IsActive, address.CreatedAt);
    }

    public async Task<IReadOnlyList<AddressDTO>> GetAllAddressesAsync()
    {
        return await db.Addresses
            .AsNoTracking()
            .Select(a => new AddressDTO(a.Id, a.Domain, a.CompanyId, a.IsActive, a.CreatedAt))
            .ToListAsync();
    }
}
