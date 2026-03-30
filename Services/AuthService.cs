using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LlmRemote.Services;

public class AuthService
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(
        string lpszUsername, string lpszDomain, string lpszPassword,
        int dwLogonType, int dwLogonProvider, out IntPtr phToken);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();
    private readonly ConcurrentDictionary<string, (int count, DateTime lastAttempt)> _failedAttempts = new();
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public bool IsLockedOut(string ip)
    {
        if (_failedAttempts.TryGetValue(ip, out var info))
        {
            if (info.count >= MaxFailedAttempts && DateTime.UtcNow - info.lastAttempt < LockoutDuration)
                return true;
            if (DateTime.UtcNow - info.lastAttempt >= LockoutDuration)
                _failedAttempts.TryRemove(ip, out _);
        }
        return false;
    }

    public void RecordFailedAttempt(string ip)
    {
        _failedAttempts.AddOrUpdate(ip,
            _ => (1, DateTime.UtcNow),
            (_, old) => (old.count + 1, DateTime.UtcNow));
    }

    public void ClearFailedAttempts(string ip)
    {
        _failedAttempts.TryRemove(ip, out _);
    }

    public bool ValidateCredentials(string username, string password)
    {
        var domain = ".";
        if (username.Contains('\\'))
        {
            var parts = username.Split('\\', 2);
            domain = parts[0];
            username = parts[1];
        }

        bool result = LogonUser(username, domain, password,
            LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, out var token);

        if (result)
            CloseHandle(token);

        return result;
    }

    public string CreateSession()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = DateTime.UtcNow;
        return token;
    }

    public bool ValidateSession(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        if (_sessions.TryGetValue(token, out var created))
        {
            if (DateTime.UtcNow - created < TimeSpan.FromHours(24))
                return true;
            _sessions.TryRemove(token, out _);
        }
        return false;
    }
}
