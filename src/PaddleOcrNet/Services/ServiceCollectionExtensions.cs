using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace PaddleOcrNet.Services;

/// <summary>
/// Dependency-injection helpers for registering PaddleOcrNet.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPaddleOcrService"/> (implemented by <see cref="PaddleOcrService"/>) as a
    /// singleton. ONNX sessions are expensive to create and thread-safe to reuse, so a singleton is
    /// the recommended lifetime. The configured <see cref="PaddleOcrServiceOptions"/> is also registered
    /// so add-ons (e.g. the health check) can read it.
    /// </summary>
    public static IServiceCollection AddPaddleOcrNet(
        this IServiceCollection services,
        Action<PaddleOcrServiceOptions>? configure = null)
    {
        var options = new PaddleOcrServiceOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.AddSingleton<IPaddleOcrService>(sp =>
        {
            var logger = sp.GetService<ILogger<PaddleOcrService>>();
            return new PaddleOcrService(options, logger);
        });

        return services;
    }

    /// <summary>
    /// Adds a health check that verifies the model cache is accessible and (optionally) that the
    /// models for the given <paramref name="languages"/> are already present on disk — so a probe can
    /// distinguish "ready to serve" from "will download on first request".
    /// </summary>
    /// <param name="builder">The health-checks builder (from <c>services.AddHealthChecks()</c>).</param>
    /// <param name="languages">Languages whose models should be present for a Healthy result. Empty = cache check only.</param>
    /// <param name="name">Health check name. Defaults to <c>paddleocr</c>.</param>
    /// <param name="failureStatus">Status reported when models are missing. Defaults to <see cref="HealthStatus.Degraded"/>.</param>
    public static IHealthChecksBuilder AddPaddleOcrHealthCheck(
        this IHealthChecksBuilder builder,
        IEnumerable<string>? languages = null,
        string name = "paddleocr",
        HealthStatus failureStatus = HealthStatus.Degraded)
    {
        var langs = languages?.ToArray() ?? Array.Empty<string>();
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetService<PaddleOcrServiceOptions>() ?? new PaddleOcrServiceOptions();
            return new PaddleOcrHealthCheck(options, langs, failureStatus);
        });
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => sp.GetRequiredService<PaddleOcrHealthCheck>(),
            failureStatus,
            tags: new[] { "ocr", "paddleocr" }));
    }
}
