using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Role page permissions. Backed by role_page_permissions (schema.sql):
/// reads via fn_get_role_page_permissions (functions.sql), writes via
/// sp_save_role_page_permissions (stored_procedures.sql).
/// </summary>
public class RolePermissionRepository : IRolePermissionRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RolePermissionRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<IReadOnlyList<RolePagePermissionRowDto>> GetByRoleAsync(string role)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_role_page_permissions(@role);";

        var rows = await connection.QueryAsync<RolePagePermissionRowDto>(sql, new { role });
        return rows.ToList();
    }

    public async Task SaveAsync(string role, IReadOnlyDictionary<string, bool> pageAccess, string? updatedBy)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "CALL sp_save_role_page_permissions(@role, @pageKeys, @allowed, @updatedBy);";

        await connection.ExecuteAsync(sql, new
        {
            role,
            pageKeys = pageAccess.Keys.ToArray(),
            allowed = pageAccess.Values.ToArray(),
            updatedBy
        });
    }
}
