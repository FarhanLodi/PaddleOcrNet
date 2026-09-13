using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PaddleOcrNet.Diagnostics;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-free tests for the service plumbing fixes: diagnostics version, DI registration, the health
/// check's model list, and argument/disposal checks on <c>DetectRegionsAsync(string)</c>.
/// </summary>
public class ServiceInfrastructureTests
{
    [Fact]
    public void Diagnostics_version_strips_build_metadata()
    {
        Assert.Equal("2.1.0", PaddleOcrDiagnostics.ResolveVersion("2.1.0+9488cdc", null));
        Assert.Equal("2.2.0-beta.1", PaddleOcrDiagnostics.ResolveVersion("2.2.0-beta.1", null));
        Assert.Equal("1.2.3", PaddleOcrDiagnostics.ResolveVersion(null, new Version(1, 2, 3, 4)));
        Assert.Equal("0.0.0", PaddleOcrDiagnostics.ResolveVersion(" ", null));

        Assert.NotEqual("1.0.0", PaddleOcrDiagnostics.ActivitySource.Version);
        Assert.DoesNotContain("+", PaddleOcrDiagnostics.ActivitySource.Version);
    }

    [Fact]
    public void AddPaddleOcrNet_twice_keeps_one_service_and_one_options_instance()
    {
        var services = new ServiceCollection();
        services.AddPaddleOcrNet(o => o.MaxImagePixels = 1);
        services.AddPaddleOcrNet(o => o.MaxImagePixels = 2);

        Assert.Single(services, d => d.ServiceType == typeof(IPaddleOcrService));
        Assert.Single(services, d => d.ServiceType == typeof(PaddleOcrServiceOptions));

        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, provider.GetRequiredService<PaddleOcrServiceOptions>().MaxImagePixels);
    }

    private static string EmptyCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"paddleocr-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Health_check_lists_the_models_the_configured_service_loads()
    {
        var cache = EmptyCache();
        try
        {
            var options = new PaddleOcrServiceOptions { ModelCachePath = cache, DetectionModel = OcrModelVariant.Server };
            var check = new PaddleOcrHealthCheck(options, new[] { "en" });

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Degraded, result.Status);
            var missing = Assert.IsAssignableFrom<IEnumerable<string>>(result.Data["missing"]).ToList();
            Assert.Contains(PaddleModelRegistry.ServerDetector.FileName, missing);
            Assert.DoesNotContain(PaddleModelRegistry.MobileDetector.FileName, missing);
            Assert.Contains(PaddleModelRegistry.TextLineOrientationClassifier.FileName, missing);
            Assert.Contains(PaddleModelRegistry.DocOrientationClassifier.FileName, missing);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task Health_check_is_unhealthy_when_a_local_model_path_is_missing()
    {
        var cache = EmptyCache();
        try
        {
            var options = new PaddleOcrServiceOptions
            {
                ModelCachePath = cache,
                DetectionModelPath = Path.Combine(cache, "nope_det.onnx"),
            };

            var result = await new PaddleOcrHealthCheck(options).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            var missing = Assert.IsAssignableFrom<IEnumerable<string>>(result.Data["missing"]).ToList();
            Assert.Contains(Path.Combine(cache, "nope_det.onnx"), missing);
            Assert.DoesNotContain(PaddleModelRegistry.MobileDetector.FileName, missing);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task DetectRegionsAsync_path_validates_arguments_and_disposal()
    {
        var cache = EmptyCache();
        try
        {
            var service = new PaddleOcrService(new PaddleOcrServiceOptions
            {
                ModelCachePath = cache,
                ExecutionProvider = OcrExecutionProvider.Cpu,
            });

            await Assert.ThrowsAsync<ArgumentException>(() => service.DetectRegionsAsync(" "));
            await Assert.ThrowsAsync<FileNotFoundException>(() => service.DetectRegionsAsync(Path.Combine(cache, "missing.png")));

            await service.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.DetectRegionsAsync(Path.Combine(cache, "missing.png")));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
