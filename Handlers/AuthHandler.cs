using System.Text.Json;
using LlmRemote.Services;

namespace LlmRemote.Handlers;

public static class AuthHandler
{
    public static async Task<IResult> Login(HttpContext context, AuthService auth)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (auth.IsLockedOut(ip))
            return Results.Json(new { error = "Too many failed attempts. Try again in 15 minutes." }, statusCode: 429);

        var body = await JsonSerializer.DeserializeAsync<LoginRequest>(context.Request.Body);
        if (body is null || string.IsNullOrEmpty(body.username) || string.IsNullOrEmpty(body.password))
            return Results.BadRequest(new { error = "Username and password required" });

        if (!auth.ValidateCredentials(body.username, body.password))
        {
            auth.RecordFailedAttempt(ip);
            return Results.Unauthorized();
        }

        auth.ClearFailedAttempts(ip);
        var token = auth.CreateSession();
        context.Response.Cookies.Append("session", token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromHours(24)
        });

        return Results.Ok(new { success = true });
    }

    public static bool IsAuthenticated(HttpContext context, AuthService auth)
    {
        var token = context.Request.Cookies["session"];
        if (auth.ValidateSession(token))
            return true;

        var query = context.Request.Query["session"].FirstOrDefault();
        return auth.ValidateSession(query);
    }

    private record LoginRequest(string? username, string? password);
}
