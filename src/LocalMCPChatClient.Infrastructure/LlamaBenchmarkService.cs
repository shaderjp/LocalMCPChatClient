using System.Text.Json;
using LocalMCPChatClient.Core;
using Microsoft.Extensions.Logging;

namespace LocalMCPChatClient.Infrastructure;

public sealed class LlamaBenchmarkService(
    IAppPaths paths,
    ISettingsStore settingsStore,
    IInferenceRuntimeManager runtimeManager,
    ILogger<LlamaBenchmarkService> logger) : IInferenceBenchmarkService
{
    public async Task<IReadOnlyList<InferenceBenchmarkResult>> RunAsync(
        ModelProfile model,
        IProgress<BenchmarkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modelPath = Path.GetFullPath(!string.IsNullOrWhiteSpace(model.LocalPath)
            ? model.LocalPath
            : Path.Combine(paths.ModelsDirectory, model.FileName));
        if (!File.Exists(modelPath)) throw new FileNotFoundException("速度診断に使用するモデルが見つかりません。", modelPath);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        progress?.Report(new BenchmarkProgress("推論ランタイムを停止しています", Percent: 2));
        await runtimeManager.StopAsync(token).ConfigureAwait(false);
        var hardware = await runtimeManager.DetectHardwareAsync(token).ConfigureAwait(false);
        var candidates = FindCandidates(hardware);
        if (candidates.Count == 0)
            throw new InvalidOperationException("診断可能なllama-benchがインストールされていません。");

        var results = new List<InferenceBenchmarkResult>();
        for (var index = 0; index < candidates.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            progress?.Report(new BenchmarkProgress(
                $"{candidate.Backend}を測定しています",
                candidate.Backend,
                5 + index * 85d / candidates.Count));
            var run = await InferenceOptimization.RunAndCaptureAsync(
                candidate.BenchPath,
                ["-m", modelPath, "-p", "512", "-n", "128", "-r", "2", "-ngl", candidate.Backend == RuntimeBackend.Cpu ? "0" : "999", "-fa", "auto", "-o", "json"],
                TimeSpan.FromSeconds(60),
                token).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                logger.LogWarning("llama-bench failed for {Backend}: {Output}", candidate.Backend, Limit(run.Output, 2048));
                continue;
            }

            var parsed = ParseResult(run.Output);
            if (parsed is null) continue;
            var runtimeBuild = await ReadRuntimeBuildAsync(candidate.ServerPath, token).ConfigureAwait(false);
            results.Add(new InferenceBenchmarkResult
            {
                Fingerprint = InferenceOptimization.CreateBenchmarkFingerprint(
                    model, modelPath, candidate.ServerPath, candidate.Backend, hardware, runtimeBuild),
                ModelId = model.Id,
                Backend = candidate.Backend,
                RuntimeBuild = runtimeBuild,
                PromptTokensPerSecond = parsed.Value.PromptRate,
                GeneratedTokensPerSecond = parsed.Value.GenerationRate,
                MeasuredAt = DateTimeOffset.UtcNow
            });
        }

        if (results.Count == 0) throw new InvalidOperationException("速度診断を完了できませんでした。診断ログを確認してください。");
        await settingsStore.UpdateAsync(settings => settings with
        {
            InferenceBenchmarks = settings.InferenceBenchmarks
                .Where(existing => existing.ModelId != model.Id || results.All(result => result.Backend != existing.Backend))
                .Concat(results)
                .ToList()
        }, token).ConfigureAwait(false);
        progress?.Report(new BenchmarkProgress("速度診断が完了しました", Percent: 100));
        return results;
    }

    private List<Candidate> FindCandidates(HardwareCapabilities hardware)
    {
        var candidates = new List<Candidate>();
        if (hardware.HasNvidiaGpu) Add(RuntimeBackend.Cuda);
        if (hardware.HasVulkanGpu) Add(RuntimeBackend.Vulkan);
        if (candidates.Count == 0) Add(RuntimeBackend.Cpu);
        return candidates;

        void Add(RuntimeBackend backend)
        {
            if (!Directory.Exists(paths.RuntimesDirectory)) return;
            var server = Directory.EnumerateFiles(paths.RuntimesDirectory, "llama-server.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Contains(backend.ToString(), StringComparison.OrdinalIgnoreCase));
            if (server is null) return;
            var bench = Path.Combine(Path.GetDirectoryName(server)!, "llama-bench.exe");
            if (File.Exists(bench)) candidates.Add(new Candidate(backend, server, bench));
        }
    }

    internal static (double PromptRate, double GenerationRate)? ParseResult(string output)
    {
        var start = output.IndexOf('[', StringComparison.Ordinal);
        var end = output.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        try
        {
            using var document = JsonDocument.Parse(output[start..(end + 1)]);
            double prompt = 0;
            double generation = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var rate = item.TryGetProperty("avg_ts", out var rateElement) ? rateElement.GetDouble() : 0;
                if (item.TryGetProperty("n_prompt", out var promptElement) && promptElement.GetInt32() > 0) prompt = rate;
                if (item.TryGetProperty("n_gen", out var generationElement) && generationElement.GetInt32() > 0) generation = rate;
            }
            return prompt > 0 && generation > 0 ? (prompt, generation) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadRuntimeBuildAsync(string serverPath, CancellationToken cancellationToken)
    {
        var result = await InferenceOptimization.RunAndCaptureAsync(
            serverPath, ["--version"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
               ?? File.GetLastWriteTimeUtc(serverPath).Ticks.ToString();
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + "…";

    private sealed record Candidate(RuntimeBackend Backend, string ServerPath, string BenchPath);
}
