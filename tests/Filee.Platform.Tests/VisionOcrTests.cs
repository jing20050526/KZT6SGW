using Filee.Core.Conversion;
using Filee.Core.Ocr;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Ocr;
using Filee.Engines.Pdf;
using Filee.Platform.MacOS;

namespace Filee.Platform.Tests;

public sealed class VisionOcrTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public async Task VisionOcrRecognizesTextInImageOnMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS Vision framework.");
            return;
        }

        var engine = new MacOsVisionOcrEngine();
        Assert.True(engine.IsAvailable);
        Assert.Equal("vision", engine.Id);
        Assert.NotEmpty(engine.SupportedLanguages);

        var imagePath = Path.Combine(FindRepoRoot(), "docs", "media", "banner-en.jpg");
        Assert.True(File.Exists(imagePath), $"Test image {imagePath} should exist");

        var result = await engine.RecognizeAsync(imagePath, "en-US", TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Text);
        Assert.NotEmpty(result.Lines);
        Assert.NotEmpty(result.Words);
        Assert.True(result.ImageWidth > 0);
        Assert.True(result.ImageHeight > 0);

        // The banner image has "Filee"
        Assert.Contains("Filee", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImageOcrConverterConvertsImageToTextAndDocx()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS Vision framework.");
            return;
        }

        var engine = new MacOsVisionOcrEngine();
        OcrEngineRegistry.Register(engine);

        var converter = new ImageOcrConverter();
        Assert.True(converter.GetStatus().IsAvailable);

        var imagePath = Path.Combine(FindRepoRoot(), "docs", "media", "banner-en.jpg");
        var workDir = Path.Combine(Path.GetTempPath(), $"filee-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            var txtOut = Path.Combine(workDir, "banner.txt");
            var txtStep = new ConversionStep(
                InputPath: imagePath,
                From: "jpg",
                To: "txt",
                Preset: new Preset(),
                Output: new FixedPath(txtOut),
                WorkDirectory: workDir);

            var txtOutputs = await converter.ConvertAsync(txtStep, null, TestContext.Current.CancellationToken);
            Assert.Single(txtOutputs);
            Assert.True(File.Exists(txtOutputs[0]));
            var txt = await File.ReadAllTextAsync(txtOutputs[0], TestContext.Current.CancellationToken);
            Assert.Contains("Filee", txt, StringComparison.OrdinalIgnoreCase);

            var docxOut = Path.Combine(workDir, "banner.docx");
            var docxStep = new ConversionStep(
                InputPath: imagePath,
                From: "jpg",
                To: "docx",
                Preset: new Preset(),
                Output: new FixedPath(docxOut),
                WorkDirectory: workDir);

            var docxOutputs = await converter.ConvertAsync(docxStep, null, TestContext.Current.CancellationToken);
            Assert.Single(docxOutputs);
            Assert.True(File.Exists(docxOutputs[0]));
            Assert.True(new FileInfo(docxOutputs[0]).Length > 1000);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void PdfDocumentReaderExtractsEditableTextFromScannedPdf()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS Vision framework.");
            return;
        }

        var engine = new MacOsVisionOcrEngine();
        OcrEngineRegistry.Register(engine);

        var imagePath = Path.Combine(FindRepoRoot(), "docs", "media", "banner-en.jpg");
        var workDir = Path.Combine(Path.GetTempPath(), $"filee-pdf-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            var pdfPath = Path.Combine(workDir, "scanned.pdf");

            // Create a pure scanned PDF with only a bitmap image using PdfSharp
            using (var pdfDoc = new PdfSharp.Pdf.PdfDocument())
            {
                var page = pdfDoc.AddPage();
                page.Width = PdfSharp.Drawing.XUnit.FromPoint(600);
                page.Height = PdfSharp.Drawing.XUnit.FromPoint(400);
                using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                using var img = PdfSharp.Drawing.XImage.FromFile(imagePath);
                gfx.DrawImage(img, 0, 0, 600, 400);
                pdfDoc.Save(pdfPath);
            }

            Assert.True(File.Exists(pdfPath));

            // Read the scanned PDF using PdfDocumentReader with OCR enabled
            var mediaFolder = Path.Combine(workDir, "media");
            var document = PdfDocumentReader.Read(pdfPath, mediaFolder, engine);

            Assert.NotEmpty(document.Sections);
            var paragraphs = document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().ToList();
            Assert.NotEmpty(paragraphs);

            var allText = string.Join(" ", paragraphs.SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text));
            Assert.Contains("Filee", allText, StringComparison.OrdinalIgnoreCase);

            // Verify plain text reader also extracts the OCR text
            var plainDoc = PdfDocumentReader.ReadText(pdfPath, engine);
            var plainText = PdfDocumentReader.PlainText(plainDoc);
            Assert.Contains("Filee", plainText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void ScannedPdfWithoutOcrStaysPictureOnly()
    {
        var imagePath = Path.Combine(FindRepoRoot(), "docs", "media", "banner-en.jpg");
        var workDir = Path.Combine(Path.GetTempPath(), $"filee-pdf-noocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            var pdfPath = Path.Combine(workDir, "scanned.pdf");
            using (var pdfDoc = new PdfSharp.Pdf.PdfDocument())
            {
                var page = pdfDoc.AddPage();
                page.Width = PdfSharp.Drawing.XUnit.FromPoint(600);
                page.Height = PdfSharp.Drawing.XUnit.FromPoint(400);
                using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                using var img = PdfSharp.Drawing.XImage.FromFile(imagePath);
                gfx.DrawImage(img, 0, 0, 600, 400);
                pdfDoc.Save(pdfPath);
            }

            var document = PdfDocumentReader.Read(pdfPath, Path.Combine(workDir, "media"), null, null, enableOcr: false);
            var paragraphs = document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>()
                .SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text).ToList();
            Assert.Empty(paragraphs);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    private sealed class FixedPath(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
