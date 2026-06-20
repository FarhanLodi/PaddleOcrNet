using System.Collections.Frozen;

namespace PaddleOcrNet.Internal;

/// <summary>
/// Defines a downloadable model asset (an ONNX network or a character dictionary file).
/// </summary>
/// <param name="FileName">The single-segment file name cached on disk (e.g. <c>PP-OCRv5_mobile_rec.onnx</c>).</param>
/// <param name="Url">The absolute HTTPS URL the asset is fetched from when not cached.</param>
/// <param name="Sha256">
/// Expected upper-case-hex SHA256, or <c>null</c> when no checksum is published yet. A <c>null</c>
/// checksum makes <see cref="ModelDownloadManager"/> take its fail-open path: the asset is only accepted
/// when <see cref="Services.ModelDownloadOptions.AllowUnverifiedModels"/> is set, otherwise the download
/// is rejected. See <see cref="PaddleModelRegistry.Checksums"/> for why these are currently empty.
/// </param>
internal sealed record ModelAsset(string FileName, string Url, string? Sha256);

/// <summary>
/// Describes a recognizer "pack": the recognition ONNX network plus the character
/// <see cref="Dictionary"/> sidecar holding the ordered ppocr key set the network's CTC head emits.
/// PaddleOCR ships one recognizer (and a matching dictionary) per language/script family, so a pack is
/// exactly the (recognition model, dictionary) pair selected for a requested language.
/// </summary>
/// <param name="Name">Stable pack identifier, also the recognizer session cache key (e.g. <c>latin_PP-OCRv5_mobile</c>).</param>
/// <param name="Model">The recognition ONNX model asset.</param>
/// <param name="Dictionary">The ppocr character-dictionary asset (one key per line).</param>
/// <param name="Languages">Language codes this pack serves; the first is its representative code.</param>
internal sealed record RecognizerPack(
    string Name,
    ModelAsset Model,
    ModelAsset Dictionary,
    string[] Languages);

/// <summary>
/// Static catalogue of the PaddleOCR <b>PP-OCRv5</b> model set exported to ONNX, plus the character
/// dictionaries each recognizer needs. It covers:
/// <list type="bullet">
///   <item>text detection — PP-OCRv5 <see cref="MobileDetector">mobile</see> and <see cref="ServerDetector">server</see>;</item>
///   <item>the default text recognition — PP-OCRv5 <see cref="MobileRecognizer">mobile</see> and
///         <see cref="ServerRecognizer">server</see> (Chinese / English / Japanese, on <c>ppocrv5_dict.txt</c>);</item>
///   <item>the <see cref="TextLineOrientationClassifier">text-line orientation</see> (180° flip) classifier;</item>
///   <item>the <see cref="DocOrientationClassifier">document-orientation</see> (0/90/180/270°) classifier;</item>
///   <item>the per-script recognition language packs (latin, cyrillic, arabic, devanagari, korean, japan,
///         thai, greek, telugu, tamil, chinese_cht, eslav), each with its matching ppocr dictionary.</item>
/// </list>
/// <para>
/// <b>SHA256 values are placeholders.</b> The assets are not yet published, so every checksum is absent
/// (see <see cref="Checksums"/>): downloads succeed only under
/// <see cref="Services.ModelDownloadOptions.AllowUnverifiedModels"/>. Regenerate and populate
/// <see cref="Checksums"/> from the exact files once the maintainer uploads them to the model repo; the
/// verification path in <see cref="ModelDownloadManager"/> is already wired and will start enforcing them
/// automatically with no other change.
/// </para>
/// </summary>
internal static class PaddleModelRegistry
{
    /// <summary>
    /// Base URL where the exported PP-OCRv5 ONNX assets and dictionaries are hosted. This is a
    /// <b>placeholder</b> repository path; override it at runtime via
    /// <see cref="Services.ModelDownloadOptions.BaseUrlOverride"/> or the
    /// <c>PADDLEOCRNET_MODEL_BASE_URL</c> environment variable (both honored by
    /// <see cref="ModelDownloadManager"/>) to point at the real model host or a private mirror.
    /// </summary>
    public const string DefaultBaseUrl =
        "https://huggingface.co/PaddleOcrNet/PaddleOcrNet-models/resolve/main";

