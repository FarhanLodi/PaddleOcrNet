# Opt-in recognition bounds and long lines

`RecognitionOptions.MaxBatchPixels` caps each normalized recognition input tensor's `N × H × W`, including padding. Three float channels require 12 bytes per pixel, excluding outputs and runtime working memory. Zero retains the original count-only batches. Aspect-ratio sorting and caller result order are retained. An individual crop that cannot fit throws before inference rather than exceeding the budget. Multiply the cap by concurrent batches when planning memory. Batch shape can change model readings.

```csharp
var options = new RecognitionOptions
{
    MaxBatchPixels = 48L * 3200 * 2,
    ReuseTensorBuffers = true,
    SegmentLongLines = true,
    LongLineOverlapFraction = 0.2,
    MaxLongLineSegments = 64,
    ReturnWordBoxes = true
};
```

Segmentation recognizes crops wider than 3200 pixels at the model's normalized height in overlapping full-height windows. All windows remain at or below that width. The segment limit rejects excessive expansion before segment allocation; segment images are disposed even on failure. One original line's windows are materialized at a time.

Recognition observes request cancellation before window allocation, between batches, during crop packing, before and after native inference, and between decoded rows. Cancellation releases window images and inference outputs. An already executing native ONNX run is not forcibly interrupted.

Stitching assigns each complete Unicode grapheme to a window by its CTC position and the midpoint of the overlap. It retains repeated words, punctuation, emoji and combining marks without suffix deduplication or normalization. Emissions are assembled in their original left-to-right image order; the existing recognizer-pack RTL transformation then runs once on the completed line. Character geometry is translated to original crop pixels so existing word-box mapping, padding and rotations still apply. Confidence is the mean of retained grapheme scores, and remains uncalibrated.

Tensor pixel limits, segmentation and tensor-buffer reuse all default off. Windows can disagree near seams, CTC alignment is approximate, and geometric ownership does not guarantee perfect deduplication under disagreement. This is a usable opt-in recovery path, not evidence of an accuracy improvement. Validate long-line CER, critical punctuation, combining-script and RTL results on representative held-out documents before rollout. When any crop is segmented, unsegmented crops in that call are recognized individually; changed batch widths can change their readings too. No GPU, trained model or universal accuracy claim is implied.

## Call-local tensor reuse

`ReuseTensorBuffers` retains bounded float workspaces within a recognition call and reuses them across batches. Each workspace is sized for the largest planned batch; active tensor values, including right padding, are cleared before packing. Workspaces are released after concurrent batches finish, including failure and cancellation paths, and do not persist across requests. The default continues allocating per batch.

`MaxBatchPixels` still limits each active input tensor, not total process memory. Reuse can retain a largest-batch workspace while processing smaller batches, so peak retained memory can exceed the current tensor's active size. Multiply workspace needs by concurrent batches and requests; ONNX outputs, native runtime allocations and resident models are additional. This is managed float-storage reuse, not GPU I/O binding or persistent device buffers. Measure allocations, peak memory and latency on representative workloads before recommending it as a performance improvement.

Use the separate [recognizer residency setting](recognizer-residency.md) to limit cached recognizer session counts. That setting does not impose a byte limit on tensor workspaces or the entire process.
