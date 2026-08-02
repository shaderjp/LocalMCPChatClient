using LocalMCPChatClient.Core;

namespace LocalMCPChatClient.Infrastructure;

public static class BuiltInArtifacts
{
    public const string LlamaCppVersion = "b10205";

    public static IReadOnlyList<ArtifactDescriptor> RuntimeArtifacts { get; } =
    [
        new ArtifactDescriptor
        {
            Id = "llama-b10205-cpu",
            Kind = ArtifactKind.Runtime,
            DisplayName = "llama.cpp b10205 CPU",
            FileName = "llama-b10205-bin-win-cpu-x64.zip",
            DownloadUri = new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b10205/llama-b10205-bin-win-cpu-x64.zip"),
            Sha256 = "b442f140a513e478e6bda26b0a769cfce18699c1c85f3d2df33a8637dcd5e14f",
            Size = 18_351_085,
            Backend = RuntimeBackend.Cpu,
            InstallationGroup = "cpu-b10205"
        },
        new ArtifactDescriptor
        {
            Id = "llama-b10205-vulkan",
            Kind = ArtifactKind.Runtime,
            DisplayName = "llama.cpp b10205 Vulkan",
            FileName = "llama-b10205-bin-win-vulkan-x64.zip",
            DownloadUri = new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b10205/llama-b10205-bin-win-vulkan-x64.zip"),
            Sha256 = "2df7c1567e87a41f5d55efdd3b70953518e96bc5604b159655320380584800ca",
            Size = 33_648_680,
            Backend = RuntimeBackend.Vulkan,
            InstallationGroup = "vulkan-b10205"
        },
        new ArtifactDescriptor
        {
            Id = "llama-b10205-cuda-runtime",
            Kind = ArtifactKind.Runtime,
            DisplayName = "CUDA 12.4 runtime for llama.cpp b10205",
            FileName = "cudart-llama-bin-win-cuda-12.4-x64.zip",
            DownloadUri = new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b10205/cudart-llama-bin-win-cuda-12.4-x64.zip"),
            Sha256 = "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6",
            Size = 39_144_362,
            Backend = RuntimeBackend.Cuda,
            InstallationGroup = "cuda-b10205",
            IsRuntimeDependency = true
        },
        new ArtifactDescriptor
        {
            Id = "llama-b10205-cuda",
            Kind = ArtifactKind.Runtime,
            DisplayName = "llama.cpp b10205 CUDA 12.4",
            FileName = "llama-b10205-bin-win-cuda-12.4-x64.zip",
            DownloadUri = new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b10205/llama-b10205-bin-win-cuda-12.4-x64.zip"),
            Sha256 = "830d50bedb4dbb21e619ec7883fd7356f9b92c803a99c4d56deef000322ce787",
            Size = 25_098_515,
            Backend = RuntimeBackend.Cuda,
            InstallationGroup = "cuda-b10205"
        }
    ];

    public static ArtifactDescriptor CreateModelArtifact(ModelProfile model, string? destinationDirectory = null) => new()
    {
        Id = model.Id,
        Kind = ArtifactKind.Model,
        DisplayName = model.DisplayName,
        FileName = model.FileName,
        DownloadUri = new Uri($"https://huggingface.co/{model.Repository}/resolve/{model.Revision}/{model.FileName}"),
        Sha256 = model.Sha256,
        Size = model.Size,
        DestinationDirectory = destinationDirectory
    };
}
