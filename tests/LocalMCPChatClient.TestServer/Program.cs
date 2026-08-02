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
    builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
    var app = builder.Build();
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
