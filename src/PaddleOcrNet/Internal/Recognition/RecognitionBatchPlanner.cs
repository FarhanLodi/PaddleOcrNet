namespace PaddleOcrNet.Internal.Recognition;

/// <summary>Plans contiguous batches over sorted aspect ratios without allocating tensors.</summary>
internal static class RecognitionBatchPlanner
{
    internal static IReadOnlyList<(int Start, int End)> Plan(
        IReadOnlyList<double> sortedRatios, int batchSize, int height, long maxPixels, bool legacy = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPixels);
        var batches = new List<(int, int)>();
        int start = 0;
        while (start < sortedRatios.Count)
        {
            int end = start;
            while (end < sortedRatios.Count && end - start < batchSize)
            {
                long pixels = (long)(end - start + 1) * height *
                    SvtrRecognizer.ComputeBatchTensorWidth(height, sortedRatios[end], legacy);
                if (maxPixels > 0 && pixels > maxPixels)
                {
                    if (end == start)
                        throw new ArgumentException("MaxBatchPixels cannot accommodate one crop at its normalized tensor width.", nameof(maxPixels));
                    break;
                }
                end++;
            }
            batches.Add((start, end));
            start = end;
        }
        return batches;
    }
}
