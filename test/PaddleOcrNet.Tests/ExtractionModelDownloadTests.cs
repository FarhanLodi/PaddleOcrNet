using PaddleOcrNet.Extraction;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Network-free tests for <see cref="PaddleOcrModels"/>: file-set resolution, and pre-download against a
/// pre-seeded temporary cache in offline mode.
/// </summary>
public class ExtractionModelDownloadTests
{
    private sealed class CollectingProgress : IProgress<ModelDownloadProgress>
    {
        public List<ModelDownloadProgress> Reports { get; } = new();

        public void Report(ModelDownloadProgress value) => Reports.Add(value);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "paddleocrnet-extraction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Ocr_set_for_english_lists_detector_pack_and_classifiers()
    {
        var files = PaddleOcrModels.GetModelFileNames(new[] { OcrLanguage.English });

        Assert.Equal(
            new[]
            {
                "PP-OCRv5_mobile_det.onnx",
                "en_PP-OCRv5_mobile_rec_infer.onnx",
                "ppocrv5_en_dict.txt",
                "PP-LCNet_x1_0_textline_ori.onnx",
                "PP-LCNet_x1_0_doc_ori.onnx",
            },
            files);
    }

    [Fact]
    public void Languages_sharing_a_pack_are_deduplicated()
    {
        var files = PaddleOcrModels.GetModelFileNames(new[] { OcrLanguage.French, OcrLanguage.German, OcrLanguage.Latin });
        Assert.Single(files, f => f == "latin_PP-OCRv5_mobile_rec.onnx");
        Assert.Equal(files.Count, files.Distinct().Count());
    }

    [Fact]
    public void Auto_expands_to_the_auto_detect_candidates_and_none_means_default_pack()
    {
        var auto = PaddleOcrModels.GetModelFileNames(new[] { OcrLanguage.Auto });
        Assert.Contains("PP-OCRv5_mobile_rec.onnx", auto);
        Assert.Contains("arabic_PP-OCRv5_mobile_rec.onnx", auto);
        Assert.Contains("ta_PP-OCRv5_mobile_rec.onnx", auto);

        var none = PaddleOcrModels.GetModelFileNames(Array.Empty<OcrLanguage>());
        Assert.Contains("PP-OCRv5_mobile_rec.onnx", none);
    }

    [Fact]
    public void Structure_set_includes_layout_table_formula_and_seal()
    {
        var files = PaddleOcrModels.GetModelFileNames(new[] { OcrLanguage.ChineseSimplified }, PaddleModelSet.Structure);

        Assert.Contains("PP-OCRv5_mobile_rec.onnx", files);
        Assert.Contains("PP-DocLayoutV3.onnx", files);
        Assert.Contains("PP-DocLayoutV3_labels.txt", files);
        Assert.Contains("SLANet_plus.onnx", files);
        Assert.Contains("latexocr_tokenizer.json", files);
        Assert.Contains("PP-OCRv4_server_seal_det.onnx", files);
        Assert.DoesNotContain("UVDoc.onnx", files);
        Assert.Equal(files.Count, files.Distinct().Count());
    }

    [Fact]
    public void All_set_never_lists_unhosted_assets()
    {
        var files = PaddleOcrModels.GetModelFileNames(new[] { OcrLanguage.Auto }, PaddleModelSet.All);

        Assert.Contains("UVDoc.onnx", files);
        Assert.Contains("SLANeXt_wired.onnx", files);
        Assert.Contains("PP-DocLayout-M.onnx", files);
        Assert.Contains("PP-OCRv5_server_rec.onnx", files);
        Assert.DoesNotContain("PP-DocLayout_plus-L.onnx", files);
        Assert.DoesNotContain("table_structure_dict.txt", files);
        Assert.DoesNotContain("RT-DETR-L_wired_table_cell_det.onnx", files);
    }

