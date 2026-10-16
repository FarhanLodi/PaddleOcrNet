using PaddleOcrNet.Models;

namespace PaddleOcrNet.Office;

/// <summary>Office extraction policy and admission bounds; limits fail explicitly without partial results.</summary>
public sealed record OfficeDocumentOptions
{
    /// <summary>Run sequential OCR on supported embedded raster images in service extensions. Default true.</summary>
    public bool RecognizeEmbeddedImages { get; init; } = true;
    /// <summary>Languages passed to embedded-image OCR.</summary>
    public IReadOnlyList<OcrLanguage> Languages { get; init; } = new[] { OcrLanguage.Auto };
    /// <summary>Recognition settings for each embedded image.</summary>
    public RecognitionOptions? Recognition { get; init; }
    /// <summary>Maximum compressed input bytes buffered. Default 64 MiB.</summary>
    public long MaxInputBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>Maximum sum of declared uncompressed sizes and cumulative bytes read from parts, including repeated reads. Default 256 MiB.</summary>
    public long MaxExpandedBytes { get; init; } = 256L * 1024 * 1024;
    /// <summary>Maximum uncompressed size of any package part. Default 32 MiB.</summary>
    public int MaxPartBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>Maximum ZIP entry count. Default 10,000.</summary>
    public int MaxEntries { get; init; } = 10_000;
    /// <summary>Maximum extracted stories/sheets/slides. Default 1,000.</summary>
    public int MaxSections { get; init; } = 1_000;
    /// <summary>Maximum extracted blocks and native table cells combined. Default 100,000.</summary>
    public int MaxBlocks { get; init; } = 100_000;
    /// <summary>Maximum image occurrences admitted for OCR, including reused images. Default 100.</summary>
    public int MaxImages { get; init; } = 100;
    /// <summary>Maximum characters parsed per XML part. Default 16 Mi characters; DTDs are prohibited.</summary>
    public long MaxXmlCharacters { get; init; } = 16L * 1024 * 1024;
    /// <summary>Maximum admitted native and OCR text characters, counting reused source text on each occurrence. Default 32 Mi characters.</summary>
    public long MaxTextCharacters { get; init; } = 32L * 1024 * 1024;

    internal void Validate()
    {
        if (MaxInputBytes is <= 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(MaxInputBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxExpandedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPartBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBlocks);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxImages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxXmlCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTextCharacters);
        ArgumentNullException.ThrowIfNull(Languages);
        if (RecognizeEmbeddedImages && Languages.Count == 0) throw new ArgumentException("OCR languages cannot be empty.", nameof(Languages));
    }
}
