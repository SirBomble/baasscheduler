using System.Collections.Concurrent;

namespace BaaSScheduler;

public class SessionService
{
    private readonly ConcurrentDictionary<string, DateTime> _activeSessions = new();
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromHours(1);

    public string CreateSession(string password, string configPassword)
    {
        if (password != configPassword)
        {
            throw new UnauthorizedAccessException("Invalid password");
        }

        var sessionId = Guid.NewGuid().ToString();
        _activeSessions[sessionId] = DateTime.UtcNow.Add(_sessionTimeout);
        
        // Clean up expired sessions
        CleanupExpiredSessions();
        
        return sessionId;
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
