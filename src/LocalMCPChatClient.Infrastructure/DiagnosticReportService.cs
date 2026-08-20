using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.Infrastructure;

public sealed class DiagnosticReportService(
    IAppPaths paths,
    ISettingsStore settingsStore,
    IInferenceRuntimeManager runtimeManager,
    IMcpConnectionManager mcpManager) : IDiagnosticReportService
{
    public async Task<string> ExportAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var hardware = await runtimeManager.DetectHardwareAsync(cancellationToken).ConfigureAwait(false);
        var connections = await mcpManager.GetConnectionsAsync(cancellationToken).ConfigureAwait(false);
        var runtime = runtimeManager.State;
        var lookDev = new List<object>();
        foreach (var connection in connections.Where(item => !string.IsNullOrWhiteSpace(item.LookDevContractVersion)))
        {
            string? applicationVersion = null;
            string? rendererApi = null;
            string? adapter = null;
            string? dxrTier = null;
            try
            {
                var integration = await mcpManager.ReadResourceAsync(new McpResourceReference(
                    connection.ServerId, connection.DisplayName, "lookdevpt://integration", "integration", "application/json"),
                    cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(integration.Content);
                if (document.RootElement.TryGetProperty("application", out var application) &&
                    application.TryGetProperty("version", out var version))
                    applicationVersion = version.GetString();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                applicationVersion = "unavailable: " + Mask(exception.Message);
            }
            try
            {
                var stats = await mcpManager.ReadResourceAsync(new McpResourceReference(
                    connection.ServerId, connection.DisplayName, "lookdevpt://stats", "stats", "application/json"),
                    cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(stats.Content);
                rendererApi = document.RootElement.TryGetProperty("api", out var api) ? api.GetString() : null;
                adapter = document.RootElement.TryGetProperty("adapter", out var adapterValue) ? adapterValue.GetString() : null;
                dxrTier = document.RootElement.TryGetProperty("dxrTier", out var tier) ? tier.GetString() : null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                dxrTier = "unavailable: " + Mask(exception.Message);
            }
            lookDev.Add(new
            {
                applicationVersion,
                contractVersion = connection.LookDevContractVersion,
                rendererApi,
                adapter,
                dxrTier,
                state = connection.State.ToString(),
                connection.SupportsResources,
                connection.SupportsPrompts,
                connection.SupportsResourceTemplates,
                connection.SupportsSubscriptions,
                error = Mask(connection.Error)
            });
        }

        var selectedModel = settings.Models.FirstOrDefault(item => item.Id == settings.SelectedModelId);
        var report = new
        {
            schemaVersion = 1,
            createdAtUtc = DateTimeOffset.UtcNow,
            application = new
            {
                name = "LocalMCPChatClient",
                version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(DiagnosticReportService).Assembly.GetName().Version?.ToString()
            },
            operatingSystem = new
            {
                description = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                is64Bit = Environment.Is64BitOperatingSystem
            },
            hardware = new
            {
                cpu = hardware.CpuName,
                physicalCores = hardware.PhysicalCoreCount,
                logicalProcessors = hardware.LogicalProcessorCount,
                recommendedBackend = hardware.RecommendedBackend.ToString(),
                gpus = (hardware.Gpus ?? []).Select(gpu => new
                {
                    gpu.Name,
                    gpu.Vendor,
                    gpu.DedicatedMemoryBytes,
                    gpu.DriverVersion,
                    gpu.SupportsCuda,
                    gpu.SupportsVulkan
                })
            },
            inference = new
            {
                status = runtime.Status.ToString(),
                backend = runtime.Backend?.ToString(),
                runtime.VisionEnabled,
                error = Mask(runtime.Error),
                selectedModel = selectedModel is null ? null : new
                {
                    selectedModel.Id,
                    selectedModel.DisplayName,
                    modalities = selectedModel.Modalities.Select(value => value.ToString()),
                    modelInstalled = !string.IsNullOrWhiteSpace(selectedModel.LocalPath) && File.Exists(selectedModel.LocalPath),
                    projectorInstalled = !string.IsNullOrWhiteSpace(selectedModel.VisionProjectorPath) && File.Exists(selectedModel.VisionProjectorPath)
                }
            },
            mcp = new
            {
                connectedCount = connections.Count(item => item.State == McpConnectionState.Connected),
                configuredCount = settings.McpServers.Count,
                lookDev
            },
            privacy = new
            {
                includesCredentials = false,
                includesConversationContent = false,
                includesAbsolutePaths = false,
                includesEndpoints = false
            }
        };
        var destination = Path.Combine(paths.LogsDirectory, $"diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(destination,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        return destination;
    }

    private static string? Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var masked = RedactingFileLoggerProvider.MaskSecrets(value);
        masked = Regex.Replace(masked, """(?i)https?://[^\s"'<>]+""", "<redacted-endpoint>", RegexOptions.CultureInvariant);
        return Regex.Replace(masked, """(?i)(?:[a-z]:[\\/]|\\\\)[^\s"'<>|]+""", "<redacted-path>", RegexOptions.CultureInvariant);
    }
}
