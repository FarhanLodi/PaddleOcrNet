using PaddleOcrNet.Models;

namespace PaddleOcrNet.Internal;

internal static class AutoRoutingPolicy
{
    internal static IReadOnlyList<RecognizerPack> ResolveActualCandidates(IReadOnlyList<RecognizerPack> candidates,
        OcrModelVariant variant)
        => candidates.Select(pack => variant == OcrModelVariant.Server &&
                (pack.Name == PaddleModelRegistry.MobileRecognizer.Name || pack.Name == PaddleModelRegistry.ChineseTraditional.Name)
                ? PaddleModelRegistry.ServerRecognizer : pack)
            .DistinctBy(pack => pack.Name, StringComparer.Ordinal).ToArray();

    internal static void ValidateCalibration(IReadOnlyList<RecognizerPack> candidates,
        IReadOnlyDictionary<string, RecognitionCalibration> calibration, bool localOverride)
    {
        if (candidates.Count == 0)
            throw new ArgumentException("Calibrated routing requires at least one resolvable candidate.");
        foreach (var pack in candidates)
        {
            if (localOverride && (pack.Name == PaddleModelRegistry.MobileRecognizer.Name || pack.Name == PaddleModelRegistry.ServerRecognizer.Name))
                throw new ArgumentException("Registry-bound calibration cannot be used with a local recognition model override.");
            if (!calibration.TryGetValue(pack.Name, out var curve) || curve is null)
                throw new ArgumentException($"Missing calibration for admitted recognizer '{pack.Name}'.");
            curve.Validate(pack.Model.Sha256, pack.Dictionary.Sha256);
        }
    }
}