    // -----------------------------------------------------------------------------------------------
    // Asset file names. These mirror PaddleOCR's PP-OCRv5 export / dict naming so that whoever exports
    // the models and uploads them only has to keep the upstream file names. Detection/recognition
    // networks are exported to ".onnx"; dictionaries are the upstream ppocr/utils/dict/*.txt files.
    // -----------------------------------------------------------------------------------------------

    private const string MobileDetFile = "PP-OCRv5_mobile_det.onnx";
    private const string ServerDetFile = "PP-OCRv5_server_det.onnx";

    private const string MobileRecFile = "PP-OCRv5_mobile_rec.onnx";
    private const string ServerRecFile = "PP-OCRv5_server_rec.onnx";

    private const string TextLineOriFile = "PP-LCNet_x1_0_textline_ori.onnx";
    private const string DocOriFile = "PP-LCNet_x1_0_doc_ori.onnx";

    // ----- Document-structure subsystem assets (Structure/) -----
    // Layout detection (PP-DocLayout). The S/M variants are PicoDet; "plus-L" is RT-DETR. Each ONNX network
    // ships with a label sidecar (.yml) giving its raw-class-id → label-name list, parsed into the
    // class-id → StructureBlockType maps the detectors take.
    private const string DocLayoutSFile = "PP-DocLayout-S.onnx";
    private const string DocLayoutMFile = "PP-DocLayout-M.onnx";
    private const string DocLayoutPlusLFile = "PP-DocLayout_plus-L.onnx";
    private const string DocLayoutSLabelsFile = "PP-DocLayout-S.yml";
    private const string DocLayoutMLabelsFile = "PP-DocLayout-M.yml";
    private const string DocLayoutPlusLLabelsFile = "PP-DocLayout_plus-L.yml";

    // Document pre-processing: PP-LCNet whole-page orientation (0/90/180/270°) + UVDoc unwarp (dewarp).
    private const string DocImageOriFile = "PP-LCNet_x1_0_doc_ori.onnx"; // 4-class document image orientation
    private const string UVDocUnwarpFile = "UVDoc.onnx";

    // Table: a lightweight cls (is-this-table-wired-or-wireless) gate, two SLANet-family structure models
    // (wired vs wireless), the SLANeXt structure models, the shared structure-token dictionary, and the
    // RT-DETR cell detectors (wired vs wireless).
    private const string TableClsFile = "PP-LCNet_x1_0_table_cls.onnx";
    private const string SlanetPlusWiredFile = "SLANet_plus_wired.onnx";
    private const string SlanetPlusWirelessFile = "SLANet_plus_wireless.onnx";
    private const string SlaNeXtWiredFile = "SLANeXt_wired.onnx";
    private const string SlaNeXtWirelessFile = "SLANeXt_wireless.onnx";
    private const string TableStructureDictFile = "table_structure_dict.txt";
    private const string RtDetrWiredCellFile = "RT-DETR-L_wired_table_cell_det.onnx";
    private const string RtDetrWirelessCellFile = "RT-DETR-L_wireless_table_cell_det.onnx";

    // Seal: PP-OCRv4 curved-text (seal) detector. Seal recognition reuses the shared text recognizer.
    private const string SealDetFile = "PP-OCRv4_server_seal_det.onnx";

