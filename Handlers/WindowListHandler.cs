using LlmRemote.Services;

namespace LlmRemote.Handlers;

public static class WindowListHandler
{
    public static IResult GetWindows(HttpContext context, AuthService auth, WindowService windowService)
    {
        if (!AuthHandler.IsAuthenticated(context, auth))
            return Results.Unauthorized();

        var windows = windowService.GetWindows();
        return Results.Ok(windows);
    }
}
