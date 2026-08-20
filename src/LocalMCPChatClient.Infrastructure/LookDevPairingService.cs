using System.Net.Http.Json;
using System.Text.Json;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.Infrastructure;

public sealed class LookDevPairingService(
    IHttpClientFactory httpClientFactory,
    ISettingsStore settingsStore,
    ISecretStore secretStore,
    IMcpConnectionManager mcpManager) : ILookDevPairingService
{
    public async Task<LookDevPairingResult> PairAsync(
        string address,
        string pairingCode,
        string clientName = "LocalMCPChatClient",
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateLoopbackUri(address, "Pairing先");
        if (pairingCode.Length != 8 || !pairingCode.All(char.IsAsciiDigit))
            throw new InvalidOperationException("D3D12側に表示された8桁コードを入力してください。");

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var discoveryResponse = await client.GetAsync(
            new Uri(baseUri, "/.well-known/lookdevpt/v1"), cancellationToken).ConfigureAwait(false);
        discoveryResponse.EnsureSuccessStatusCode();
        using var discovery = JsonDocument.Parse(
            await discoveryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var endpoint = discovery.RootElement.GetProperty("endpoint").GetString()
            ?? throw new InvalidOperationException("LookDev discoveryにendpointがありません。");
        _ = ValidateLoopbackUri(endpoint, "MCP endpoint");
        var contractVersion = discovery.RootElement.GetProperty("contractVersion").GetString()
            ?? throw new InvalidOperationException("LookDev契約版を確認できません。");
        if (!contractVersion.Equals("1.0", StringComparison.Ordinal))
            throw new InvalidOperationException($"未対応のLookDev契約版です: {contractVersion}");
        var applicationVersion = discovery.RootElement.TryGetProperty("version", out var version)
            ? version.GetString() ?? "unknown"
            : "unknown";

        using var pairResponse = await client.PostAsJsonAsync(new Uri(baseUri, "/pair"),
            new { code = pairingCode, clientName }, cancellationToken).ConfigureAwait(false);
        var pairJson = await pairResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!pairResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Pairingに失敗しました ({(int)pairResponse.StatusCode})。D3D12側で新しいコードを発行してください。");
        using var paired = JsonDocument.Parse(pairJson);
        var token = paired.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Pairing tokenが返されませんでした。");

        var serverId = "lookdevpt-" + Guid.NewGuid().ToString("N");
        var secretReference = $"mcp:{serverId}:header:Authorization";
        var persisted = false;
        try
        {
            await secretStore.SetAsync(secretReference, "Bearer " + token, cancellationToken).ConfigureAwait(false);
            var profile = new McpServerProfile
            {
                Id = serverId,
                Name = "D3D12 LookDev (paired)",
                Transport = McpTransportKind.StreamableHttp,
                Url = endpoint,
                Headers = [new SecretValue("Authorization", SecretRef: secretReference)],
                EnableStandaloneGetStream = false,
                BufferHttpRequestBody = true,
                StartupTimeoutSeconds = 10,
                TimeoutSeconds = 120
            };
            var replacedSecretReferences = new List<string>();
            await settingsStore.UpdateAsync(settings =>
            {
                replacedSecretReferences.AddRange(settings.McpServers
                    .Where(item => item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(item => item.Headers)
                    .Select(item => item.SecretRef)
                    .OfType<string>());
                return settings with
                {
                    McpServers = settings.McpServers
                        .Where(item => !item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
                        .Append(profile)
                        .ToList()
                };
            }, cancellationToken).ConfigureAwait(false);
            persisted = true;
            foreach (var oldReference in replacedSecretReferences.Where(value => value != secretReference))
                await secretStore.DeleteAsync(oldReference, cancellationToken).ConfigureAwait(false);

            var connection = await mcpManager.ConnectAsync(profile, cancellationToken).ConfigureAwait(false);
            if (connection.State != McpConnectionState.Connected)
                throw new InvalidOperationException(connection.Error ?? "MCP接続に失敗しました。");
            return new LookDevPairingResult(profile, applicationVersion,
                connection.LookDevContractVersion ?? contractVersion, connection);
        }
        catch
        {
            if (!persisted) await secretStore.DeleteAsync(secretReference, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static Uri ValidateLoopbackUri(string address, string label)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || uri.Host is not ("127.0.0.1" or "localhost"))
            throw new InvalidOperationException($"{label}はhttp://127.0.0.1またはhttp://localhostに限定されます。");
        return uri;
    }
}
