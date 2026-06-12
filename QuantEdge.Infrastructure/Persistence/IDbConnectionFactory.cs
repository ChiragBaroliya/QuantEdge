using System.Data;

namespace QuantEdge.Infrastructure.Persistence;

/// <summary>
/// Factory interface tasked with creating ADO.NET database connections for Dapper execution.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Creates and returns a new database connection instance.
    /// </summary>
    IDbConnection CreateConnection();
}
