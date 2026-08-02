using System.Text.Json;
using System.Text.Json.Serialization;
using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.Infrastructure;

public sealed class JsonSettingsStore(IAppPaths paths) : ISettingsStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var next = update(current);
            await SaveCoreAsync(next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.SettingsPath))
        {
            var defaults = CreateDefaults();
            await SaveCoreAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        try
        {
            await using var stream = File.OpenRead(paths.SettingsPath);
            var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return Normalize(loaded ?? CreateDefaults());
        }
        catch (JsonException)
        {
            var backup = paths.SettingsPath + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(paths.SettingsPath, backup, true);
            var defaults = CreateDefaults();
            await SaveCoreAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        settings = RemoveResolvedSecrets(settings);
        var temporaryPath = paths.SettingsPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, paths.SettingsPath, true);
    }

    private static AppSettings RemoveResolvedSecrets(AppSettings settings) => settings with
    {
        McpServers = settings.McpServers.Select(server => server with
        {
            Environment = server.Environment.Select(RemoveResolvedSecret).ToList(),
            Headers = server.Headers.Select(RemoveResolvedSecret).ToList()
        }).ToList()
    };

    private static SecretValue RemoveResolvedSecret(SecretValue value)
        => string.IsNullOrWhiteSpace(value.SecretRef) ? value : value with { Value = null };

    private static AppSettings Normalize(AppSettings settings)
    {
        var defaults = CreateDefaults();
        var models = settings.Models.ToDictionary(model => model.Id, StringComparer.Ordinal);
        foreach (var builtIn in defaults.Models)
        {
            if (!models.TryGetValue(builtIn.Id, out var current))
            {
                models[builtIn.Id] = builtIn;
                continue;
            }
            models[builtIn.Id] = current with
            {
                Repository = string.IsNullOrWhiteSpace(current.Repository) ? builtIn.Repository : current.Repository,
                Revision = string.IsNullOrWhiteSpace(current.Revision) || current.Revision == "main" ? builtIn.Revision : current.Revision,
                FileName = string.IsNullOrWhiteSpace(current.FileName) ? builtIn.FileName : current.FileName,
                Sha256 = builtIn.Sha256,
                Size = builtIn.Size,
                LicenseUrl = string.IsNullOrWhiteSpace(current.LicenseUrl) ? builtIn.LicenseUrl : current.LicenseUrl
            };
        }
        return settings with
        {
            SchemaVersion = 1,
            SelectedModelId = string.IsNullOrWhiteSpace(settings.SelectedModelId) ? defaults.SelectedModelId : settings.SelectedModelId,
            ContextSize = Math.Clamp(settings.ContextSize, 512, 131_072),
            MaxOutputTokens = Math.Clamp(settings.MaxOutputTokens, 64, 32_768),
            Temperature = Math.Clamp(settings.Temperature, 0, 2),
            Models = models.Values.ToList(),
            McpServers = settings.McpServers ?? [],
            ApprovalRules = settings.ApprovalRules ?? []
        };
    }

    public static AppSettings CreateDefaults() => new()
    {
        Models =
        [
            new ModelProfile
            {
                Id = "gemma-4-e2b-it-q4",
                DisplayName = "Gemma 4 E2B IT (Q4_0)",
                Repository = "google/gemma-4-E2B-it-qat-q4_0-gguf",
                Revision = "675cff42a74c774d6cb76f76d8eacb49b48c9b93",
                FileName = "gemma-4-E2B_q4_0-it.gguf",
                Sha256 = "fa401b55b07ee70a54c6dae3903c783a6e65064312529ea57175cb5f8dec6634",
                Size = 3_349_516_256,
                LicenseUrl = "https://huggingface.co/google/gemma-4-E2B-it-qat-q4_0-gguf"
            },
            new ModelProfile
            {
                Id = "gemma-4-e4b-it-q4",
                DisplayName = "Gemma 4 E4B IT (Q4_0)",
                Repository = "google/gemma-4-E4B-it-qat-q4_0-gguf",
                Revision = "4b4a2c1d584be7264f87aac328a1bc739ce81b6c",
                FileName = "gemma-4-E4B_q4_0-it.gguf",
                Sha256 = "676c35070db6dbe52f93e9c864ee0fba4eddea94b9c875d9cb10daff453fbaee",
                Size = 5_154_941_280,
                LicenseUrl = "https://huggingface.co/google/gemma-4-E4B-it-qat-q4_0-gguf"
            }
        ],
        SelectedModelId = "gemma-4-e2b-it-q4"
    };
}
