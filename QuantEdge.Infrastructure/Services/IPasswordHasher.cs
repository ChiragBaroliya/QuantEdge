namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Service interface for hashing and verifying passwords securely.
/// </summary>
public interface IPasswordHasher
{
    string HashPassword(string password);
    bool VerifyPassword(string password, string hashedPassword);
}