    // Formula: LaTeX-OCR (RapidLaTeXOCR) image-to-LaTeX, an image-resizer + ViT encoder + transformer
    // decoder + tokenizer.json sidecar.
    // IMPORTANT: formula recognition uses LaTeX-OCR (RapidLaTeXOCR) ONNX, NOT PP-FormulaNet. PP-FormulaNet
    // has no ONNX export path, so it cannot run on the ONNX-Runtime-only stack PaddleOcrNet is built on.
    private const string LatexImageResizerFile = "latexocr_image_resizer.onnx";
    private const string LatexEncoderFile = "latexocr_encoder.onnx";
    private const string LatexDecoderFile = "latexocr_decoder.onnx";
    private const string LatexTokenizerFile = "latexocr_tokenizer.json";

    /// <summary>
    /// SHA256 checksums (upper-case hex) of every published asset, verified after download by
    /// <see cref="ModelDownloadManager"/>.
    /// <para>
    /// PLACEHOLDER: this table is intentionally empty because the PP-OCRv5 assets have not been exported
    /// and uploaded yet, so no real hashes exist. While an entry is absent, <see cref="Asset"/> hands the
    /// downloader a <c>null</c> <see cref="ModelAsset.Sha256"/> and the download is only accepted under
    /// <see cref="Services.ModelDownloadOptions.AllowUnverifiedModels"/> (the fail-open path) — otherwise it
    /// is rejected. <b>Regenerate every value below from the exact uploaded files</b> (e.g.
    /// <c>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))</c>) and fill them in; the verifier
    /// then enforces them with no further code change. Keys must equal the asset <see cref="ModelAsset.FileName"/>.
    /// </para>
    /// </summary>
    private static readonly FrozenDictionary<string, string> Checksums =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // SHA256 (upper-case hex) of every asset published to PaddleOcrNet/PaddleOcrNet-models.
            // Generated by tools/stage_and_checksum.py after the 2026-06-20 re-host upload.
            ["PP-OCRv5_mobile_det.onnx"] = "D7FE3EA74652890722C0F4D02458B7261D9F5AE6C92904D05707C9EB155C7924",
            ["PP-OCRv5_server_det.onnx"] = "7C1A843C497972B9B470748E91A6D83C0F79C4B5B6459508AC533A88F418F33B",
            ["PP-OCRv5_mobile_rec.onnx"] = "D253C3CBEE6E507828A5271A30AB0EC8AE7C2A99D0CC8E6F844FE380809D22B3",
            ["PP-OCRv5_server_rec.onnx"] = "C8CFF87FD83A76B97178250653924598593088C6BB71B2D2D942EF13B9A2D2EC",
            ["ppocrv5_dict.txt"] = "9DFC80C50B6CB07399A47A7CF25D11DB475FB4AD0E1FC96B2EFF6467C8166FF3",
            ["PP-LCNet_x1_0_textline_ori.onnx"] = "1E82A12332D6B0B09A0643250C48A8D372515BA672E1299259ADCF28B9701460",
            ["PP-LCNet_x1_0_doc_ori.onnx"] = "D85B3185075AFCA1A83157F73EAC2E52B598D72E9D47DD19CC4A2F3605E23E3F",
            ["UVDoc.onnx"] = "7E54E917AD9CA8F6CFFE606C7C311AAD3B6EEE457D4D9776F99F175D0CA86835",
            ["latin_PP-OCRv5_mobile_rec.onnx"] = "497DBED20B7FD86334C9DEB5082C2958982A316BB16E3589EBC6502CD85CAE79",
            ["latin_dict.txt"] = "7274E68C7675355E45DD75C360C83FAA0D0624DE33A704C799A7DA8897662201",
            ["cyrillic_PP-OCRv5_mobile_rec.onnx"] = "0D91040605EF7BDCA6868FAD6CAD1664840D031E9F6D40D54607DF5B50FA280A",
            ["cyrillic_dict.txt"] = "90822A96058EB83F8E637ACE75F90CBD36C41253F9DBDFB88FCF5A2BDD50D669",
            ["arabic_PP-OCRv5_mobile_rec.onnx"] = "B58F7D1CC7200566AAE1D56AC26C17942881958ED100AC5A07D9A6E67150CBEF",
            ["arabic_dict.txt"] = "F6B5BAF1335408E6E1FB5CC6E8268A26769F549C2BB37268E72A6804FE269B4C",
            ["devanagari_PP-OCRv5_mobile_rec.onnx"] = "932E2D53FAFDFD929131C4E20133F49901288E8EC1BA03B18317A4780B21F3CE",
            ["devanagari_dict.txt"] = "4BFB5FC511C24DA3B1F3A582AE4E07ECD16601997B70B864BB34E111FC646CFE",
            ["korean_PP-OCRv5_mobile_rec.onnx"] = "EE0DFDE503D787C91FD0455DAAE2EB85311B4B5BDCDDF85A54D8C1E0ADC157DE",
            ["korean_dict.txt"] = "A3792CBB41215A43E555E16FF4A7F7B18DB5DD80CD058637098F0D872F2DD9D6",
            ["japan_PP-OCRv5_mobile_rec.onnx"] = "45B0872D8E93EA6293C9F7A5AED101B4C73B94E3C5F9076AC6176BDD0506EF22",
            ["japan_dict.txt"] = "EF0DEDF763530A436043372CFF6E0886451A40CD203EB8FD9F11384041BBF437",
            ["th_PP-OCRv5_mobile_rec.onnx"] = "6F07AA2AF58713DA04B4BDAF1524588D8051FC734DB5A1AD7B7A65C1E7C7EC18",
            ["th_dict.txt"] = "EAACAE5CF9B0808D9078CE1152C9EA5C6099D1F563750E66B93C12854ACEA31A",
            ["el_PP-OCRv5_mobile_rec.onnx"] = "54235F471B1CA8E356C48673125E0B14E4823496CD7BB583B24FB8808DBCD9C4",
            ["ppocrv5_el_dict.txt"] = "17147B8921FBCC7D2BCBC69CB7DBB7AE61CC0808E6DEC1B311D746FFE5F6AFED",
            ["te_PP-OCRv5_mobile_rec.onnx"] = "E068D1331426F686BCFFA65B04D295359F2615429100119482E7687E91420504",
            ["te_dict.txt"] = "3B4FA5B9A85676C59681DF9155612B86D61A407A2ABB074F729AE66E41301A57",
            ["ta_PP-OCRv5_mobile_rec.onnx"] = "99734F0D198FDDC89869DCCE54CAB4C8330898BAC6AB4A192EE5AEF4DEBB023B",
            ["ta_dict.txt"] = "ED4C188A82743F6067809D004BE1B320B3D2C53C849A9CA83F8E47E7BCD6010F",
            ["eslav_PP-OCRv5_mobile_rec.onnx"] = "BB86CDBD09C21D6BD73C0C0D5DC50379A6F94FC0078F7200A192ED1087F3E6AC",
            ["ppocrv5_eslav_dict.txt"] = "1BBD3D43860503821DEBCBBA74FE9BF3F9E42CAF50DCF6D2F5365D7F75769C91",
            ["latexocr_image_resizer.onnx"] = "E0B075C39700F64D50400F39C8FC186BBB3B5D84D31864008313F376603ACA9D",
            ["latexocr_encoder.onnx"] = "01BF5DC25539CA0CD5B1BD29296EA495977A6BA5F629DC4178277809D26E5E7D",
            ["latexocr_decoder.onnx"] = "BD695497BF1B22279B7626F5916C79226E1E244C84355F8DA7EDFD2D921D0072",
            ["latexocr_tokenizer.json"] = "1DC27B18D6A518D0D5FF3F4BB7BD98521FE80AD39E5B2A246D4109F1BB9D5019",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Builds a <see cref="ModelAsset"/> for <paramref name="fileName"/>: its URL is the file under
    /// <see cref="DefaultBaseUrl"/>, and its checksum is looked up in <see cref="Checksums"/>
    /// (<c>null</c> when not yet published — see the placeholder note on <see cref="Checksums"/>).
    /// </summary>
    private static ModelAsset Asset(string fileName) =>
        new(fileName, $"{DefaultBaseUrl}/{fileName}", Checksums.GetValueOrDefault(fileName));