    [Fact]
    public void Service_options_select_server_variants_and_skip_local_models()
    {
        var server = new PaddleOcrServiceOptions { DetectionModel = OcrModelVariant.Server, RecognitionModel = OcrModelVariant.Server };
        var serverFiles = PaddleOcrModels.GetModelFileNames(new[] { OcrLanguage.ChineseSimplified, OcrLanguage.Korean }, serviceOptions: server);
        Assert.Contains("PP-OCRv5_server_det.onnx", serverFiles);
        Assert.Contains("PP-OCRv5_server_rec.onnx", serverFiles);
        Assert.Contains("korean_PP-OCRv5_mobile_rec.onnx", serverFiles);
        Assert.DoesNotContain("PP-OCRv5_mobile_det.onnx", serverFiles);
        Assert.DoesNotContain("PP-OCRv5_mobile_rec.onnx", serverFiles);

        var local = new PaddleOcrServiceOptions { DetectionModelPath = "det.onnx", RecognitionModelPath = "rec.onnx" };
        var localFiles = PaddleOcrModels.GetModelFileNames(new[] { OcrLanguage.ChineseSimplified }, serviceOptions: local);
        Assert.DoesNotContain("PP-OCRv5_mobile_det.onnx", localFiles);
        Assert.DoesNotContain("PP-OCRv5_mobile_rec.onnx", localFiles);
        Assert.Contains("ppocrv5_dict.txt", localFiles);
    }

    [Fact]
    public void GetCacheDirectory_honors_the_service_cache_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "some-cache");
        Assert.Equal(Path.GetFullPath(dir), PaddleOcrModels.GetCacheDirectory(new PaddleOcrServiceOptions { ModelCachePath = dir }));
        Assert.False(string.IsNullOrWhiteSpace(PaddleOcrModels.GetCacheDirectory()));
    }

    [Fact]
    public async Task Pre_seeded_cache_reports_every_file_as_cached()
    {
        var dir = NewTempDir();
        try
        {
            var languages = new[] { OcrLanguage.English };
            var expected = PaddleOcrModels.GetModelFileNames(languages);
            foreach (var file in expected)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, file), new byte[] { 1, 2, 3 });
            }

            var options = new PaddleOcrServiceOptions { ModelCachePath = dir, Download = { Offline = true } };
            var progress = new CollectingProgress();
            var report = await PaddleOcrModels.DownloadAsync(options, languages, progress: progress);

            Assert.Equal(Path.GetFullPath(dir), report.CacheDirectory);
            Assert.Equal(expected, report.Files.Select(f => f.FileName));
            Assert.All(report.Files, f =>
            {
                Assert.Equal(ModelFileStatus.Cached, f.Status);
                Assert.Equal(3, f.SizeBytes);
                Assert.Equal(Path.Combine(Path.GetFullPath(dir), f.FileName), f.Path);
                Assert.Null(f.Error);
            });
            Assert.Equal(expected.Count, report.CachedCount);
            Assert.Equal(0, report.DownloadedCount);
            Assert.Equal(3L * expected.Count, report.TotalBytes);
            Assert.True(report.IsComplete);
            report.EnsureSuccess();
            Assert.Equal(expected, progress.Reports.Select(p => p.FileName));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Offline_missing_files_are_reported_as_failed()
    {
        var dir = NewTempDir();
        try
        {
            var options = new PaddleOcrServiceOptions { ModelCachePath = dir, Download = { Offline = true } };
            var report = await PaddleOcrModels.DownloadAsync(options, new[] { OcrLanguage.English }, PaddleModelSet.Seal);

            var file = Assert.Single(report.Files);
            Assert.Equal("PP-OCRv4_server_seal_det.onnx", file.FileName);
            Assert.Equal(ModelFileStatus.Failed, file.Status);
            Assert.IsType<OfflineModelMissingException>(file.Error);
            Assert.False(report.IsComplete);
            var ex = Assert.Throws<ModelDownloadException>(report.EnsureSuccess);
            Assert.Contains("PP-OCRv4_server_seal_det.onnx", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PaddleOcrModels.DownloadAsync(new[] { OcrLanguage.English }, cancellationToken: cts.Token));
    }
}
