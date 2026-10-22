using Filee.Core.Ocr;
using Filee.Engines.Ocr;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class TesseractOcrTests
{
    // Pure mapping logic: runs even where Tesseract itself isn't installed (CI).
    [Theory]
    [InlineData("en", "eng")]
    [InlineData("ne", "nep")]
    [InlineData("zh-Hans", "chi_sim")]
    [InlineData("zh-Hant", "chi_tra")]
    [InlineData(null, "eng")]
    public void MapLanguage_maps_known_codes(string? filee, string tesseract) =>
        Assert.Equal(tesseract, TesseractOcrEngine.MapLanguage(filee));

    [Fact]
    public void MapLanguage_returns_null_for_a_code_with_no_trained_data()
    {
        // Unlike the old PaddleOCR engine, which silently fell back to English for any code it didn't
        // recognize, an unmapped code must be reported as unsupported rather than guessed at.
        Assert.Null(TesseractOcrEngine.MapLanguage("xal"));
    }

    [Fact]
    public async Task RecognizesTextInAGeneratedImage()
    {
        var engine = new TesseractOcrEngine();
        Assert.SkipUnless(engine.IsAvailable, "Tesseract is not installed on this machine.");

        var imagePath = Path.Combine(Path.GetTempPath(), $"filee-tesseract-test-{Guid.NewGuid():N}.png");
        try
        {
            using (var bitmap = new SKBitmap(400, 120))
            using (var canvas = new SKCanvas(bitmap))
            using (var font = new SKFont(SKTypeface.Default, 48))
            using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
            {
                canvas.Clear(SKColors.White);
                canvas.DrawText("Filee OCR Test", 10, 70, font, paint);
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.OpenWrite(imagePath);
                data.SaveTo(stream);
            }

            var result = await engine.RecognizeAsync(imagePath, "en", TestContext.Current.CancellationToken);

            Assert.Contains("Filee", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(result.Words);
            Assert.True(result.ImageWidth > 0 && result.ImageHeight > 0);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task RecognizeAsync_throws_a_clear_error_for_an_unmapped_language()
    {
        var engine = new TesseractOcrEngine();
        Assert.SkipUnless(engine.IsAvailable, "Tesseract is not installed on this machine.");

        // Any existing file: the unmapped-language check happens before the image is read.
        var imagePath = Path.Combine(AppContext.BaseDirectory, "Filee.Engines.Tests.dll");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.RecognizeAsync(imagePath, "xal", TestContext.Current.CancellationToken));
        Assert.Contains("xal", ex.Message);
    }
}
