using System;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper-based repository implementation for user management in PostgreSQL using Database Functions.
/// </summary>
public class UserRepository : IUserRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public UserRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<AppUser?> GetByUsernameOrEmailAsync(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return null;

        using var conn = _connectionFactory.CreateConnection();
        const string sql = "SELECT * FROM fn_get_user_by_identifier(@Identifier);";

        return await conn.QueryFirstOrDefaultAsync<AppUser>(sql, new { Identifier = identifier.Trim() });
    }

    public async Task<AppUser?> GetByIdAsync(int id)
    {
        using var conn = _connectionFactory.CreateConnection();
        const string sql = "SELECT * FROM fn_get_user_by_id(@Id);";

        return await conn.QueryFirstOrDefaultAsync<AppUser>(sql, new { Id = id });
    }

    public async Task<bool> ExistsByEmailAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        using var conn = _connectionFactory.CreateConnection();
        const string sql = "SELECT fn_check_email_exists(@Email);";
        return await conn.ExecuteScalarAsync<bool>(sql, new { Email = email.Trim() });
    }

    public async Task<bool> ExistsByUsernameAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return false;

        using var conn = _connectionFactory.CreateConnection();
        const string sql = "SELECT fn_check_username_exists(@Username);";
        return await conn.ExecuteScalarAsync<bool>(sql, new { Username = username.Trim() });
    }

    public async Task<int> CreateUserAsync(AppUser user)
    {
        if (user == null)
            throw new ArgumentNullException(nameof(user));

        using var conn = _connectionFactory.CreateConnection();
        const string sql = "SELECT fn_register_user(@FullName, @Email, @MobileNo, @Username, @PasswordHash, @Role);";

        return await conn.ExecuteScalarAsync<int>(sql, new
        {
            user.FullName,
            user.Email,
            user.MobileNo,
            user.Username,
            user.PasswordHash,
            user.Role
        });
    }

    public async Task<bool> UpdatePasswordAsync(int userId, string newPasswordHash)
    {
        using var conn = _connectionFactory.CreateConnection();
        const string sql = "SELECT fn_update_user_password(@UserId, @NewPasswordHash);";
        return await conn.ExecuteScalarAsync<bool>(sql, new { UserId = userId, NewPasswordHash = newPasswordHash });
    }

    public async Task<bool> HasAdminUserAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        const string sql = "SELECT fn_has_admin_user();";
        return await conn.ExecuteScalarAsync<bool>(sql);
    }
}
