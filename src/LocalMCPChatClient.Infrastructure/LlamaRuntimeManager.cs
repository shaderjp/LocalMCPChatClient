using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using LocalMCPChatClient.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace LocalMCPChatClient.Infrastructure;

public sealed class LlamaRuntimeManager(
    IAppPaths paths,
    ISettingsStore settingsStore,
    IHttpClientFactory httpClientFactory,
    ILogger<LlamaRuntimeManager> logger) : IInferenceRuntimeManager, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private string? _pidFile;
    private InferenceProfile? _activeProfile;
    private readonly ConcurrentQueue<string> _recentRuntimeLines = new();
    private readonly ConcurrentDictionary<string, string> _runtimeHelp = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _runtimeProbes = new(StringComparer.OrdinalIgnoreCase);
    private Task<HardwareCapabilities>? _hardwareDetection;
    private int _disposed;
    public RuntimeState State { get; private set; } = RuntimeState.Stopped;
    public event EventHandler<RuntimeState>? StateChanged;

    public async Task<RuntimeState> StartAsync(InferenceProfile profile, ModelProfile model, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var modelPath = ResolveModelPath(model);
            if (!File.Exists(modelPath))
                throw new FileNotFoundException("モデルがインストールされていません。初回設定からモデルを取得してください。", modelPath);
            var visionProjectorPath = ResolveVisionProjectorPath(model);
            var visionEnabled = profile.EnableVision && model.Modalities.Contains(InputModality.Image);
            if (visionEnabled && (string.IsNullOrWhiteSpace(visionProjectorPath) || !File.Exists(visionProjectorPath)))
                throw new FileNotFoundException("画像入力用mmprojがインストールされていません。設定から画像入力コンポーネントを取得するか、画像入力を無効にしてください。", visionProjectorPath);

            if (State.Status == RuntimeStatus.Ready &&
                string.Equals(State.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
                Equals(_activeProfile, profile))
                return State;

            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            await RecoverOwnedProcessCoreAsync(cancellationToken).ConfigureAwait(false);

            var hardware = await DetectHardwareAsync(cancellationToken).ConfigureAwait(false);
            var backends = profile.Mode == InferenceMode.Auto
                ? await ResolveAutoBackendsAsync(profile, model, modelPath, hardware, cancellationToken).ConfigureAwait(false)
                : [ToBackend(profile.Mode)];
            var failures = new List<string>();

            foreach (var backend in backends)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var executable = ResolveRuntimePath(profile.CustomRuntimePath, backend);
                if (executable is null)
                {
                    failures.Add($"{backend}: ランタイム未導入");
                    continue;
                }
                if (!await CanUseRuntimeAsync(executable, backend, cancellationToken).ConfigureAwait(false))
                {
                    failures.Add($"{backend}: 対応デバイスを確認できません");
                    continue;
                }

                try
                {
                    var ready = await StartOneAsync(executable, backend, profile, modelPath, visionProjectorPath, visionEnabled, hardware, cancellationToken).ConfigureAwait(false);
                    if (profile.Mode == InferenceMode.Auto)
                    {
                        try
                        {
                            await settingsStore.UpdateAsync(settings => settings with { LastSuccessfulAutoBackend = backend }, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            logger.LogWarning(exception, "Could not persist the successful automatic backend {Backend}", backend);
                        }
                    }
                    _activeProfile = profile;
                    return ready;
                }
                catch (Exception exception) when (profile.Mode == InferenceMode.Auto && exception is not OperationCanceledException)
                {
                    failures.Add($"{backend}: {exception.Message}");
                    logger.LogWarning(exception, "{Backend} backend failed; trying the next backend", backend);
                    await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (Exception exception)
                {
                    await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                    var backendMessage = $"{backend}バックエンドを起動できませんでした: {exception.Message}";
                    SetState(new RuntimeState(RuntimeStatus.Faulted, backend, ModelPath: modelPath, Error: backendMessage));
                    throw new InvalidOperationException(backendMessage, exception);
                }
            }

            var message = "利用可能な推論バックエンドを起動できませんでした。" + Environment.NewLine + string.Join(Environment.NewLine, failures);
            SetState(new RuntimeState(RuntimeStatus.Faulted, Error: message));
            throw new InvalidOperationException(message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task RecoverOwnedProcessAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RecoverOwnedProcessCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<HardwareCapabilities> DetectHardwareAsync(CancellationToken cancellationToken = default)
    {
        var task = _hardwareDetection ??= InferenceOptimization.DetectHardwareAsync(CancellationToken.None);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RuntimeState> StartOneAsync(
        string executable,
        RuntimeBackend backend,
        InferenceProfile profile,
        string modelPath,
        string? visionProjectorPath,
        bool visionEnabled,
        HardwareCapabilities hardware,
        CancellationToken cancellationToken)
    {
        var port = GetFreeTcpPort();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        SetState(new RuntimeState(RuntimeStatus.Starting, backend, endpoint, modelPath));
        while (_recentRuntimeLines.TryDequeue(out _)) { }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        AddArgument(startInfo, "--model", modelPath);
        if (visionEnabled) AddArgument(startInfo, "--mmproj", visionProjectorPath!);
        AddArgument(startInfo, "--host", "127.0.0.1");
        AddArgument(startInfo, "--port", port.ToString());
        AddArgument(startInfo, "--api-key", token);
        AddArgument(startInfo, "--ctx-size", profile.ContextSize.ToString());
        AddArgument(startInfo, "--n-predict", profile.MaxOutputTokens.ToString());
        var help = await GetRuntimeHelpAsync(executable, cancellationToken).ConfigureAwait(false);
        var physicalCores = Math.Max(1, hardware.PhysicalCoreCount);
        AddArgumentIfSupported(startInfo, help, "--n-gpu-layers", backend == RuntimeBackend.Cpu ? "0" : help.Contains("'all'", StringComparison.OrdinalIgnoreCase) ? "all" : "999");
        AddArgumentIfSupported(startInfo, help, "--threads", physicalCores.ToString());
        AddArgumentIfSupported(startInfo, help, "--threads-batch", physicalCores.ToString());
        AddArgumentIfSupported(startInfo, help, "--batch-size", "2048");
        AddArgumentIfSupported(startInfo, help, "--ubatch-size", "512");
        AddArgumentIfSupported(startInfo, help, "--flash-attn", "auto");
        AddArgumentIfSupported(startInfo, help, "--cache-type-k", "f16");
        AddArgumentIfSupported(startInfo, help, "--cache-type-v", "f16");
        AddArgumentIfSupported(startInfo, help, "--parallel", "1");
        if (visionEnabled) AddArgumentIfSupported(startInfo, help, "--image-max-tokens", profile.ImageTokenBudget.ToString());
        if (backend != RuntimeBackend.Cpu)
        {
            AddArgumentIfSupported(startInfo, help, "--fit", "on");
            AddArgumentIfSupported(startInfo, help, "--fit-target", "1024");
        }
        AddFlagIfSupported(startInfo, help, "--cache-prompt");
        AddFlagIfSupported(startInfo, help, "--metrics");
        AddFlagIfSupported(startInfo, help, "--jinja");
        AddFlagIfSupported(startInfo, help, "--no-webui");

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) => LogRuntimeLine(args.Data, false);
        _process.ErrorDataReceived += (_, args) => LogRuntimeLine(args.Data, true);
        if (!_process.Start()) throw new InvalidOperationException("llama-serverを起動できませんでした。");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        paths.EnsureCreated();
        _pidFile = Path.Combine(paths.DataDirectory, "llama-server.pid");
        await File.WriteAllTextAsync(_pidFile, $"{_process.Id}|{Path.GetFullPath(executable)}", cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var client = httpClientFactory.CreateClient();
        client.BaseAddress = endpoint;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (_process.HasExited)
                    throw CreateRuntimeExitException(_process.ExitCode);
                try
                {
                    using var response = await client.GetAsync("health", timeout.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        var ready = new RuntimeState(RuntimeStatus.Ready, backend, endpoint, modelPath, AuthenticationToken: token, VisionEnabled: visionEnabled);
                        SetState(ready);
                        return ready;
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(400, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("llama-serverのモデル読み込みが90秒以内に完了しませんでした。");
        }
        throw new TimeoutException("llama-serverのモデル読み込みが90秒以内に完了しませんでした。");
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        process.Kill(true);
                        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            finally { process.Dispose(); }
        }
        if (_pidFile is not null && File.Exists(_pidFile)) File.Delete(_pidFile);
        _pidFile = null;
        _activeProfile = null;
        SetState(RuntimeState.Stopped);
    }

    private async Task RecoverOwnedProcessCoreAsync(CancellationToken cancellationToken)
    {
        var pidFile = Path.Combine(paths.DataDirectory, "llama-server.pid");
        if (!File.Exists(pidFile)) return;
        try
        {
            var parts = (await File.ReadAllTextAsync(pidFile, cancellationToken).ConfigureAwait(false)).Split('|', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var pid)) return;
            using var process = Process.GetProcessById(pid);
            var processPath = process.MainModule?.FileName;
            if (processPath is not null && Path.GetFullPath(processPath).Equals(Path.GetFullPath(parts[1]), StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        finally { File.Delete(pidFile); }
    }

    private string ResolveModelPath(ModelProfile model) => Path.GetFullPath(
        !string.IsNullOrWhiteSpace(model.LocalPath) ? model.LocalPath : Path.Combine(paths.ModelsDirectory, model.FileName));

    private string? ResolveVisionProjectorPath(ModelProfile model)
    {
        if (!string.IsNullOrWhiteSpace(model.VisionProjectorPath)) return Path.GetFullPath(model.VisionProjectorPath);
        return model.VisionProjector is null ? null : Path.GetFullPath(Path.Combine(paths.ModelsDirectory, model.VisionProjector.FileName));
    }

    private string? ResolveRuntimePath(string? customPath, RuntimeBackend backend)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
            return File.Exists(customPath) ? Path.GetFullPath(customPath) : null;
        if (!Directory.Exists(paths.RuntimesDirectory)) return null;
        return Directory.EnumerateFiles(paths.RuntimesDirectory, "llama-server.exe", SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Contains(backend.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<RuntimeBackend>> ResolveAutoBackendsAsync(
        InferenceProfile profile,
        ModelProfile model,
        string modelPath,
        HardwareCapabilities hardware,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(profile.CustomRuntimePath) && File.Exists(profile.CustomRuntimePath))
        {
            var output = await InferenceOptimization.RunAndCaptureAsync(
                profile.CustomRuntimePath, ["--list-devices"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (output.Output.Contains("CUDA", StringComparison.OrdinalIgnoreCase)) return [RuntimeBackend.Cuda];
            if (output.Output.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)) return [RuntimeBackend.Vulkan];
            return [RuntimeBackend.Cpu];
        }

        var preferred = new List<RuntimeBackend>();
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var result in settings.InferenceBenchmarks
                     .Where(result => result.ModelId == model.Id)
                     .OrderByDescending(result => result.PromptTokensPerSecond)
                     .ThenByDescending(result => result.GeneratedTokensPerSecond))
        {
            var executable = ResolveRuntimePath(null, result.Backend);
            if (executable is null) continue;
            var build = await GetRuntimeBuildAsync(executable, cancellationToken).ConfigureAwait(false);
            var fingerprint = InferenceOptimization.CreateBenchmarkFingerprint(model, modelPath, executable, result.Backend, hardware, build);
            if (string.Equals(result.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                AddDistinct(preferred, result.Backend);
        }

        return InferenceOptimization.CreateBackendOrder(hardware, preferred);
    }

    private async Task<bool> CanUseRuntimeAsync(string executable, RuntimeBackend backend, CancellationToken cancellationToken)
    {
        if (backend == RuntimeBackend.Cpu) return File.Exists(executable);
        var key = $"{Path.GetFullPath(executable)}|{backend}";
        if (_runtimeProbes.TryGetValue(key, out var cached)) return cached;
        var result = await InferenceOptimization.RunAndCaptureAsync(
            executable, ["--list-devices"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        var available = result.ExitCode == 0 && result.Output.Contains(backend.ToString(), StringComparison.OrdinalIgnoreCase);
        _runtimeProbes[key] = available;
        return available;
    }

    private async Task<string> GetRuntimeHelpAsync(string executable, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(executable);
        if (_runtimeHelp.TryGetValue(path, out var cached)) return cached;
        var result = await InferenceOptimization.RunAndCaptureAsync(
            path, ["--help"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        _runtimeHelp[path] = result.Output;
        return result.Output;
    }

    private static async Task<string> GetRuntimeBuildAsync(string executable, CancellationToken cancellationToken)
    {
        var result = await InferenceOptimization.RunAndCaptureAsync(
            executable, ["--version"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        var firstLine = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? File.GetLastWriteTimeUtc(executable).Ticks.ToString() : firstLine.Trim();
    }

    private static void AddDistinct(List<RuntimeBackend> backends, RuntimeBackend backend)
    {
        if (!backends.Contains(backend)) backends.Add(backend);
    }

    private static RuntimeBackend ToBackend(InferenceMode mode) => mode switch
    {
        InferenceMode.Cpu => RuntimeBackend.Cpu,
        InferenceMode.Cuda => RuntimeBackend.Cuda,
        InferenceMode.Vulkan => RuntimeBackend.Vulkan,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static void AddArgument(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(value);
    }

    private static void AddArgumentIfSupported(ProcessStartInfo info, string help, string name, string value)
    {
        if (help.Contains(name, StringComparison.Ordinal)) AddArgument(info, name, value);
    }

    private static void AddFlagIfSupported(ProcessStartInfo info, string help, string name)
    {
        if (help.Contains(name, StringComparison.Ordinal)) info.ArgumentList.Add(name);
    }

    private void LogRuntimeLine(string? line, bool standardError)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _recentRuntimeLines.Enqueue(line);
        while (_recentRuntimeLines.Count > 80) _recentRuntimeLines.TryDequeue(out _);
        if (standardError) logger.LogDebug("llama-server: {Line}", line);
        else logger.LogTrace("llama-server: {Line}", line);
    }

    private InvalidOperationException CreateRuntimeExitException(int exitCode)
    {
        var output = string.Join('\n', _recentRuntimeLines);
        if (output.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || output.Contains("cudaMalloc", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("GPUメモリが不足してllama-serverを起動できませんでした。コンテキスト長を下げるかCPU推論を選択してください。");
        if (output.Contains("invalid gguf", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("failed to load model", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("model load failed", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("モデルを読み込めませんでした。GGUFが破損している可能性があるため、再取得または再インポートしてください。");
        return new InvalidOperationException($"llama-serverが終了しました (exit code {exitCode})。モデルと選択したバックエンドを確認してください。");
    }

    private void SetState(RuntimeState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopAsync().GetAwaiter().GetResult();
        _gate.Dispose();
    }
}
