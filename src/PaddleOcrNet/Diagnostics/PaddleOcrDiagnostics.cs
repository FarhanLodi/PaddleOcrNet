using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace PaddleOcrNet.Diagnostics;

/// <summary>
/// OpenTelemetry-friendly diagnostics for PaddleOcrNet. Subscribe with the public
/// <see cref="MeterName"/> / <see cref="ActivitySourceName"/>:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithMetrics(m => m.AddMeter(PaddleOcrDiagnostics.MeterName))
///     .WithTracing(t => t.AddSource(PaddleOcrDiagnostics.ActivitySourceName));
/// </code>
/// Instruments have near-zero cost when nobody is listening, so they are always on.
/// </summary>
public static class PaddleOcrDiagnostics
{
    /// <summary>
    /// Meter name to register with your metrics pipeline.
    /// </summary>
    public const string MeterName = "PaddleOcrNet";

    /// <summary>
    /// ActivitySource name to register with your tracing pipeline.
    /// </summary>
    public const string ActivitySourceName = "PaddleOcrNet";

    /// <summary>
    /// The library version reported on the activity source and meter: the assembly's informational version
    /// without the <c>+commit</c> source-link suffix.
    /// </summary>
    internal static readonly string Version = ResolveVersion(
        typeof(PaddleOcrDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(PaddleOcrDiagnostics).Assembly.GetName().Version);

    /// <summary>
    /// Activity source for per-operation OCR spans.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    internal static readonly Meter Meter = new(MeterName, Version);

    /// <summary>
    /// Strips build metadata (<c>+sha</c>) from an informational version, falling back to the assembly
    /// version when no informational version is present.
    /// </summary>
    internal static string ResolveVersion(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            int plus = informationalVersion.IndexOf('+');
            var trimmed = (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return assemblyVersion?.ToString(3) ?? "0.0.0";
    }

    internal static readonly Counter<long> Operations =
        Meter.CreateCounter<long>("paddleocr.operations", unit: "{operation}", description: "OCR operations performed.");

    internal static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("paddleocr.duration", unit: "ms", description: "OCR operation wall-clock duration.");

    internal static readonly Counter<long> LinesRecognized =
        Meter.CreateCounter<long>("paddleocr.lines", unit: "{line}", description: "Text lines returned by recognition.");

    internal static readonly Counter<long> ModelLoads =
        Meter.CreateCounter<long>("paddleocr.model.loads", unit: "{model}", description: "ONNX model sessions created.");

    internal static readonly Counter<long> ModelDownloadBytes =
        Meter.CreateCounter<long>("paddleocr.model.download_bytes", unit: "By", description: "Bytes downloaded for model assets.");
}
