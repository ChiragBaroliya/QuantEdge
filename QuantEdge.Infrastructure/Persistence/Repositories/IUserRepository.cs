using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository interface for user management and authentication persistence.
/// </summary>
public interface IUserRepository
{
    Task<AppUser?> GetByUsernameOrEmailAsync(string identifier);
    Task<AppUser?> GetByIdAsync(int id);
    Task<bool> ExistsByEmailAsync(string email, int? excludeUserId = null);
    Task<bool> ExistsByUsernameAsync(string username, int? excludeUserId = null);
    Task<int> CreateUserAsync(AppUser user);
    Task<bool> UpdateUserAsync(AppUser user);
    Task<bool> UpdatePasswordAsync(int userId, string newPasswordHash);
    Task<bool> DeleteUserAsync(int userId);
    Task<bool> HasAdminUserAsync();
    Task<int> GetAdminCountAsync();
    Task<PaginatedUserResultDto> GetPaginatedUsersAsync(string? search, string? roleFilter, int pageNumber, int pageSize);
    Task<UserSummaryDto> GetUserSummaryAsync();
}
