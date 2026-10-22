// Image → TXT, DOCX and Markdown via whichever OCR engine OcrEngineRegistry resolves (Apple Vision on macOS,
// Tesseract everywhere else). Allows dropping scans, photos or screenshots directly and extracting editable text.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Ocr;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Ocr;

/// <summary>
/// Converts raster images directly to text documents using Optical Character Recognition.
/// </summary>
public sealed class ImageOcrConverter : IConverter
{
    private static readonly string[] ImageFormats = ["png", "jpg", "jpeg", "webp", "tiff", "bmp"];

    public string Id => "image-ocr";
    public string DisplayName => "Image OCR (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        ImageFormats.SelectMany(from => new ConversionEdge[]
        {
            new(from, "txt", 15, DirectOnly: true),
            new(from, "docx", 15, DirectOnly: true),
            new(from, "md", 15, DirectOnly: true),
        }).ToList();

    public EngineStatus GetStatus()
    {
        var engine = OcrEngineRegistry.GetDefault();
        if (engine is { IsAvailable: true })
            return EngineStatus.Available($"Image → TXT, DOCX, Markdown ({engine.DisplayName})", EngineVersions.BuiltIn);

        return EngineStatus.Unavailable("engine.reason.not_installed", "No supported OCR engine found on this system.");
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!step.Preset.Document.Ocr)
            throw new InvalidOperationException("OCR is disabled in preset settings.");

        var language = step.Preset.Document.OcrLanguage;
        var engine = OcrEngineRegistry.GetDefault(language);
        if (engine is null || !engine.IsAvailable)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(language)
                ? "No OCR engine is installed. Install Tesseract in Settings → Engines."
                : $"No installed OCR engine recognizes '{language}'. Install Tesseract with that language's trained data in Settings → Engines.");

        progress?.Report(0.1);
        var result = await engine.RecognizeAsync(step.InputPath, OcrEngineRegistry.ResolveLanguage(engine, language), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.7);

        if (string.IsNullOrWhiteSpace(result.Text))
            throw new InvalidOperationException("No text could be recognized in the image.");

        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];

        switch (step.To)
        {
            case "txt":
                await File.WriteAllTextAsync(output, result.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
                break;

            case "md":
                var mdContent = string.Join("\n\n", result.Lines.Select(l => l.Text));
                await File.WriteAllTextAsync(output, mdContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
                break;

            case "docx":
                var doc = new HDocument();
                var section = new HSection();
                foreach (var line in result.Lines)
                {
                    var p = new HParagraph();
                    p.Inlines.Add(new HText(line.Text, default));
                    section.Blocks.Add(p);
                }
                doc.Sections.Add(section);
                await Task.Run(() => DocxWriter.Write(doc, output), cancellationToken);
                break;

            default:
                throw new NotSupportedException($"Target format {step.To} is not supported by Image OCR.");
        }

        progress?.Report(1.0);
        return [output];
    }
}
