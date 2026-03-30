namespace LlmRemote.Services;

public static class Log
{
    private static readonly string LogDir = Path.Combine(
        AppContext.BaseDirectory == Directory.GetCurrentDirectory()
            ? Directory.GetCurrentDirectory()
            : AppContext.BaseDirectory,
        "logs");

    private static readonly object _lock = new();

    static Log()
    {
        Directory.CreateDirectory(LogDir);
    }

    public static void Write(string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var line = $"[{timestamp}] {message}";

        Console.WriteLine(line);

        lock (_lock)
        {
            var filePath = Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(filePath, line + Environment.NewLine);
        }
    }
}
