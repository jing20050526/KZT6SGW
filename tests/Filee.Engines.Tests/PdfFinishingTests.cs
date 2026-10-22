// Watermark, page numbers and password on real PDFs (PdfFinishing): the stamps are drawn where they belong, any
// language has outlines, and a locked PDF opens only with its password.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using Filee.Engines.Vector;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using SkiaSharp;

#pragma warning disable CA1416

namespace Filee.Engines.Tests;

public class PdfFinishingTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A PDF of blank A4 pages.</summary>
    private static string BlankPdf(string folder, int pages)
    {
        var path = Path.Combine(folder, "blank.pdf");
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
            document.AddPage();
        document.Save(path);
        return path;
    }

    /// <summary>Pixels of a rendered page that aren't white, in a box given as fractions of the page.</summary>
    private static int Marked(string pdf, int page, double left, double top, double right, double bottom, string? password = null)
    {
        using var bitmap = Conversion.ToImage(File.ReadAllBytes(pdf), page, password, new RenderOptions(Dpi: 50, BackgroundColor: SKColors.White));
        var count = 0;
        for (var y = (int)(top * bitmap.Height); y < (int)(bottom * bitmap.Height); y++)
            for (var x = (int)(left * bitmap.Width); x < (int)(right * bitmap.Width); x++)
                if (bitmap.GetPixel(x, y).Red < 245)
                    count++;
        return count;
    }

    [Fact]
    public async Task A_watermark_and_page_numbers_are_drawn_on_every_page()
    {
        var pdf = BlankPdf(fx.NewFolder(), 2);

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "pdf", Pdf = { Watermark = "CONFIDENTIAL", PageNumbers = true } });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = job.Outputs.Single();
        for (var page = 0; page < 2; page++)
        {
            Assert.True(Marked(output, page, 0.3, 0.4, 0.7, 0.6) > 50, "the watermark crosses the middle");
            Assert.True(Marked(output, page, 0.4, 0.9, 0.6, 0.99) > 5, "the page number sits at the bottom");
            Assert.Equal(0, Marked(output, page, 0, 0, 0.1, 0.1)); // the corners stay clean
        }
        Assert.Equal(0, Marked(pdf, 0, 0, 0, 1, 1)); // the source is untouched
    }

    [Fact]
    public async Task A_password_locks_the_pdf()
    {
        var pdf = BlankPdf(fx.NewFolder(), 1);

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "pdf", Pdf = { Password = "filee-1234" } });

        Assert.Equal(JobState.Completed, job.State);
        var output = job.Outputs.Single();
        Assert.ThrowsAny<Exception>(() => PdfReader.Open(output, PdfDocumentOpenMode.Import).Dispose());
        using var opened = PdfReader.Open(output, "filee-1234", PdfDocumentOpenMode.Import);
        Assert.Equal(1, opened.PageCount);
        Assert.Contains("/AESV3", await File.ReadAllTextAsync(output, System.Text.Encoding.Latin1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Text_in_other_scripts_has_outlines()
    {
        using var glyphs = new GlyphOutlines();
        foreach (var text in new[] { "DRAFT", "기밀", "機密", "Черновик" })
        {
            using var outline = PdfFinishing.Outline(glyphs, text, 40, bold: true);
            Assert.SkipWhen(outline.IsEmpty && text != "DRAFT", $"no font for {text} on this machine");
            Assert.False(outline.IsEmpty, text);
        }
    }
}
