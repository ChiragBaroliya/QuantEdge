using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository interface for user management and authentication persistence.
/// </summary>
public interface IUserRepository
{
    Task<AppUser?> GetByUsernameOrEmailAsync(string identifier);
    Task<AppUser?> GetByIdAsync(int id);
    Task<bool> ExistsByEmailAsync(string email);
    Task<bool> ExistsByUsernameAsync(string username);
    Task<int> CreateUserAsync(AppUser user);
    Task<bool> UpdatePasswordAsync(int userId, string newPasswordHash);
    Task<bool> HasAdminUserAsync();
}
