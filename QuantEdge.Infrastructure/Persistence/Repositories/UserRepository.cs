using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

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

    public async Task<bool> ExistsByEmailAsync(string email, int? excludeUserId = null)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        using var conn = _connectionFactory.CreateConnection();
        const string sql = @"
            SELECT EXISTS (
                SELECT 1 FROM app_users 
                WHERE LOWER(email) = LOWER(@Email)
                AND (@ExcludeUserId IS NULL OR id <> @ExcludeUserId)
            );";
        return await conn.ExecuteScalarAsync<bool>(sql, new { Email = email.Trim(), ExcludeUserId = excludeUserId });
    }

    public async Task<bool> ExistsByUsernameAsync(string username, int? excludeUserId = null)
    {
        if (string.IsNullOrWhiteSpace(username))
            return false;

        using var conn = _connectionFactory.CreateConnection();
        const string sql = @"
            SELECT EXISTS (
                SELECT 1 FROM app_users 
                WHERE LOWER(username) = LOWER(@Username)
                AND (@ExcludeUserId IS NULL OR id <> @ExcludeUserId)
            );";
        return await conn.ExecuteScalarAsync<bool>(sql, new { Username = username.Trim(), ExcludeUserId = excludeUserId });
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

    public async Task<bool> UpdateUserAsync(AppUser user)
    {
        if (user == null)
            throw new ArgumentNullException(nameof(user));

        using var conn = _connectionFactory.CreateConnection();
        const string sql = @"
            UPDATE app_users
            SET full_name = @FullName,
                email = NULLIF(LOWER(@Email), ''),
                mobile_no = NULLIF(@MobileNo, ''),
                role = @Role,
                updated_at = NOW()
            WHERE id = @Id;";

        int rows = await conn.ExecuteAsync(sql, new
        {
            user.Id,
            user.FullName,
            user.Email,
            user.MobileNo,
            user.Role
        });

        return rows > 0;
    }

    public async Task<bool> UpdatePasswordAsync(int userId, string newPasswordHash)
    {
        using var conn = _connectionFactory.CreateConnection();
        const string sql = "SELECT fn_update_user_password(@UserId, @NewPasswordHash);";
        return await conn.ExecuteScalarAsync<bool>(sql, new { UserId = userId, NewPasswordHash = newPasswordHash });
    }

    public async Task<bool> DeleteUserAsync(int userId)
    {
        using var conn = _connectionFactory.CreateConnection();
        const string sql = "DELETE FROM app_users WHERE id = @UserId;";
        int rows = await conn.ExecuteAsync(sql, new { UserId = userId });
        return rows > 0;
    }

    public async Task<bool> HasAdminUserAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        const string sql = "SELECT fn_has_admin_user();";
        return await conn.ExecuteScalarAsync<bool>(sql);
    }

    public async Task<int> GetAdminCountAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        const string sql = "SELECT COUNT(*)::INT FROM app_users WHERE LOWER(role) = 'admin';";
        return await conn.ExecuteScalarAsync<int>(sql);
    }

    public async Task<PaginatedUserResultDto> GetPaginatedUsersAsync(string? search, string? roleFilter, int pageNumber, int pageSize)
    {
        using var conn = _connectionFactory.CreateConnection();
        const string sql = "SELECT * FROM sp_get_paginated_users(@Search, @RoleFilter, @PageNumber, @PageSize);";

        var rawRows = await conn.QueryAsync(sql, new
        {
            Search = search ?? string.Empty,
            RoleFilter = roleFilter ?? string.Empty,
            PageNumber = pageNumber < 1 ? 1 : pageNumber,
            PageSize = pageSize < 1 ? 25 : pageSize
        });

        var rowList = rawRows.ToList();
        int totalCount = 0;
        var items = new List<UserDto>();

        foreach (var row in rowList)
        {
            IDictionary<string, object> dict = (IDictionary<string, object>)row;
            
            if (dict.TryGetValue("TotalRecords", out var totalVal) || dict.TryGetValue("totalrecords", out totalVal))
            {
                totalCount = Convert.ToInt32(totalVal);
            }

            int id = Convert.ToInt32(dict["Id"] ?? dict["id"]);
            string fullName = (string)(dict["FullName"] ?? dict["full_name"] ?? string.Empty);
            string? email = (string?)(dict.ContainsKey("Email") ? dict["Email"] : dict.ContainsKey("email") ? dict["email"] : null);
            string? mobileNo = (string?)(dict.ContainsKey("MobileNo") ? dict["MobileNo"] : dict.ContainsKey("mobile_no") ? dict["mobile_no"] : null);
            string username = (string)(dict["Username"] ?? dict["username"] ?? string.Empty);
            string role = (string)(dict["Role"] ?? dict["role"] ?? "User");
            DateTime createdAt = Convert.ToDateTime(dict["CreatedAt"] ?? dict["created_at"]);

            items.Add(new UserDto
            {
                Id = id,
                FullName = fullName,
                Email = email,
                MobileNo = mobileNo,
                Username = username,
                Role = role,
                CreatedAt = createdAt
            });
        }

        return new PaginatedUserResultDto
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<UserSummaryDto> GetUserSummaryAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        const string sql = @"
            SELECT 
                COUNT(*)::INT AS TotalUsers,
                COUNT(*) FILTER (WHERE LOWER(role) = 'admin')::INT AS AdminCount,
                COUNT(*) FILTER (WHERE LOWER(role) <> 'admin')::INT AS UserCount
            FROM app_users;";

        var summary = await conn.QueryFirstOrDefaultAsync<UserSummaryDto>(sql);
        return summary ?? new UserSummaryDto();
    }
}
