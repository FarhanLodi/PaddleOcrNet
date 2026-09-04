using Microsoft.ML.OnnxRuntime;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for
/// <see cref="ExecutionProviderResolver.BuildSessionOptionsWithStatus"/>: session building must never
/// throw, and the reported <see cref="SessionBuildResult.ActiveProvider"/> must be truthful — the
/// requested accelerator only when its native provider actually attached, CPU (with a populated
/// <see cref="SessionBuildResult.ProviderFailureHint"/>) when the append failed. These tests are
/// written to pass both on CPU-only boxes and on machines where the accelerator is genuinely present.
/// </summary>
public class SessionBuildStatusTests
{
    [Fact]
    public void Cpu_request_yields_cpu_with_no_hint()
    {
        var result = ExecutionProviderResolver.BuildSessionOptionsWithStatus(
            OcrExecutionProvider.Cpu, new PaddleEngineOptions(), logger: null);

        Assert.NotNull(result.Options);
        Assert.Equal(OcrExecutionProvider.Cpu, result.ActiveProvider);
        Assert.Null(result.ProviderFailureHint);
        result.Options.Dispose();
    }

    [Fact]
    public void Cuda_request_does_not_throw_and_reports_a_truthful_provider()
    {
        // On a box without the CUDA native runtime the append fails: the options still build, the
        // active provider degrades to CPU, and a fix-it hint is produced. When CUDA IS present the
        // append succeeds and no hint is emitted. Either way it must not throw and must not lie.
        var result = ExecutionProviderResolver.BuildSessionOptionsWithStatus(
            OcrExecutionProvider.Cuda, new PaddleEngineOptions(), logger: null);

        Assert.NotNull(result.Options);
        if (result.ActiveProvider == OcrExecutionProvider.Cuda)
        {
            Assert.Null(result.ProviderFailureHint);
        }
        else
        {
            Assert.Equal(OcrExecutionProvider.Cpu, result.ActiveProvider);
            Assert.False(string.IsNullOrWhiteSpace(result.ProviderFailureHint));
        }
        result.Options.Dispose();
    }

    [Fact]
    public void Thread_counts_from_engine_options_are_honored()
    {
        var options = new PaddleEngineOptions { IntraOpNumThreads = 3, InterOpNumThreads = 2 };

        var result = ExecutionProviderResolver.BuildSessionOptionsWithStatus(
            OcrExecutionProvider.Cpu, options, logger: null);

        Assert.Equal(3, result.Options.IntraOpNumThreads);
        Assert.Equal(2, result.Options.InterOpNumThreads);
        result.Options.Dispose();
    }
}

/// <summary>
/// Tests for the displaced-runtime diagnosis (<see cref="ExecutionProviderResolver.CudaProviderDisplacedHint"/>):
/// the state where <c>PaddleOcrNet.Gpu</c> is installed but NuGet handed the shared
/// <c>runtimes/&lt;rid&gt;/native/onnxruntime</c> slot to the CPU package, so the CUDA provider library is
/// deployed next to a core runtime that cannot load it (GitHub issue #6). The file probe is exercised
/// directly against both deployment layouts; the composed hint is asserted only for consistency with the
/// loaded runtime, so the test is meaningful on a GPU box and on a CPU-only one alike.
/// </summary>
public class CudaProviderDisplacedHintTests
{
    private static string CudaProviderLibraryName => OperatingSystem.IsWindows()
        ? "onnxruntime_providers_cuda.dll"
        : "libonnxruntime_providers_cuda.so";

    private static string NativeRid => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    [Fact]
    public void An_output_folder_without_the_cuda_provider_is_not_a_displaced_runtime()
    {
        using var dir = new TempDirectory();

        Assert.False(ExecutionProviderResolver.CudaProviderLibraryDeployed(dir.Path));
    }

    [SkippableFact]
    public void The_cuda_provider_is_found_in_the_portable_runtimes_layout()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), "CUDA ships for Windows and Linux only.");
        using var dir = new TempDirectory();
        var native = Path.Combine(dir.Path, "runtimes", NativeRid, "native");
        Directory.CreateDirectory(native);
        File.WriteAllBytes(Path.Combine(native, CudaProviderLibraryName), Array.Empty<byte>());

        Assert.True(ExecutionProviderResolver.CudaProviderLibraryDeployed(dir.Path));
    }

    [SkippableFact]
    public void The_cuda_provider_is_found_in_the_flat_rid_specific_layout()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), "CUDA ships for Windows and Linux only.");
        using var dir = new TempDirectory();
        File.WriteAllBytes(Path.Combine(dir.Path, CudaProviderLibraryName), Array.Empty<byte>());

        Assert.True(ExecutionProviderResolver.CudaProviderLibraryDeployed(dir.Path));
    }

    [Fact]
    public void No_hint_is_produced_when_the_cuda_provider_was_never_deployed()
    {
        using var dir = new TempDirectory();

        Assert.Null(ExecutionProviderResolver.CudaProviderDisplacedHint(dir.Path));
    }

    [SkippableFact]
    public void A_deployed_provider_the_loaded_runtime_denies_having_is_diagnosed()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), "CUDA ships for Windows and Linux only.");
        using var dir = new TempDirectory();
        File.WriteAllBytes(Path.Combine(dir.Path, CudaProviderLibraryName), Array.Empty<byte>());

        var hint = ExecutionProviderResolver.CudaProviderDisplacedHint(dir.Path);

        // A runtime that genuinely carries CUDA is not in the displaced state however the files look, so
        // the hint must be absent there and present (and explanatory) everywhere else.
        if (OrtEnv.Instance().GetAvailableProviders().Contains("CUDAExecutionProvider"))
        {
            Assert.Null(hint);
        }
        else
        {
            Assert.NotNull(hint);
            Assert.Contains("Microsoft.ML.OnnxRuntime.Gpu", hint);
            Assert.Contains("ExcludeAssets", hint);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "paddleocrnet-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* a locked temp file must not fail the test */ }
        }
    }
}