    // ===============================================================================================
    // Detection
    // ===============================================================================================

    /// <summary>PP-OCRv5 <b>mobile</b> DB text-detection model — the lightweight default detector.</summary>
    public static readonly ModelAsset MobileDetector = Asset(MobileDetFile);

    /// <summary>PP-OCRv5 <b>server</b> DB text-detection model — higher accuracy, heavier than mobile.</summary>
    public static readonly ModelAsset ServerDetector = Asset(ServerDetFile);

    /// <summary>
    /// The DB text detector used by default (shared across all languages). Aliases
    /// <see cref="MobileDetector"/> — the lightweight choice — and is the asset the engine loads unless a
    /// server detector is wired in explicitly.
    /// </summary>
    public static readonly ModelAsset Detector = MobileDetector;

    // ===============================================================================================
    // Orientation classifiers
    // ===============================================================================================

    /// <summary>
    /// Text-line orientation classifier (PP-LCNet, 0°/180° per detected line). Loaded only when
    /// <c>use_textline_orientation</c> is enabled.
    /// </summary>
    public static readonly ModelAsset TextLineOrientationClassifier = Asset(TextLineOriFile);

    /// <summary>
    /// Whole-document orientation classifier (PP-LCNet, 0°/90°/180°/270°). Loaded only when document
    /// orientation correction is enabled; used to upright a page before detection.
    /// </summary>
    public static readonly ModelAsset DocOrientationClassifier = Asset(DocOriFile);

