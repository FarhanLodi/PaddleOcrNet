using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Export;
using PaddleOcrNet.Extraction;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

public sealed class DocumentProcessingTests
{
    private static RecognitionOptions SourceRecoveryOptions => new()
    {
        UseDocOrientation = false,
        Preprocessing = new PreprocessingOptions { CorrectNonSquarePixels = false }
    };

    [Theory]
    [InlineData("orientation")]
    [InlineData("unwarp")]
    [InlineData("deskew")]
    [InlineData("denoise")]
    [InlineData("binarize")]
    [InlineData("detect-orientation")]
    [InlineData("pixel-aspect")]
    public async Task Explicit_recovery_page_stages_fail_before_initial_inference(string stage)
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake();
        var recovery = stage switch
        {
            "orientation" => SourceRecoveryOptions with { UseDocOrientation = true },
            "unwarp" => SourceRecoveryOptions with { UseDocUnwarp = true },
            "deskew" => SourceRecoveryOptions with { Preprocessing = SourceRecoveryOptions.Preprocessing with { Deskew = true } },
            "denoise" => SourceRecoveryOptions with { Preprocessing = SourceRecoveryOptions.Preprocessing with { Denoise = true } },
            "binarize" => SourceRecoveryOptions with { Preprocessing = SourceRecoveryOptions.Preprocessing with { Binarize = true } },
            "detect-orientation" => SourceRecoveryOptions with { Preprocessing = SourceRecoveryOptions.Preprocessing with { DetectOrientation = true } },
            _ => SourceRecoveryOptions with { Preprocessing = SourceRecoveryOptions.Preprocessing with { CorrectNonSquarePixels = true } }
        };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.ProcessDocumentAsync(image,
            new[] { OcrLanguage.English }, new() { RecoveryOptions = recovery }));
        Assert.Equal("RecoveryOptions", error.ParamName);
        Assert.Contains("source-frame", error.Message);
        Assert.Equal(0, service.OriginalCalls);
        Assert.Equal(0, service.RecoveryCalls);
    }

    [Fact]
    public async Task Default_original_options_remain_valid_and_automatic_recovery_disables_page_stages()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake();
        await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English }, options: RecognitionOptions.Default);
        Assert.Same(RecognitionOptions.Default, service.OriginalOptions);
        Assert.Equal(0, service.RecoveryCalls);
        await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English });
        Assert.Equal(1, service.RecoveryCalls);
        Assert.False(service.RecoveryOptions!.UseDocOrientation);
        Assert.False(service.RecoveryOptions.Preprocessing.CorrectNonSquarePixels);
        Assert.True(RecognitionOptions.Default.UseDocOrientation);
        Assert.True(RecognitionOptions.Default.Preprocessing.CorrectNonSquarePixels);
    }

    [Fact]
    public async Task Original_preprocessing_applies_only_to_baseline_and_rescan_preserves_character_controls()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake
        {
            Original = Result(), Detected = new[] { new DetectedRegion { BoundingPolygon = Line().BoundingPolygon } }
        };
        var original = new RecognitionOptions { Allowlist = new[] { "8", ":", "4", "7" }, Preprocessing = new() { Binarize = true } };
        await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English }, new()
        {
            EnableDetectionRescan = true, MaximumExtraPasses = 2, DetectionRescanOptions = DetectionOptions.Default
        }, original);
        Assert.Same(original, service.OriginalOptions);
        Assert.True(original.Preprocessing.Binarize);
        Assert.Equal(1, service.RecoveryCalls);
        Assert.Same(original.Allowlist, service.RecoveryOptions!.Allowlist);
        Assert.False(service.RecoveryOptions.Preprocessing.Binarize);
        Assert.False(service.RecoveryOptions.UseDocOrientation);
    }

    [Fact]
    public async Task Small_existing_header_does_not_suppress_larger_missing_text_region()
    {
        using var image = new Image<Rgb24>(40, 20);
        var expanded = Line("header and previously missed body", .95) with
        {
            BoundingPolygon = new[] { new OcrPoint(0, 0), new OcrPoint(40, 0), new OcrPoint(40, 20), new OcrPoint(0, 20) },
            BoundingBox = new OcrBoundingBox(0, 0, 40, 20)
        };
        using var service = new Fake
        {
            Original = Result(Line("header", .99)), Candidate = Result(expanded),
            Detected = new[] { new DetectedRegion { BoundingPolygon = expanded.BoundingPolygon } }
        };
        var result = await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English }, new()
        {
            EnableDetectionRescan = true, MaximumExtraPasses = 2
        });
        Assert.Equal(1, service.RecoveryCalls);
        Assert.Equal(expanded.Text, Assert.Single(result.AdditionalCandidates).Text);
        Assert.Equal("header", result.RawResult.FullText);
        Assert.Contains("new-detector-regions-require-review", result.Quality.ReviewReasons);
    }

    [Theory]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\v")]
    [InlineData("\f")]
    public void Horizontal_cleanup_retains_unicode_line_breaks_as_explicit_lf_view(string separator)
    {
        string text = " A  B " + separator + " C  D ";
        var raw = Result(Line(text));
        var view = raw.ToNormalizedText(new() { CleanWhitespace = true });
        Assert.Equal("A B\nC D", view.Text);
        Assert.Equal(text, view.RawText);
        Assert.Equal(text, Assert.Single(view.Transformations).Before);
        Assert.Equal(text, raw.ToNormalizedText().Text);
    }

    [Fact]
    public void Image_diagnostics_are_bounded_and_report_contrast_without_modifying_pixels()
    {
        using var image = new Image<Rgb24>(256, 256);
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < 256; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < 256; x++) row[x] = x < 128 ? new Rgb24(0, 0, 0) : new Rgb24(255, 255, 255);
            }
        });
        var quality = image.AssessImageQuality();
        Assert.InRange(quality.SampleCount, 1, 4096);
        Assert.InRange(quality.LuminanceContrast, .499, .501);
        Assert.InRange(quality.IlluminationVariation, .999, 1.001);
        Assert.Equal(quality, image.AssessImageQuality());
    }

    [Fact]
    public async Task Actual_dewarped_provenance_blocks_recovery_even_without_explicit_options()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake { Original = Result(Line()) with { Coordinates = new() { SourceMappingAvailable = false, Frame = OcrCoordinateFrame.DewarpedPixels } } };
        var result = await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English }, new() { EnableDetectionRescan = true });
        Assert.Equal(0, service.RecoveryCalls);
        Assert.Equal(0, service.DetectionCalls);
        Assert.Contains("unwarped-coordinates-require-inverse-mapping", result.Quality.ReviewReasons);
    }

    [Fact]
    public async Task Limited_auto_candidates_are_disclosed()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake();
        var result = await service.ProcessDocumentAsync(image, new[] { OcrLanguage.Auto }, options: new() { AutoRouting = new() { MaxCandidatePacks = 1 } });
        Assert.Contains("auto-routing-candidates-limited", result.Quality.ReviewReasons);
    }

    [Fact]
    public async Task Detector_rescan_preserves_new_text_separately_and_respects_pass_limit()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake
        {
            Original = Result(),
            Detected = new[] { new DetectedRegion { BoundingPolygon = Line().BoundingPolygon } }
        };
        var result = await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English }, new()
        {
            EnableDetectionRescan = true, Profile = DocumentProcessingProfile.Accurate
        });
        Assert.Equal(1, service.DetectionCalls);
        Assert.Equal(1, service.RecoveryCalls);
        Assert.Equal(2, result.Quality.ExtraPasses);
        Assert.Empty(result.RawResult.Lines);
        Assert.Single(result.AdditionalCandidates);
        Assert.Empty(result.UnreadRegions);
        Assert.Contains("new-detector-regions-require-review", result.Quality.ReviewReasons);
    }

    [Fact]
    public async Task Detection_exhausting_budget_retains_unread_regions()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake
        {
            Original = Result(), Detected = new[] { new DetectedRegion { BoundingPolygon = Line().BoundingPolygon } }
        };
        var result = await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English }, new() { EnableDetectionRescan = true });
        Assert.True(result.Quality.BudgetExhausted);
        Assert.Single(result.UnreadRegions);
        Assert.Empty(result.AdditionalCandidates);
        Assert.Equal(0, service.RecoveryCalls);
    }

    [Fact]
    public async Task Existing_source_regions_are_not_duplicated_by_rescan()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake
        {
            Original = Result(Line(confidence: .99)), Detected = new[] { new DetectedRegion { BoundingPolygon = Line().BoundingPolygon } }
        };
        var result = await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English }, new() { EnableDetectionRescan = true, MaximumExtraPasses = 2 });
        Assert.Equal(1, service.DetectionCalls);
        Assert.Equal(0, service.RecoveryCalls);
        Assert.Empty(result.AdditionalCandidates);
    }

    private static OcrLine Line(string text = "8:47", double confidence = .4) => new()
    {
        Text = text, Confidence = confidence,
        BoundingPolygon = new[] { new OcrPoint(1, 1), new OcrPoint(30, 1), new OcrPoint(30, 10), new OcrPoint(1, 10) },
        BoundingBox = new OcrBoundingBox(1, 1, 30, 10)
    };

    private static OcrResult Result(params OcrLine[] lines) => new()
    {
        FullText = string.Join("\n", lines.Select(l => l.Text)), Lines = lines, Languages = new[] { "en" }
    };

    [Fact]
    public async Task Higher_confidence_damaged_candidate_is_preserved_without_replacement()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake { Original = Result(Line()), Candidate = Result(Line("8147", .99)) };
        var result = await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English });
        Assert.Same(service.Original, result.RawResult);
        Assert.Equal("8:47", result.RawResult.FullText);
        Assert.Equal("8147", Assert.Single(Assert.Single(result.Regions).Alternatives).Text);
        Assert.Contains("alternative-disagreement", result.Quality.ReviewReasons);
        Assert.False(result.Quality.IsConfidenceCalibrated);
        Assert.Equal(800, result.Quality.ExtraPixels);
    }

    [Theory]
    [InlineData(DocumentProcessingProfile.Fast, 0)]
    [InlineData(DocumentProcessingProfile.Balanced, 1)]
    [InlineData(DocumentProcessingProfile.Accurate, 2)]
    public async Task Profiles_bound_extra_calls(DocumentProcessingProfile profile, int expected)
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake { Original = Result(Line(), Line(), Line()) };
        var result = await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English }, new() { Profile = profile });
        Assert.Equal(expected, service.RecoveryCalls);
        Assert.Equal(expected, result.Quality.ExtraPasses);
        Assert.Equal(expected > 0, result.Quality.BudgetExhausted);
    }

    [Fact]
    public async Task Pixel_budget_prevents_even_first_additional_call()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake();
        var result = await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English }, new() { MaximumExtraPixels = 799 });
        Assert.True(result.Quality.BudgetExhausted);
        Assert.Equal(0, service.RecoveryCalls);
    }

    [Fact]
    public async Task Explicit_options_are_not_silently_modified()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake();
        var options = new RecognitionOptions { Allowlist = new[] { "8", ":", "4", "7" }, Region = OcrRegion.Pixels(0, 0, 40, 20) };
        var languages = new[] { OcrLanguage.English };
        var result = await service.ProcessDocumentAsync(image, languages, options: options);
        Assert.Same(options, service.OriginalOptions);
        Assert.Same(languages, service.OriginalLanguages);
        Assert.Equal(0, service.RecoveryCalls);
        Assert.Contains("explicit-options-preserved", result.Quality.ReviewReasons);
    }

    [Fact]
    public async Task Explicit_recovery_settings_and_languages_are_forwarded()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake();
        var recovery = SourceRecoveryOptions with { UseLegacyResize = true, Allowlist = new[] { "8", ":", "4", "7" } };
        var languages = new[] { OcrLanguage.English };
        await service.ProcessDocumentAsync(image, languages, new() { RecoveryOptions = recovery }, RecognitionOptions.Default);
        Assert.Same(recovery, service.RecoveryOptions);
        Assert.Same(languages, service.RecoveryLanguages);
    }

    [Fact]
    public async Task Timeout_during_recovery_returns_original_and_budget_review()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake { WaitForCancellation = true };
        var result = await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English }, new() { MaximumElapsed = TimeSpan.FromMilliseconds(100) });
        Assert.True(result.Quality.BudgetExhausted);
        Assert.Same(service.Original, result.RawResult);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_during_recovery()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake { WaitForCancellation = true };
        using var cancel = new CancellationTokenSource();
        var task = service.ProcessDocumentAsync(image, new[] { OcrLanguage.English }, cancellationToken: cancel.Token);
        await service.RecoveryStarted.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Blank_pages_report_unverified_coverage_without_inventing_regions()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake { Original = Result() };
        var result = await service.ProcessDocumentAsync(image, new[] { OcrLanguage.English });
        Assert.True(result.Quality.RequiresReview);
        Assert.Contains("no-recognized-text-coverage-unverified", result.Quality.ReviewReasons);
        Assert.Equal(0, service.RecoveryCalls);
    }

    [Fact]
    public void Normalized_view_defaults_to_exact_raw_text_and_escapes_source_markup()
    {
        var raw = Result(Line("<script>  cafe\u0301\r\n A-12 & 8:47"));
        var view = raw.ToNormalizedText();
        Assert.Equal(raw.FullText, view.Text);
        Assert.Empty(view.Transformations);
        Assert.Contains("&lt;script&gt;", raw.ToNormalizedHtml());
        Assert.DoesNotContain("<script>", raw.ToNormalizedHtml());
    }

    [Fact]
    public void Requested_normalization_records_reversible_steps_and_preserves_identifiers()
    {
        var raw = Result(Line("HEADER\r\n cafe\u0301  A-12 8:47\r\nhy-\r\nphen"));
        var view = raw.ToNormalizedText(new()
        {
            SuppressLineIndices = new[] { 0 }, CanonicalUnicode = true, CleanWhitespace = true, JoinHyphenatedLines = true
        });
        Assert.Equal("café A-12 8:47\nhyphen", view.Text);
        Assert.Equal(raw.FullText, view.RawText);
        Assert.Equal(4, view.Transformations.Count);
        Assert.Equal(view.RawText, view.Transformations[0].Before);
        for (int i = 1; i < view.Transformations.Count; i++) Assert.Equal(view.Transformations[i - 1].After, view.Transformations[i].Before);
        Assert.Equal(view.Text, view.Transformations[^1].After);
        Assert.Equal("HEADER\r\n cafe\u0301  A-12 8:47\r\nhy-\r\nphen", raw.FullText);
    }

    [Fact]
    public async Task Invalid_budget_is_rejected_before_inference()
    {
        using var image = new Image<Rgb24>(40, 20);
        using var service = new Fake();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ProcessDocumentAsync(image, new[] { OcrLanguage.English }, new() { MaximumExtraPasses = -1 }));
        Assert.Equal(0, service.OriginalCalls);
    }

    private sealed class Fake : IPaddleOcrService
    {
        public OcrResult Original { get; init; } = Result(Line());
        public OcrResult Candidate { get; init; } = Result(Line());
        public bool WaitForCancellation { get; init; }
        public IReadOnlyList<DetectedRegion> Detected { get; init; } = Array.Empty<DetectedRegion>();
        public int DetectionCalls { get; private set; }
        public int OriginalCalls { get; private set; }
        public int RecoveryCalls { get; private set; }
        public RecognitionOptions? OriginalOptions { get; private set; }
        public RecognitionOptions? RecoveryOptions { get; private set; }
        public IReadOnlyList<OcrLanguage>? OriginalLanguages { get; private set; }
        public IReadOnlyList<OcrLanguage>? RecoveryLanguages { get; private set; }
        public TaskCompletionSource RecoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<DetectedRegion>> DetectRegionsAsync(Image<Rgb24> image, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        {
            DetectionCalls++;
            return Task.FromResult(Detected);
        }
        public Task<OcrResult> ExtractTextFromImage(Image<Rgb24> image, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        {
            OriginalCalls++;
            OriginalOptions = options;
            OriginalLanguages = languages;
            return Task.FromResult(Original);
        }
        public async Task<OcrResult> RecognizeRegionsAsync(Image<Rgb24> image, IEnumerable<IReadOnlyList<OcrPoint>> regions, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        {
            RecoveryCalls++;
            RecoveryOptions = options;
            RecoveryLanguages = languages;
            RecoveryStarted.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return Candidate;
        }
        public Task<OcrResult> ExtractTextFromImage(string imagePath, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OcrResult> ExtractTextFromImage(Stream imageStream, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OcrResult> ExtractTextFromImage(byte[] imageBytes, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OcrResult> ExtractTextFromImage(ReadOnlyMemory<byte> imageBytes, IReadOnlyList<OcrLanguage> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
