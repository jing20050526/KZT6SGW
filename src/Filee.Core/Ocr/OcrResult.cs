// The result of an IOcrEngine.RecognizeAsync call: text and boxes in image pixel coordinates, grouped into words
// and lines so a caller can either read the plain text or reconstruct layout (PdfDocumentReader scales these boxes
// back onto the PDF page, one shared baseline per line rather than per word).

namespace Filee.Core.Ocr;

/// <summary>
/// A bounding box in image pixel coordinates (Left/Top to Right/Bottom).
/// </summary>
public readonly record struct OcrRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Math.Max(0, Right - Left);
    public double Height => Math.Max(0, Bottom - Top);
}

/// <summary>
/// A recognized word with its text, bounding box, and recognition confidence.
/// </summary>
public sealed record OcrWord(string Text, OcrRect Box, float Confidence = 1.0f);

/// <summary>
/// A recognized line composed of words.
/// </summary>
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words, OcrRect Box);

/// <summary>
/// The complete OCR result of an image or page.
/// </summary>
public sealed record OcrPageResult(
    string Text,
    IReadOnlyList<OcrLine> Lines,
    IReadOnlyList<OcrWord> Words,
    int ImageWidth,
    int ImageHeight,
    string? Language = null)
{
    public static OcrPageResult Empty(int width = 0, int height = 0) =>
        new("", [], [], width, height);
}
