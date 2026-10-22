// Tesseract OCR engine: a single self-contained binary plus pinned .traineddata files, found the same way as every
// other optional engine (OwnCopies, Filee's own copy, then one already on the PC). The real cross-platform, no-Python
// fallback for platforms without a native OCR API (Windows, Linux) and for scripts Apple Vision doesn't recognize.

using System.Globalization;
using Filee.Core.Ocr;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Ocr;

/// <summary>
/// Optical Character Recognition engine powered by Tesseract (Apache-2.0), run as a subprocess. Covers every
/// platform and, with the matching .traineddata installed, scripts Apple Vision does not (Devanagari, Cyrillic
/// beyond Russian/Ukrainian, Thai, Greek, Arabic script, ...).
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public string Id => "tesseract";
    public string DisplayName => "Tesseract OCR";

    public bool IsAvailable => LocateExecutable() is not null;

    /// <summary>
    /// Filee's language codes this engine can be asked to recognize, limited to what Tesseract's own trained-data
    /// set actually ships (tessdata_fast/best): asking for a code outside this list always fails, so the UI and
    /// <see cref="OcrEngineRegistry"/> only offer what can really work, instead of PaddleOCR's old claimed-but-never
    /// bundled 100+ languages.
    /// </summary>
    public IReadOnlyList<string> SupportedLanguages => [.. LanguageMap.Keys];

    /// <summary>Filee language code (the OCR language picker, BCP-47-ish) → Tesseract's own 3-letter code.</summary>
    private static readonly IReadOnlyDictionary<string, string> LanguageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng",
        ["ne"] = "nep",
        ["hi"] = "hin",
        ["zh-Hans"] = "chi_sim",
        ["zh-Hant"] = "chi_tra",
        ["ja"] = "jpn",
        ["ko"] = "kor",
        ["es"] = "spa",
        ["fr"] = "fra",
        ["de"] = "deu",
        ["it"] = "ita",
        ["pt"] = "por",
        ["ru"] = "rus",
        ["ar"] = "ara",
        ["th"] = "tha",
        ["el"] = "ell",
        ["uk"] = "ukr",
        ["tr"] = "tur",
        ["vi"] = "vie",
        ["id"] = "ind",
        ["pl"] = "pol",
        ["nl"] = "nld",
        ["sv"] = "swe",
        ["da"] = "dan",
        ["fi"] = "fin",
        ["no"] = "nor",
        ["cs"] = "ces",
        ["sk"] = "slk",
        ["hu"] = "hun",
        ["ro"] = "ron",
        ["bg"] = "bul",
        ["hr"] = "hrv",
        ["sr-Cyrl"] = "srp",
        ["sr-Latn"] = "srp_latn",
        ["sl"] = "slv",
        ["et"] = "est",
        ["lv"] = "lav",
        ["lt"] = "lit",
        ["be"] = "bel",
        ["sa"] = "san",
        ["mr"] = "mar",
        ["ta"] = "tam",
        ["te"] = "tel",
        ["ur"] = "urd",
        ["fa"] = "fas",
        ["ps"] = "pus",
        ["sd"] = "snd",
        ["ug"] = "uig",
        ["af"] = "afr",
        ["az"] = "aze",
        ["bs"] = "bos",
        ["ca"] = "cat",
        ["cy"] = "cym",
        ["eu"] = "eus",
        ["ga"] = "gle",
        ["gl"] = "glg",
        ["is"] = "isl",
        ["ka"] = "kat",
        ["ku"] = "kmr",
        ["la"] = "lat",
        ["lb"] = "ltz",
        ["mi"] = "mri",
        ["mk"] = "mkd",
        ["mn"] = "mon",
        ["ms"] = "msa",
        ["mt"] = "mlt",
        ["oc"] = "oci",
        ["qu"] = "que",
        ["sq"] = "sqi",
        ["sw"] = "swa",
        ["tl"] = "fil",
        ["uz"] = "uzb",
        ["kk"] = "kaz",
        ["ky"] = "kir",
        ["tg"] = "tgk",
        ["tt"] = "tat",
    };

    /// <summary>Tesseract's own code for a Filee language code, or null when Tesseract has no matching trained data.</summary>
    public static string? MapLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "eng";
        return LanguageMap.TryGetValue(language.Trim(), out var code) ? code : null;
    }

    public static string? LocateExecutable() =>
        EngineEnvironment.OwnProgram("tesseract", "tesseract.exe")
        ?? EngineEnvironment.BundledProgram("tesseract", "tesseract.exe")
        ?? EngineEnvironment.SystemProgram("tesseract", "tesseract.exe");

    /// <summary>
    /// The tessdata folder next to <paramref name="executable"/>: Homebrew, apt and the official Windows installer
    /// all keep trained-data files at one of these relative locations. Null lets tesseract fall back to its own
    /// compiled-in default (TESSDATA_PREFIX or the path it was built with), same as running it from a shell.
    /// </summary>
    public static string? LocateTessdataDir(string executable)
    {
        var bin = Path.GetDirectoryName(executable);
        if (bin is null)
            return null;
        var root = Path.GetFullPath(Path.Combine(bin, ".."));
        string[] candidates =
        [
            Path.Combine(bin, "tessdata"),
            Path.Combine(root, "share", "tessdata"),
            Path.Combine(root, "share", "tesseract-ocr", "5", "tessdata"),
            Path.Combine(root, "share", "tesseract-ocr", "4.00", "tessdata"),
            "/usr/share/tesseract-ocr/5/tessdata",
            "/usr/share/tesseract-ocr/4.00/tessdata",
            "/usr/share/tessdata",
        ];
        return candidates.FirstOrDefault(c => Directory.Exists(c) && Directory.EnumerateFiles(c, "*.traineddata").Any());
    }

    /// <summary>Whether <paramref name="language"/> has its .traineddata installed, not just a known mapping.</summary>
    public bool HasLanguageData(string? language)
    {
        var exe = LocateExecutable();
        var code = MapLanguage(language);
        if (exe is null || code is null)
            return false;
        var dir = LocateTessdataDir(exe);
        if (dir is null)
            return true; // Can't check; let tesseract itself report a missing language if it turns out to be wrong.
        return File.Exists(Path.Combine(dir, $"{code}.traineddata"));
    }

    public async Task<OcrPageResult> RecognizeAsync(string imagePath, string? language = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Image file not found for OCR.", imagePath);

        var exe = LocateExecutable() ?? throw new InvalidOperationException("Tesseract is not installed.");
        var code = MapLanguage(language)
            ?? throw new InvalidOperationException($"Tesseract has no trained data for language '{language}'.");

        var args = new List<string> { imagePath, "stdout", "-l", code };
        if (LocateTessdataDir(exe) is { } tessdata)
            args.AddRange(["--tessdata-dir", tessdata]);
        args.Add("tsv");

        var result = await ProcessRunner.RunAsync(exe, [.. args], Timeout, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.StandardError.Contains("Failed loading language", StringComparison.OrdinalIgnoreCase)
                ? $"Tesseract has no trained data installed for '{code}'. Install the {language} language pack and try again."
                : $"Tesseract could not read the image. {result.StandardError.Trim()}");
        }

        return ParseTsv(result.StandardOutput, code);
    }

    private static OcrPageResult ParseTsv(string tsv, string language)
    {
        var words = new List<OcrWord>();
        var lineGroups = new List<(string Key, List<OcrWord> Words)>();
        var imgWidth = 0;
        var imgHeight = 0;

        foreach (var row in tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cols = row.Split('\t');
            // level, page_num, block_num, par_num, line_num, word_num, left, top, width, height, conf, text
            if (cols.Length < 12)
                continue;
            if (cols[0] == "1")
            {
                // Page-level row: left/top/width/height is the whole image, needed to scale word boxes back
                // to the source page (PdfDocumentReader) even when the recognized text doesn't fill the image.
                int.TryParse(cols[8], out imgWidth);
                int.TryParse(cols[9], out imgHeight);
                continue;
            }
            if (cols[0] != "5")
                continue;
            var text = cols[11].Trim();
            if (text.Length == 0)
                continue;
            if (!double.TryParse(cols[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
                || !double.TryParse(cols[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var top)
                || !double.TryParse(cols[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                || !double.TryParse(cols[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
                continue;
            float.TryParse(cols[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf);

            var word = new OcrWord(text, new OcrRect(left, top, left + width, top + height), Math.Max(0, conf) / 100f);
            words.Add(word);

            var lineKey = string.Join('.', cols[1], cols[2], cols[3], cols[4]);
            var group = lineGroups.Count > 0 && lineGroups[^1].Key == lineKey ? lineGroups[^1] : (lineKey, []);
            if (lineGroups.Count == 0 || lineGroups[^1].Key != lineKey)
                lineGroups.Add(group);
            group.Words.Add(word);
        }

        var lines = lineGroups.Select(g =>
        {
            var text = string.Join(' ', g.Words.Select(w => w.Text));
            var box = new OcrRect(
                g.Words.Min(w => w.Box.Left), g.Words.Min(w => w.Box.Top),
                g.Words.Max(w => w.Box.Right), g.Words.Max(w => w.Box.Bottom));
            return new OcrLine(text, g.Words, box);
        }).ToList();

        return new OcrPageResult(string.Join('\n', lines.Select(l => l.Text)), lines, words, imgWidth, imgHeight, language);
    }
}
