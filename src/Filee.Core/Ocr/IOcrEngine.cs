// The contract every OCR engine implements (Apple Vision, Tesseract): recognize text and word boxes in an image,
// so callers like PdfDocumentReader can place the result back onto the page without knowing which engine ran.

namespace Filee.Core.Ocr;

/// <summary>
/// A service that extracts text and bounding boxes from images.
/// </summary>
public interface IOcrEngine
{
    /// <summary>Unique engine identifier (e.g. "vision", "tesseract").</summary>
    string Id { get; }

    /// <summary>Display name (e.g. "Apple Vision", "Tesseract OCR").</summary>
    string DisplayName { get; }

    /// <summary>Whether this engine is available and ready on the current machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Supported languages (BCP-47 codes, or empty if generic/all).</summary>
    IReadOnlyList<string> SupportedLanguages { get; }

    /// <summary>
    /// Recognizes text in an image file.
    /// </summary>
    /// <param name="imagePath">Path to an image file (PNG, JPG, TIFF, etc.).</param>
    /// <param name="language">Optional language hint or code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OcrPageResult> RecognizeAsync(string imagePath, string? language = null, CancellationToken cancellationToken = default);
}
