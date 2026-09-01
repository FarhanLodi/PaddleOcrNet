using System;
using PaddleOcrNet.Internal;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the CUDA-mismatch diagnostic. When the CUDA provider
/// DLL loads but its CUDA dependencies do not, ONNX Runtime names the first missing library — e.g.
/// <c>cublasLt64_13.dll</c> — whose suffix is the CUDA <b>major</b> version that runtime was built against.
/// That is easy to misread as a broken CUDA install when the machine actually has a working CUDA of the
/// other major version, so the resolver turns it into an explicit "you have the wrong toolkit" warning.
/// </summary>
public class CudaToolkitHintTests
{
    private static string? Hint(string message)
        => ExecutionProviderResolver.CudaToolkitHint(new InvalidOperationException(message));

    // The message ONNX Runtime 1.27 (CUDA 13) produces on a machine with only the CUDA 12 toolkit.
    private const string Cuda13OnCuda12 =
        "[ONNXRuntimeError] : 1 : FAIL : Error loading " +
        @"""C:\app\runtimes\win-x64\native\onnxruntime_providers_cuda.dll"" which depends on " +
        @"""cublasLt64_13.dll"" which is missing. (Error 126)";

    [Fact]
    public void A_missing_cuda_13_library_names_the_toolkit_that_is_wanted()
    {
        string? hint = Hint(Cuda13OnCuda12);

        Assert.NotNull(hint);
        Assert.Contains("CUDA 13.x", hint);
        Assert.Contains("cublasLt64_13.dll", hint);
        Assert.Contains("1.27", hint);
    }

    [Fact]
    public void A_missing_cuda_12_library_points_at_the_1_21_to_1_26_range()
    {
        string? hint = Hint(@"Error loading ""onnxruntime_providers_cuda.dll"" which depends on ""cudart64_12.dll"" which is missing.");

        Assert.NotNull(hint);
        Assert.Contains("CUDA 12.x", hint);
        Assert.Contains("1.21 through 1.26", hint);
    }

    [Fact]
    public void The_linux_library_spelling_is_recognized_too()
    {
        string? hint = Hint("libonnxruntime_providers_cuda.so: cannot open shared object file: libcublasLt.so.13");

        Assert.NotNull(hint);
        Assert.Contains("CUDA 13.x", hint);
    }

    [Fact]
    public void An_unrelated_failure_produces_no_cuda_hint()
    {
        Assert.Null(Hint("No CUDA-capable device is detected."));
        Assert.Null(Hint("Failed to load library onnxruntime_providers_shared.dll"));
    }
}