    /// <summary>
    /// The orientation classifier the engine loads for <c>use_textline_orientation</c>. Aliases
    /// <see cref="TextLineOrientationClassifier"/>.
    /// </summary>
    public static readonly ModelAsset Classifier = TextLineOrientationClassifier;

    // ===============================================================================================
    // Document-structure subsystem assets (consumed by Structure/PaddleStructureEngine)
    // ===============================================================================================

    // ----- Layout detection (PP-DocLayout) -----

    /// <summary>PP-DocLayout-S layout detector (PicoDet) ONNX network — the lightweight layout model.</summary>
    public static readonly ModelAsset DocLayoutS = Asset(DocLayoutSFile);

    /// <summary>PP-DocLayout-M layout detector (PicoDet) ONNX network — the medium layout model.</summary>
    public static readonly ModelAsset DocLayoutM = Asset(DocLayoutMFile);

    /// <summary>PP-DocLayout_plus-L layout detector (RT-DETR) ONNX network — the high-accuracy layout model.</summary>
    public static readonly ModelAsset DocLayoutPlusL = Asset(DocLayoutPlusLFile);

    /// <summary>Label sidecar (.yml: raw-class-id → label name) for <see cref="DocLayoutS"/>.</summary>
    public static readonly ModelAsset DocLayoutSLabels = Asset(DocLayoutSLabelsFile);

    /// <summary>Label sidecar (.yml: raw-class-id → label name) for <see cref="DocLayoutM"/>.</summary>
    public static readonly ModelAsset DocLayoutMLabels = Asset(DocLayoutMLabelsFile);

    /// <summary>Label sidecar (.yml: raw-class-id → label name) for <see cref="DocLayoutPlusL"/>.</summary>
    public static readonly ModelAsset DocLayoutPlusLLabels = Asset(DocLayoutPlusLLabelsFile);

    // ----- Document pre-processing (orientation + unwarp) -----

    /// <summary>
    /// PP-LCNet whole-document image-orientation classifier (0/90/180/270°), used by the structure
    /// pre-processor to upright a page. Distinct from the OCR text-line classifier; aliases the same
    /// <c>PP-LCNet_x1_0_doc_ori.onnx</c> asset as <see cref="DocOrientationClassifier"/>.
    /// </summary>
    public static readonly ModelAsset DocImageOrientation = Asset(DocImageOriFile);

