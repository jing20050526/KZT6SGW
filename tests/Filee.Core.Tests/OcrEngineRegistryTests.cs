using Filee.Core.Ocr;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class OcrEngineRegistryTests
{
    private sealed class MockOcrEngine(string id, string name, bool available, IReadOnlyList<string> languages) : IOcrEngine
    {
        public string Id => id;
        public string DisplayName => name;
        public bool IsAvailable => available;
        public IReadOnlyList<string> SupportedLanguages => languages;

        public Task<OcrPageResult> RecognizeAsync(string imagePath, string? language = null, CancellationToken cancellationToken = default)
        {
            var word = new OcrWord("test", new OcrRect(0, 0, 10, 10), 0.99f);
            var line = new OcrLine("test", [word], new OcrRect(0, 0, 10, 10));
            return Task.FromResult(new OcrPageResult("test", [line], [word], 100, 100, language));
        }
    }

    [Fact]
    public void OcrRect_computes_width_and_height_correctly()
    {
        var rect = new OcrRect(10, 20, 110, 70);
        Assert.Equal(100, rect.Width);
        Assert.Equal(50, rect.Height);
    }

    [Fact]
    public void OcrPageResult_Empty_has_zero_words_and_lines()
    {
        var empty = OcrPageResult.Empty(200, 300);
        Assert.Empty(empty.Text);
        Assert.Empty(empty.Lines);
        Assert.Empty(empty.Words);
        Assert.Equal(200, empty.ImageWidth);
        Assert.Equal(300, empty.ImageHeight);
    }

    [Fact]
    public void Register_and_retrieve_engine()
    {
        var mock = new MockOcrEngine("mock-ocr", "Mock OCR", true, ["en", "ne"]);
        OcrEngineRegistry.Register(mock);

        Assert.Contains(OcrEngineRegistry.All, e => e.Id == "mock-ocr");
        Assert.NotNull(OcrEngineRegistry.GetById("mock-ocr"));
        Assert.Same(mock, OcrEngineRegistry.GetById("mock-ocr"));
    }

    [Fact]
    public void DocumentOptions_defaults_and_cloning()
    {
        var options = new DocumentOptions();
        Assert.True(options.Ocr);
        Assert.Null(options.OcrLanguage);

        options.Ocr = false;
        options.OcrLanguage = "ne";

        var clone = options.Clone();
        Assert.False(clone.Ocr);
        Assert.Equal("ne", clone.OcrLanguage);
    }

    [Fact]
    public void GetDefault_routes_a_language_to_the_engine_that_actually_supports_it()
    {
        // A language no other test's mock claims, so this isn't order-dependent on the shared registry.
        var tesseract = new MockOcrEngine("tesseract", "Tesseract OCR", true, ["th", "el"]);
        OcrEngineRegistry.Register(tesseract);

        var thaiEngine = OcrEngineRegistry.GetDefault("th");
        Assert.NotNull(thaiEngine);
        Assert.Equal("tesseract", thaiEngine.Id);
    }

    [Fact]
    public void GetDefault_returns_null_when_no_available_engine_supports_the_language()
    {
        var unrelated = new MockOcrEngine("unrelated-mock", "Unrelated", true, ["fr"]);
        OcrEngineRegistry.Register(unrelated);

        Assert.Null(OcrEngineRegistry.GetDefault("a-language-nothing-claims"));
    }
}
