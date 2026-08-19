using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

if (args.Contains("--http", StringComparer.Ordinal))
{
    var portIndex = Array.IndexOf(args, "--port");
    var port = portIndex >= 0 && portIndex + 1 < args.Length ? int.Parse(args[portIndex + 1]) : 3001;
    var builder = WebApplication.CreateBuilder([]);
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    var stateful = args.Contains("--stateful", StringComparer.Ordinal);
    builder.Services.AddMcpServer()
        .WithHttpTransport(options => options.Stateless = !stateful)
        .WithToolsFromAssembly()
        .WithResourcesFromAssembly()
        .WithPromptsFromAssembly();
    var app = builder.Build();
    if (args.Contains("--require-stateless-discover", StringComparer.Ordinal))
    {
        var firstPost = 0;
        app.Use(async (context, next) =>
        {
            if (context.Request.Method == "POST" && Interlocked.Exchange(ref firstPost, 1) == 0)
            {
                var protocolVersions = context.Request.Headers["MCP-Protocol-Version"];
                var methods = context.Request.Headers["MCP-Method"];
                if (protocolVersions.Count != 1 || protocolVersions[0] != "2026-07-28" ||
                    methods.Count != 1 || methods[0] != "server/discover")
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        """{"jsonrpc":"2.0","id":null,"error":{"code":-32020,"message":"Expected a 2026-07-28 server/discover request."}}""");
                    return;
                }
            }
            await next();
        });
    }
    if (args.Contains("--reject-duplicate-protocol-version", StringComparer.Ordinal))
    {
        app.Use(async (context, next) =>
        {
            var protocolVersions = context.Request.Headers["MCP-Protocol-Version"];
            if (protocolVersions.Count > 1)
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"jsonrpc":"2.0","id":null,"error":{"code":-32020,"message":"MCP-Protocol-Version must be sent exactly once."}}""");
                return;
            }
            await next();
        });
    }
    if (args.Contains("--require-content-length", StringComparer.Ordinal))
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Method == "POST" && context.Request.ContentLength is null)
            {
                context.Response.StatusCode = 411;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"jsonrpc":"2.0","id":null,"error":{"code":-32000,"message":"Content-Length is required."}}""");
                return;
            }
            await next();
        });
    }
    app.MapMcp("/mcp");
    await app.RunAsync();
}
else
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly().WithResourcesFromAssembly().WithPromptsFromAssembly();
    await builder.Build().RunAsync();
}

[McpServerResourceType]
public static class TestResources
{
    [McpServerResource(UriTemplate = "test://documents/welcome", Name = "welcome", Title = "ようこそ", MimeType = "text/plain"),
     Description("静的なテキストResourceです。")]
    public static string Welcome() => "MCP Resourceからこんにちは。";

    [McpServerResource(UriTemplate = "test://documents/multipart", Name = "multipart", Title = "複数パート", MimeType = "text/plain")]
    public static IEnumerable<ResourceContents> Multipart() =>
    [
        new TextResourceContents { Uri = "test://documents/multipart", MimeType = "text/plain", Text = "part-one" },
        new TextResourceContents { Uri = "test://documents/multipart", MimeType = "text/plain", Text = "part-two" }
    ];

    [McpServerResource(UriTemplate = "test://documents/mixed", Name = "mixed", Title = "テキストとバイナリ", MimeType = "application/octet-stream")]
    public static IEnumerable<ResourceContents> Mixed() =>
    [
        new TextResourceContents { Uri = "test://documents/mixed", MimeType = "text/plain", Text = "visible-text" },
        BlobResourceContents.FromBytes(new byte[] { 1, 2, 3 }, "test://documents/mixed", "application/octet-stream")
    ];

    [McpServerResource(UriTemplate = "test://documents/blob", Name = "blob", Title = "バイナリのみ", MimeType = "application/octet-stream")]
    public static BlobResourceContents Blob() => BlobResourceContents.FromBytes(new byte[] { 4, 5, 6 }, "test://documents/blob", "application/octet-stream");

    [McpServerResource(UriTemplate = "test://documents/pixel.png", Name = "pixel", Title = "1x1 PNG", MimeType = "image/png")]
    public static BlobResourceContents Pixel() => BlobResourceContents.FromBytes(
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
        "test://documents/pixel.png", "image/png");

    [McpServerResource(UriTemplate = "test://templates/{name}", Name = "document_by_name", Title = "Document By Name", MimeType = "text/plain")]
    public static string ByName(string name) => "template:" + name;

    [McpServerResource(UriTemplate = "test://documents/large", Name = "large", Title = "大きなテキスト", MimeType = "text/plain")]
    public static string Large() => new('あ', 100_000);

    [McpServerResource(UriTemplate = "test://documents/slow", Name = "slow", Title = "遅いResource", MimeType = "text/plain")]
    public static async Task<string> Slow(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        return "slow";
    }
}

[McpServerToolType]
public static class TestTools
{
    [McpServerTool(Name = "echo"), Description("テスト文字列をそのまま返します。")]
    public static string Echo([Description("返す文字列")] string text) => "echo:" + text;

    [McpServerTool(Name = "wait"), Description("キャンセルとタイムアウトを検証します。")]
    public static async Task<string> Wait(int milliseconds, CancellationToken cancellationToken)
    {
        await Task.Delay(milliseconds, cancellationToken);
        return "waited";
    }

    [McpServerTool(Name = "image"), Description("テキストとPNG画像を返します。")]
    public static IEnumerable<ContentBlock> Image() =>
    [
        new TextContentBlock { Text = "inline-image" },
        ImageContentBlock.FromBytes(
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
            "image/png")
    ];
}

[McpServerPromptType]
public static class TestPrompts
{
    [McpServerPrompt(Name = "review"), Description("レビューPromptを展開します。")]
    public static string Review([Description("対象名")] string subject) => "Review this: " + subject;
}