    /// <summary>UVDoc document-unwarp (dewarp) ONNX network, used by the structure pre-processor.</summary>
    public static readonly ModelAsset DocUnwarp = Asset(UVDocUnwarpFile);

    // ----- Table structure recognition -----

    /// <summary>PP-LCNet table classifier (wired vs wireless table) — gates which structure model to run.</summary>
    public static readonly ModelAsset TableClassifier = Asset(TableClsFile);

    /// <summary>SLANet_plus wired-table structure recognizer ONNX network.</summary>
    public static readonly ModelAsset SlanetPlusWired = Asset(SlanetPlusWiredFile);

    /// <summary>SLANet_plus wireless-table structure recognizer ONNX network.</summary>
    public static readonly ModelAsset SlanetPlusWireless = Asset(SlanetPlusWirelessFile);

    /// <summary>SLANeXt wired-table structure recognizer ONNX network.</summary>
    public static readonly ModelAsset SlaNeXtWired = Asset(SlaNeXtWiredFile);

    /// <summary>SLANeXt wireless-table structure recognizer ONNX network.</summary>
    public static readonly ModelAsset SlaNeXtWireless = Asset(SlaNeXtWirelessFile);

    /// <summary>Shared table-structure-token dictionary (HTML tag tokens), one token per line.</summary>
    public static readonly ModelAsset TableStructureDict = Asset(TableStructureDictFile);

    /// <summary>RT-DETR-L wired-table cell-detection ONNX network (per-cell bounding boxes).</summary>
    public static readonly ModelAsset RtDetrWiredTableCell = Asset(RtDetrWiredCellFile);

    /// <summary>RT-DETR-L wireless-table cell-detection ONNX network (per-cell bounding boxes).</summary>
    public static readonly ModelAsset RtDetrWirelessTableCell = Asset(RtDetrWirelessCellFile);

    // ----- Seal text recognition -----

    /// <summary>
    /// PP-OCRv4 seal (curved-text) detector ONNX network. Seal recognition feeds the detected curved lines
    /// through the shared SVTR text recognizer, so no dedicated seal recognizer asset is needed.
    /// </summary>
    public static readonly ModelAsset SealDetector = Asset(SealDetFile);

    // ----- Formula recognition (LaTeX-OCR / RapidLaTeXOCR) -----
    // NOTE: formula recognition uses LaTeX-OCR (RapidLaTeXOCR) ONNX, NOT PP-FormulaNet — PP-FormulaNet has
    // no ONNX export path and therefore cannot run on the ONNX-Runtime-only stack used here.

    /// <summary>LaTeX-OCR pre-encode image-resizer ONNX network (normalizes the formula crop size).</summary>
    public static readonly ModelAsset FormulaImageResizer = Asset(LatexImageResizerFile);

    /// <summary>LaTeX-OCR ViT image-encoder ONNX network.</summary>
    public static readonly ModelAsset FormulaEncoder = Asset(LatexEncoderFile);

    /// <summary>LaTeX-OCR autoregressive transformer-decoder ONNX network.</summary>
    public static readonly ModelAsset FormulaDecoder = Asset(LatexDecoderFile);

    /// <summary>LaTeX-OCR tokenizer sidecar (<c>tokenizer.json</c>: token-id → token string).</summary>
    public static readonly ModelAsset FormulaTokenizer = Asset(LatexTokenizerFile);

    // ===============================================================================================
    // Recognition packs (model + matching ppocr dictionary)
    // ===============================================================================================

    private static RecognizerPack Pack(string name, string modelFile, string dictFile, params string[] languages) =>
        new(name, Asset(modelFile), Asset(dictFile), languages);

