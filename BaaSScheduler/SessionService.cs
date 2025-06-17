using System.Collections.Concurrent;

namespace BaaSScheduler;

public class SessionService
{
    private readonly ConcurrentDictionary<string, DateTime> _activeSessions = new();
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromHours(1);    public string CreateSession(string password, string configPassword)
    {
        // Support both plain text passwords (for backward compatibility) and Argon2 hashes
        bool isValidPassword;
        
        if (configPassword.Length > 50 && IsBase64String(configPassword))
        {
            // Assume it's an Argon2 hash if it's long and base64-encoded
            isValidPassword = PasswordService.VerifyPassword(password, configPassword);
        }
        else
        {
            // Plain text comparison for backward compatibility
            isValidPassword = password == configPassword;
        }

        if (!isValidPassword)
        {
            throw new UnauthorizedAccessException("Invalid password");
        }

        var sessionId = Guid.NewGuid().ToString();
        _activeSessions[sessionId] = DateTime.UtcNow.Add(_sessionTimeout);
        
        // Clean up expired sessions
        CleanupExpiredSessions();
        
        return sessionId;
    }

    private static bool IsBase64String(string s)
    {
        try
        {
            Convert.FromBase64String(s);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsValidSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return false;

        if (_activeSessions.TryGetValue(sessionId, out var expiry))
        {
            if (DateTime.UtcNow < expiry)
            {
                // Extend session on activity
                _activeSessions[sessionId] = DateTime.UtcNow.Add(_sessionTimeout);
                return true;
            }
            else
            {
                // Remove expired session
                _activeSessions.TryRemove(sessionId, out _);
            }
        }

        return false;
    }

    public void InvalidateSession(string sessionId)
    {
        _activeSessions.TryRemove(sessionId, out _);
    }

    private void CleanupExpiredSessions()
    {
        var now = DateTime.UtcNow;
        var expiredSessions = _activeSessions
            .Where(kvp => now >= kvp.Value)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var sessionId in expiredSessions)
        {
            _activeSessions.TryRemove(sessionId, out _);
        }
    }
}
