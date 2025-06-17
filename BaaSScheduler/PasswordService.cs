using System.Security.Cryptography;
using System.Text;

namespace BaaSScheduler;

public static class PasswordService
{
    private const int SaltSize = 32;
    private const int HashSize = 32;
    private const int Iterations = 100000; // PBKDF2 iterations

    /// <summary>
    /// Generates a cryptographically secure random password
    /// </summary>
    /// <param name="length">Password length (default: 32)</param>
    /// <returns>Base64-encoded random password</returns>
    public static string GenerateSecurePassword(int length = 32)
    {
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[length];
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Hashes a password using PBKDF2 with SHA-256
    /// Note: This uses PBKDF2 instead of Argon2 due to .NET compatibility.
    /// For production use with libsodium, consider using a proper Argon2 implementation.
    /// </summary>
    /// <param name="password">The password to hash</param>
    /// <returns>PBKDF2 hash of the password</returns>
    public static string HashPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password cannot be null or empty", nameof(password));

        // Generate a random salt
        using var rng = RandomNumberGenerator.Create();
        var salt = new byte[SaltSize];
        rng.GetBytes(salt);

        // Hash the password with PBKDF2
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        var hash = pbkdf2.GetBytes(HashSize);

        // Combine salt and hash for storage (salt + hash)
        var result = new byte[SaltSize + HashSize];
        Array.Copy(salt, 0, result, 0, SaltSize);
        Array.Copy(hash, 0, result, SaltSize, HashSize);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Verifies a password against its PBKDF2 hash
    /// </summary>
    /// <param name="password">The password to verify</param>
    /// <param name="hash">The PBKDF2 hash to verify against</param>
    /// <returns>True if the password matches the hash</returns>
    public static bool VerifyPassword(string password, string hash)
    {
        if (string.IsNullOrWhiteSpace(password))
            return false;
        
        if (string.IsNullOrWhiteSpace(hash))
            return false;

        try
        {
            var hashBytes = Convert.FromBase64String(hash);
            if (hashBytes.Length != SaltSize + HashSize)
                return false;

            var salt = new byte[SaltSize];
            var storedHash = new byte[HashSize];
            
            Array.Copy(hashBytes, 0, salt, 0, SaltSize);
            Array.Copy(hashBytes, SaltSize, storedHash, 0, HashSize);

            // Hash the provided password with the same salt
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
            var computedHash = pbkdf2.GetBytes(HashSize);
            
            return CryptographicOperations.FixedTimeEquals(storedHash, computedHash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generates a new secure password and its PBKDF2 hash
    /// </summary>
    /// <param name="passwordLength">Length of the generated password</param>
    /// <returns>Tuple containing the plain password and its hash</returns>
    public static (string Password, string Hash) GeneratePassword(int passwordLength = 32)
    {
        var password = GenerateSecurePassword(passwordLength);
        var hash = HashPassword(password);
        return (password, hash);
    }

    /// <summary>
    /// Generates a human-readable password with specified character sets
    /// </summary>
    /// <param name="length">Password length</param>
    /// <param name="includeSpecialChars">Include special characters</param>
    /// <returns>Human-readable password</returns>
    public static string GenerateReadablePassword(int length = 16, bool includeSpecialChars = true)
    {
        const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        const string numbers = "0123456789";
        const string specialChars = "!@#$%^&*";
        
        var chars = letters + numbers;
        if (includeSpecialChars)
            chars += specialChars;
        
        var password = new StringBuilder();
        using var rng = RandomNumberGenerator.Create();
        var randomBytes = new byte[length];
        rng.GetBytes(randomBytes);
        
        for (int i = 0; i < length; i++)
        {
            password.Append(chars[randomBytes[i] % chars.Length]);
        }
        
        return password.ToString();
    }
}
