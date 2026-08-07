using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

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
        .WithToolsFromAssembly();
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
    builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
    await builder.Build().RunAsync();
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
}
