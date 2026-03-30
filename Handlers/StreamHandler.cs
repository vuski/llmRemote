using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LlmRemote.Services;

namespace LlmRemote.Handlers;

public static class StreamHandler
{
    public static async Task HandleWebSocket(HttpContext context, AuthService auth,
        WindowService windowService, InputService inputService)
    {
        if (!AuthHandler.IsAuthenticated(context, auth))
        {
            context.Response.StatusCode = 401;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var hwndStr = context.Request.Query["hwnd"].FirstOrDefault();
        if (!long.TryParse(hwndStr, out var hwndLong))
        {
            context.Response.StatusCode = 400;
            return;
        }

        var hWnd = (IntPtr)hwndLong;
        var ws = await context.WebSockets.AcceptWebSocketAsync();

        var cts = new CancellationTokenSource();

        var receiveTask = ReceiveInputLoop(ws, hWnd, windowService, inputService, cts.Token);
        var sendTask = SendFrameLoop(ws, hWnd, windowService, cts.Token);

        await Task.WhenAny(receiveTask, sendTask);
        cts.Cancel();

        if (ws.State == WebSocketState.Open)
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            }
            catch { }
        }
    }

    private static async Task SendFrameLoop(WebSocket ws, IntPtr hWnd,
        WindowService windowService, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                var frame = windowService.CaptureWindow(hWnd);
                if (frame != null)
                {
                    // Send header as text: {isDiff, x, y, w, h, fw, fh, size}
                    var header = JsonSerializer.Serialize(new
                    {
                        d = frame.IsDiff ? 1 : 0,
                        x = frame.X,
                        y = frame.Y,
                        w = frame.Width,
                        h = frame.Height,
                        fw = frame.FullWidth,
                        fh = frame.FullHeight
                    });
                    var headerBytes = Encoding.UTF8.GetBytes(header);
                    await ws.SendAsync(headerBytes, WebSocketMessageType.Text, true, ct);

                    // Send WebP data as binary
                    await ws.SendAsync(frame.Data, WebSocketMessageType.Binary, true, ct);
                }

                await Task.Delay(80, ct); // ~12fps
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    private static async Task ReceiveInputLoop(WebSocket ws, IntPtr hWnd,
        WindowService windowService, InputService inputService, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var msg = JsonSerializer.Deserialize<InputMessage>(json);
                    if (msg == null) continue;

                    HandleInput(hWnd, msg, windowService, inputService);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    private static void HandleInput(IntPtr hWnd, InputMessage msg,
        WindowService windowService, InputService inputService)
    {
        switch (msg.type)
        {
            case "keydown":
                inputService.SendKeyDown(hWnd, msg.keyCode);
                break;
            case "keyup":
                inputService.SendKeyUp(hWnd, msg.keyCode);
                break;
            case "char":
                if (msg.charValue != null)
                    inputService.SendChar(hWnd, msg.charValue[0]);
                break;
            case "click":
                inputService.SendMouseClick(hWnd, msg.x, msg.y, msg.rightButton);
                break;
            case "mousemove":
                inputService.SendMouseMove(hWnd, msg.x, msg.y);
                break;
            case "scroll":
                inputService.SendScroll(hWnd, msg.x, msg.y, msg.delta);
                break;
            case "resize":
                windowService.ResizeWindow(hWnd, msg.width, msg.height);
                break;
            case "focus":
                inputService.FocusWindow(hWnd);
                break;
            case "paste":
                if (msg.text != null)
                    inputService.PasteText(hWnd, msg.text, msg.enter);
                break;
        }
    }

    private record InputMessage
    {
        public string type { get; init; } = "";
        public int keyCode { get; init; }
        public string? charValue { get; init; }
        public string? text { get; init; }
        public int x { get; init; }
        public int y { get; init; }
        public bool rightButton { get; init; }
        public int delta { get; init; }
        public int width { get; init; }
        public int height { get; init; }
        public bool enter { get; init; }
    }
}