    /// <summary>
    /// PP-OCRv5 <b>mobile</b> default recognizer: Simplified/Traditional Chinese, English and Japanese on
    /// <c>ppocrv5_dict.txt</c>. The lightweight default — its language list also captures the codes that
    /// fall through to the default model rather than a dedicated script pack.
    /// </summary>
    public static readonly RecognizerPack MobileRecognizer = Pack(
        "PP-OCRv5_mobile", MobileRecFile, "ppocrv5_dict.txt",
        "ch", "ch_sim", "zh", "zh_sim", "en", "ja", "japan_default", "default");

    /// <summary>
    /// PP-OCRv5 <b>server</b> default recognizer: same Chinese/English/Japanese coverage and dictionary as
    /// <see cref="MobileRecognizer"/>, but the higher-accuracy server network. Not in the language map by
    /// default (mobile is the default for those codes); select it explicitly when accuracy matters more
    /// than size.
    /// </summary>
    public static readonly RecognizerPack ServerRecognizer = Pack(
        "PP-OCRv5_server", ServerRecFile, "ppocrv5_dict.txt",
        "ch", "ch_sim", "zh", "zh_sim", "en", "ja");

    // --- Per-script multilingual language packs (PP-OCRv5 mobile rec + the script's ppocr dict) ---
    // Language code lists follow PaddleOCR's per-script grouping; the first code is the representative.

    /// <summary>Latin-script pack (en, fr, de, es, it, pt, nl, …) on <c>latin_dict.txt</c>.</summary>
    public static readonly RecognizerPack Latin = Pack(
        "latin_PP-OCRv5_mobile", "latin_PP-OCRv5_mobile_rec.onnx", "latin_dict.txt",
        "latin", "fr", "de", "es", "it", "pt", "nl", "af", "az", "bs", "cs", "cy", "da", "et", "ga",
        "hr", "hu", "id", "is", "ku", "la", "lt", "lv", "mi", "ms", "mt", "no", "oc", "pi", "pl", "ro",
        "rs_latin", "sk", "sl", "sq", "sv", "sw", "tl", "tr", "uz", "vi");

    /// <summary>Cyrillic-script pack (ru, uk, bg, sr, …) on <c>cyrillic_dict.txt</c>.</summary>
    public static readonly RecognizerPack Cyrillic = Pack(
        "cyrillic_PP-OCRv5_mobile", "cyrillic_PP-OCRv5_mobile_rec.onnx", "cyrillic_dict.txt",
        "cyrillic", "ru", "rs_cyrillic", "be", "bg", "uk", "mn", "abq", "ady", "kbd", "ava", "dar",
        "inh", "che", "lbe", "lez", "tab", "tjk");

    /// <summary>Arabic-script pack (ar, fa, ur, ug) on <c>arabic_dict.txt</c>.</summary>
    public static readonly RecognizerPack Arabic = Pack(
        "arabic_PP-OCRv5_mobile", "arabic_PP-OCRv5_mobile_rec.onnx", "arabic_dict.txt",
        "arabic", "ar", "fa", "ug", "ur");

    /// <summary>Devanagari-script pack (hi, mr, ne, sa, …) on <c>devanagari_dict.txt</c>.</summary>
    public static readonly RecognizerPack Devanagari = Pack(
        "devanagari_PP-OCRv5_mobile", "devanagari_PP-OCRv5_mobile_rec.onnx", "devanagari_dict.txt",
        "devanagari", "hi", "mr", "ne", "bh", "mai", "ang", "bho", "mah", "sck", "new", "gom", "sa", "bgc");

    /// <summary>Korean pack on <c>korean_dict.txt</c>.</summary>
    public static readonly RecognizerPack Korean = Pack(
        "korean_PP-OCRv5_mobile", "korean_PP-OCRv5_mobile_rec.onnx", "korean_dict.txt",
        "korean", "ko");

    /// <summary>Japanese pack on <c>japan_dict.txt</c>.</summary>
    public static readonly RecognizerPack Japanese = Pack(
        "japan_PP-OCRv5_mobile", "japan_PP-OCRv5_mobile_rec.onnx", "japan_dict.txt",
        "japan", "ja_full");

