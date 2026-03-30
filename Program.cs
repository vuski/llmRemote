using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LlmRemote.Services;
using LlmRemote.Handlers;

var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
if (!File.Exists(configPath))
    configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.json");

var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(configPath))!;
var port = config.TryGetValue("port", out var p) ? p.GetInt32() : 443;

// Load certificate: use configured cert if available, otherwise generate self-signed
X509Certificate2 cert;
if (config.TryGetValue("certPath", out var cp) && File.Exists(cp.GetString()))
{
    var certPassword = config.TryGetValue("certPassword", out var pw) ? pw.GetString() ?? "" : "";
    cert = new X509Certificate2(cp.GetString()!, certPassword);
    Console.WriteLine($"Using certificate: {cp.GetString()}");
}
else
{
    cert = GenerateSelfSignedCert();
    Console.WriteLine("Using self-signed certificate");
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(port, listenOptions =>
    {
        listenOptions.UseHttps(cert);
    });
});

builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<WindowService>();
builder.Services.AddSingleton<InputService>();

var app = builder.Build();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/login", AuthHandler.Login);
app.MapGet("/api/windows", WindowListHandler.GetWindows);
app.Map("/ws/stream", StreamHandler.HandleWebSocket);

Console.WriteLine($"Server started on port {port} (HTTPS)");
Console.WriteLine($"Open https://localhost:{port} in your browser");

app.Run();

static X509Certificate2 GenerateSelfSignedCert()
{
    var certPath = Path.Combine(AppContext.BaseDirectory, "llmremote.pfx");
    if (!File.Exists(certPath))
        certPath = Path.Combine(Directory.GetCurrentDirectory(), "llmremote.pfx");

    if (File.Exists(certPath))
    {
        try
        {
            var existing = new X509Certificate2(certPath, "llmremote");
            if (existing.NotAfter > DateTime.Now.AddDays(7))
                return existing;
        }
        catch { }
    }

    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=LlmRemote", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

    var sanBuilder = new SubjectAlternativeNameBuilder();
    sanBuilder.AddDnsName("localhost");
    sanBuilder.AddIpAddress(System.Net.IPAddress.Any);
    sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
    request.CertificateExtensions.Add(sanBuilder.Build());

    var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(365));

    var pfxBytes = cert.Export(X509ContentType.Pfx, "llmremote");
    File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), "llmremote.pfx"), pfxBytes);

    return new X509Certificate2(pfxBytes, "llmremote");
}
