using System.Globalization;

namespace PaddleOcrNet.Internal.Recognition;

/// <summary>Geometric windows and grapheme-safe ownership of overlapping CTC emissions.</summary>
internal static class LongLineSegmentation
{
    internal readonly record struct Window(int Start, int Width);

    internal static void Validate(double overlap, int maximum)
    {
        if (!double.IsFinite(overlap) || overlap <= 0 || overlap >= 0.5)
            throw new ArgumentOutOfRangeException(nameof(overlap), "Overlap must be finite and between zero and one half.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
    }

    internal static IReadOnlyList<Window> Plan(int width, int height, int normalizedHeight, double overlap, int maximum)
    {
        Validate(overlap, maximum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(normalizedHeight);
        int windowWidth = (int)Math.Min(width, Math.Max(1, Math.Floor((double)SvtrRecognizer.MaxTensorWidth * height / normalizedHeight)));
        int stride = Math.Max(1, (int)Math.Floor(windowWidth * (1 - overlap)));
        long count = width <= windowWidth ? 1 : 1 + ((long)width - windowWidth + stride - 1) / stride;
        if (count > maximum)
            throw new ArgumentException("Long line exceeds MaxLongLineSegments.", nameof(maximum));
        var windows = new List<Window>((int)count);
        for (int i = 0; i < count; i++)
        {
            int start = (int)Math.Min((long)i * stride, width - windowWidth);
            windows.Add(new Window(start, windowWidth));
        }
        return windows;
    }

    internal static RecognizedText Stitch(IReadOnlyList<Window> windows, IReadOnlyList<RecognizedText> readings,
        int sourceWidth, bool includeCharacters)
    {
        if (windows.Count != readings.Count) throw new ArgumentException("One reading is required per window.");
        var kept = new List<CtcCharacter>();
        for (int i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var reading = readings[i];
            double left = i == 0 ? 0 : ((double)window.Start + windows[i - 1].Start + windows[i - 1].Width) / 2;
            double right = i == windows.Count - 1 ? sourceWidth : ((double)window.Start + window.Width + windows[i + 1].Start) / 2;
            // Use the complete Unicode text element for ownership, even when a combining sequence spans
            // several dictionary tokens. No textual suffix matching: repeated words are separate evidence.
            var characters = reading.Characters ?? throw new ArgumentException("Stitching requires CTC positions.");
            string text = string.Concat(characters.Select(c => c.Token));
            int[] boundaries = StringInfo.ParseCombiningCharacters(text);
            int tokenIndex = 0, tokenStart = 0;
            foreach (int boundary in boundaries)
            {
                int next = Array.BinarySearch(boundaries, boundary) + 1;
                int end = next < boundaries.Length ? boundaries[next] : text.Length;
                while (tokenIndex < characters.Count && tokenStart + characters[tokenIndex].Token.Length <= boundary)
                    tokenStart += characters[tokenIndex++].Token.Length;
                int last = tokenIndex, lastStart = tokenStart;
                while (last + 1 < characters.Count && lastStart + characters[last].Token.Length < end)
                    lastStart += characters[last++].Token.Length;
                if (tokenIndex >= characters.Count) continue;
                var firstCharacter = characters[tokenIndex];
                var lastCharacter = characters[last];
                double startX = window.Start + firstCharacter.FirstStep * reading.StepWidth;
                double endX = window.Start + (lastCharacter.LastStep + 1) * reading.StepWidth;
                double center = (startX + endX) / 2;
                if (center < left || center >= right) continue;
                float probability = 0;
                for (int t = tokenIndex; t <= last; t++) probability += characters[t].Probability;
                probability /= last - tokenIndex + 1;
                int startPixel = Math.Clamp((int)Math.Floor(startX), 0, sourceWidth - 1);
                int lastPixel = Math.Clamp((int)Math.Ceiling(endX) - 1, startPixel, sourceWidth - 1);
                kept.Add(new CtcCharacter(text[boundary..end], probability, startPixel, lastPixel)
                {
                    PixelGeometry = new CtcPixelGeometry(
                        Math.Clamp(startX, 0, sourceWidth), Math.Clamp(endX, 0, sourceWidth),
                        Math.Clamp(window.Start + (firstCharacter.FirstStep + 0.5) * reading.StepWidth, 0, sourceWidth),
                        reading.StepWidth),
                });
            }
        }
        return new RecognizedText(string.Concat(kept.Select(c => c.Token)), kept.Count == 0 ? 0 : kept.Average(c => c.Probability))
        {
            Characters = includeCharacters ? kept : null,
            StepWidth = includeCharacters ? 1 : 0,
        };
    }
}