    /// <summary>Thai pack on <c>th_dict.txt</c>.</summary>
    public static readonly RecognizerPack Thai = Pack(
        "th_PP-OCRv5_mobile", "th_PP-OCRv5_mobile_rec.onnx", "th_dict.txt",
        "thai", "th");

    /// <summary>Greek pack on <c>ppocrv5_el_dict.txt</c> (the PP-OCRv5 Greek dictionary).</summary>
    public static readonly RecognizerPack Greek = Pack(
        "el_PP-OCRv5_mobile", "el_PP-OCRv5_mobile_rec.onnx", "ppocrv5_el_dict.txt",
        "greek", "el");

    /// <summary>Telugu pack on <c>te_dict.txt</c>.</summary>
    public static readonly RecognizerPack Telugu = Pack(
        "te_PP-OCRv5_mobile", "te_PP-OCRv5_mobile_rec.onnx", "te_dict.txt",
        "telugu", "te");

    /// <summary>Tamil pack on <c>ta_dict.txt</c>.</summary>
    public static readonly RecognizerPack Tamil = Pack(
        "ta_PP-OCRv5_mobile", "ta_PP-OCRv5_mobile_rec.onnx", "ta_dict.txt",
        "tamil", "ta");

    /// <summary>
    /// Traditional-Chinese codes. No standalone <c>chinese_cht</c> recognizer exists as ONNX; the default
    /// PP-OCRv5 recognizer's <c>ppocrv5_dict.txt</c> already covers Traditional Chinese, so these codes map
    /// to the default mobile model rather than a dedicated pack.
    /// </summary>
    public static readonly RecognizerPack ChineseTraditional = Pack(
        "PP-OCRv5_mobile_cht", MobileRecFile, "ppocrv5_dict.txt",
        "chinese_cht", "ch_tra", "zh_tra", "cht");

    /// <summary>East-Slavic pack on <c>ppocrv5_eslav_dict.txt</c> (the PP-OCRv5 East-Slavic dictionary).</summary>
    public static readonly RecognizerPack EastSlavic = Pack(
        "eslav_PP-OCRv5_mobile", "eslav_PP-OCRv5_mobile_rec.onnx", "ppocrv5_eslav_dict.txt",
        "eslav", "ru_eslav", "uk_eslav", "be_eslav");

    /// <summary>
    /// Every recognizer pack in the catalogue. The default mobile/server recognizers come first so the
    /// Chinese/English/Japanese codes they share resolve to the mobile default (server is opt-in).
    /// </summary>
    public static readonly IReadOnlyList<RecognizerPack> All = new[]
    {
        MobileRecognizer, ServerRecognizer,
        Latin, Cyrillic, Arabic, Devanagari, Korean, Japanese, Thai, Greek,
        Telugu, Tamil, ChineseTraditional, EastSlavic,
    };

    /// <summary>
    /// Language-string → (recognition model asset + dictionary asset) map, materialized as the owning
    /// <see cref="RecognizerPack"/> (which bundles both). The first pack to claim a code wins, so the
    /// order of <see cref="All"/> matters: the mobile default takes the shared ch/en/ja codes.
    /// </summary>
    private static readonly FrozenDictionary<string, RecognizerPack> ByLanguage = BuildLanguageIndex();

    private static FrozenDictionary<string, RecognizerPack> BuildLanguageIndex()
    {
        var dict = new Dictionary<string, RecognizerPack>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in All)
        {
            foreach (var lang in pack.Languages)
            {
                // First pack wins for a shared code: MobileRecognizer (listed first) keeps ch/en/ja so the
                // default mobile model serves them rather than the heavier server pack.
                dict.TryAdd(lang, pack);
            }
        }
        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the recognizer pack (recognition model + matching dictionary) that handles the given
    /// language code, or <c>null</c> if no pack claims it. Case-insensitive.
    /// </summary>
    public static RecognizerPack? FindByLanguage(string language)
        => ByLanguage.TryGetValue(language, out var pack) ? pack : null;
}
