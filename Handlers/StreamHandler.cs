using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LlmRemote.Services;

namespace LlmRemote.Handlers;

public static class StreamHandler
{
    private static long _totalBytesSent;
    private static DateTime _startTime = DateTime.UtcNow;

    public static long TotalBytesSent => _totalBytesSent;

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
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        Log.Write($"[Stream] {ip} connected to window hwnd={hwndLong}");

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

        Log.Write($"[Stream] {ip} disconnected from window hwnd={hwndLong}");
    }

    private static async Task SendFrameLoop(WebSocket ws, IntPtr hWnd,
        WindowService windowService, CancellationToken ct)
    {
        int frameCount = 0;
        var fpsTimer = System.Diagnostics.Stopwatch.StartNew();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var frame = windowService.CaptureWindow(hWnd);
                sw.Stop();
                if (frame != null)
                {
                    frameCount++;
                    var sent = frame.Data.Length;
                    Interlocked.Add(ref _totalBytesSent, sent);
                    var total = Interlocked.Read(ref _totalBytesSent);

                    // Calculate FPS
                    double fps = 0;
                    if (fpsTimer.ElapsedMilliseconds > 0)
                        fps = frameCount * 1000.0 / fpsTimer.ElapsedMilliseconds;

                    var header = JsonSerializer.Serialize(new
                    {
                        d = frame.IsDiff ? 1 : 0,
                        x = frame.X,
                        y = frame.Y,
                        w = frame.Width,
                        h = frame.Height,
                        fw = frame.FullWidth,
                        fh = frame.FullHeight,
                        tb = total,
                        fps = Math.Round(fps, 1)
                    });
                    var headerBytes = Encoding.UTF8.GetBytes(header);
                    Interlocked.Add(ref _totalBytesSent, headerBytes.Length);

                    await ws.SendAsync(headerBytes, WebSocketMessageType.Text, true, ct);
                    await ws.SendAsync(frame.Data, WebSocketMessageType.Binary, true, ct);

                    // Log + reset FPS every 5 seconds
                    if (fpsTimer.ElapsedMilliseconds > 5000)
                    {
                        var totalMB = total / (1024.0 * 1024.0);
                        Log.Write($"[Perf] {fps:F1} fps | capture: {sw.ElapsedMilliseconds}ms | total: {totalMB:F1} MB");
                        frameCount = 0;
                        fpsTimer.Restart();
                    }
                }

                await Task.Yield();
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
