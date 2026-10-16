using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Internal.Recognition;

/// <summary>Borrowed specialist facade: keeps no native session alive between calls.</summary>
internal sealed class LeasedTextRecognizer(Func<CancellationToken, Task<LeasedResourceCache<ITextRecognizer>.Lease>> acquire) : ITextRecognizer
{
    public (string Text, float Confidence) Recognize(Image<Rgb24> crop)
    {
        using var lease = acquire(CancellationToken.None).GetAwaiter().GetResult();
        return lease.Value.Recognize(crop);
    }
    public IReadOnlyList<(string Text, float Confidence)> Recognize(IReadOnlyList<Image<Rgb24>> crops)
    {
        using var lease = acquire(CancellationToken.None).GetAwaiter().GetResult();
        return lease.Value.Recognize(crops);
    }
    public IReadOnlyList<(string Text, float Confidence)> Recognize(IReadOnlyList<Image<Rgb24>> crops, RecognitionOptions options)
    {
        using var lease = acquire(CancellationToken.None).GetAwaiter().GetResult();
        return lease.Value.Recognize(crops, options);
    }
    public IReadOnlyList<RecognizedText> RecognizeDetailed(IReadOnlyList<Image<Rgb24>> crops, RecognitionOptions options,
        int maxConcurrentBatches, bool includeCharacters, CancellationToken cancellationToken = default)
    {
        using var lease = acquire(cancellationToken).GetAwaiter().GetResult();
        return lease.Value.RecognizeDetailed(crops, options, maxConcurrentBatches, includeCharacters, cancellationToken);
    }
    public void Dispose() { } // The engine owns the cache and native sessions.
}
