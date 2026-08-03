using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LocalMCPChatClient.Core;
using Microsoft.Win32;

namespace LocalMCPChatClient.Infrastructure;

internal static class InferenceOptimization
{
    public static IReadOnlyList<RuntimeBackend> CreateBackendOrder(
        HardwareCapabilities hardware,
        IEnumerable<RuntimeBackend> benchmarkBackends)
    {
        var result = new List<RuntimeBackend>();
        foreach (var backend in benchmarkBackends) Add(backend);
        if (hardware.HasNvidiaGpu) Add(RuntimeBackend.Cuda);
        if (hardware.HasVulkanGpu) Add(RuntimeBackend.Vulkan);
        Add(RuntimeBackend.Cpu);
        return result;

        void Add(RuntimeBackend backend)
        {
            if (!result.Contains(backend)) result.Add(backend);
        }
    }

    public static async Task<HardwareCapabilities> DetectHardwareAsync(CancellationToken cancellationToken)
    {
        var logicalProcessors = Math.Max(1, Environment.ProcessorCount);
        var physicalCores = GetPhysicalCoreCount(logicalProcessors);
        var cpuName = (OperatingSystem.IsWindows()
            ? Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString",
                null) as string
            : null) ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";

        var gpus = new List<GpuCapability>();
        var nvidiaOutput = await RunAndCaptureAsync(
            "nvidia-smi.exe",
            ["--query-gpu=name,memory.total,driver_version", "--format=csv,noheader,nounits"],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        if (nvidiaOutput.ExitCode == 0)
        {
            foreach (var line in nvidiaOutput.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',', 3, StringSplitOptions.TrimEntries);
                if (parts.Length == 0) continue;
                long memoryMiB = 0;
                _ = parts.Length > 1 && long.TryParse(parts[1], out memoryMiB);
                gpus.Add(new GpuCapability(
                    parts[0], "NVIDIA", memoryMiB * 1024L * 1024L,
                    parts.Length > 2 ? parts[2] : null, SupportsCuda: true, SupportsVulkan: true));
            }
        }

        var hasNvidia = gpus.Any(gpu => gpu.SupportsCuda);
        var hasVulkan = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vulkan-1.dll"));
        if (hasVulkan && gpus.Count == 0)
            gpus.Add(new GpuCapability("Vulkan GPU", "Unknown", 0, null, SupportsCuda: false, SupportsVulkan: true));

        var recommended = hasNvidia ? RuntimeBackend.Cuda : hasVulkan ? RuntimeBackend.Vulkan : RuntimeBackend.Cpu;
        var summary = recommended switch
        {
            RuntimeBackend.Cuda => $"NVIDIA GPUを検出しました。CUDAを推奨します。 ({gpus[0].Name}, {gpus[0].DedicatedMemoryBytes / 1024d / 1024d / 1024d:F1} GiB)",
            RuntimeBackend.Vulkan => "Vulkan対応環境を検出しました。Vulkanを推奨します。",
            _ => "対応GPUを確認できませんでした。CPU推論を使用できます。"
        };
        return new HardwareCapabilities(
            hasNvidia, hasVulkan, summary, cpuName.Trim(), physicalCores, logicalProcessors, gpus, recommended);
    }

    public static string CreateBenchmarkFingerprint(
        ModelProfile model,
        string modelPath,
        string runtimePath,
        RuntimeBackend backend,
        HardwareCapabilities hardware,
        string runtimeBuild)
    {
        var modelInfo = new FileInfo(modelPath);
        var runtimeInfo = new FileInfo(runtimePath);
        var source = string.Join('|',
            model.Id, modelInfo.Length, modelInfo.LastWriteTimeUtc.Ticks,
            backend, runtimeBuild, runtimeInfo.Length, runtimeInfo.LastWriteTimeUtc.Ticks,
            hardware.CpuName, hardware.PhysicalCoreCount, hardware.LogicalProcessorCount,
            hardware.HasNvidiaGpu, hardware.HasVulkanGpu,
            string.Join(';', (hardware.Gpus ?? []).Select(gpu => $"{gpu.Vendor}:{gpu.Name}:{gpu.DedicatedMemoryBytes}:{gpu.DriverVersion}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    public static async Task<(int ExitCode, string Output)> RunAndCaptureAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) return (-1, string.Empty);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch
            {
                if (!process.HasExited) process.Kill(true);
                throw;
            }
            return (process.ExitCode, await stdout.ConfigureAwait(false) + Environment.NewLine + await stderr.ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (-1, exception.Message);
        }
    }

    private static int GetPhysicalCoreCount(int fallback)
    {
        if (!OperatingSystem.IsWindows()) return fallback;
        uint length = 0;
        _ = GetLogicalProcessorInformation(IntPtr.Zero, ref length);
        if (length == 0) return fallback;
        var pointer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformation(pointer, ref length)) return fallback;
            var itemSize = Marshal.SizeOf<SystemLogicalProcessorInformation>();
            var count = 0;
            for (var offset = 0; offset + itemSize <= length; offset += itemSize)
            {
                var item = Marshal.PtrToStructure<SystemLogicalProcessorInformation>(pointer + offset);
                if (item.Relationship == LogicalProcessorRelationship.ProcessorCore) count++;
            }
            return count > 0 ? count : fallback;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnedLength);

    private enum LogicalProcessorRelationship : uint
    {
        ProcessorCore = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemLogicalProcessorInformation
    {
        public UIntPtr ProcessorMask;
        public LogicalProcessorRelationship Relationship;
        public UIntPtr Reserved1;
        public UIntPtr Reserved2;
    }
}
