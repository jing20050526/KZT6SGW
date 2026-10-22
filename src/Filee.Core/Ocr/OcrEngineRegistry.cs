// Where callers (PdfDocumentReader, ImageOcrConverter, the preset editor) find an OCR engine without depending on
// Filee.Platform.MacOS or Filee.Engines directly: Tesseract registers itself through EngineRegistry.CreateAll on
// every platform, Apple Vision is discovered lazily and by reflection since it lives in a macOS-only assembly.

namespace Filee.Core.Ocr;

/// <summary>
/// Registry of available OCR engines.
/// </summary>
public static class OcrEngineRegistry
{
    private static readonly List<IOcrEngine> RegisteredEngines = [];
    private static readonly Lock Sync = new();

    public static void Register(IOcrEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        lock (Sync)
        {
            if (!RegisteredEngines.Any(e => e.Id.Equals(engine.Id, StringComparison.OrdinalIgnoreCase)))
            {
                RegisteredEngines.Add(engine);
            }
        }
    }

    public static IReadOnlyList<IOcrEngine> All
    {
        get
        {
            lock (Sync)
            {
                DiscoverNativeEngine();
                return [.. RegisteredEngines];
            }
        }
    }

    /// <summary>
    /// The engine to use. With a specific <paramref name="language"/>, only an engine whose
    /// <see cref="IOcrEngine.SupportedLanguages"/> actually covers it is returned — never an engine that would
    /// silently misread a script it doesn't know (e.g. Apple Vision guessing at Devanagari), even if that engine is
    /// otherwise the platform's native/preferred one. With no language ("Automatic"), the platform's native engine
    /// (Apple Vision on macOS: zero install, GPU/NPU accelerated) is preferred, since the document's script isn't
    /// known ahead of time; picking a non-Latin-only language from the preset editor is what routes to Tesseract.
    /// </summary>
    public static IOcrEngine? GetDefault(string? language = null)
    {
        lock (Sync)
        {
            DiscoverNativeEngine();

            if (!string.IsNullOrWhiteSpace(language))
                return RegisteredEngines.FirstOrDefault(e => e.IsAvailable && Supports(e, language));

            var native = RegisteredEngines.FirstOrDefault(e => e.Id.Equals("vision", StringComparison.OrdinalIgnoreCase) && e.IsAvailable);
            return native ?? RegisteredEngines.FirstOrDefault(e => e.IsAvailable);
        }
    }

    /// <summary>
    /// The exact code to pass to <paramref name="engine"/>'s <see cref="IOcrEngine.RecognizeAsync"/> for Filee's
    /// <paramref name="language"/> code: Apple Vision needs its own full BCP-47 entry ("en" → "en-US"), engines that
    /// map Filee's codes themselves (Tesseract) get the code unchanged.
    /// </summary>
    public static string? ResolveLanguage(IOcrEngine? engine, string? language)
    {
        if (engine is null || string.IsNullOrWhiteSpace(language))
            return language;
        return engine.SupportedLanguages.FirstOrDefault(l =>
            l.Equals(language, StringComparison.OrdinalIgnoreCase)
            || l.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)) ?? language;
    }

    /// <summary>
    /// Whether any registered engine (installed or not) claims to support <paramref name="language"/> — for the
    /// preset editor's language picker, so it never offers a language no engine could ever attempt. Use
    /// <see cref="GetDefault"/> to find the engine that can actually run it right now.
    /// </summary>
    public static bool AnyEngineSupports(string language)
    {
        lock (Sync)
        {
            DiscoverNativeEngine();
            return RegisteredEngines.Any(e => Supports(e, language));
        }
    }

    private static bool Supports(IOcrEngine engine, string language) =>
        engine.SupportedLanguages.Count == 0
        || engine.SupportedLanguages.Any(l => l.Equals(language, StringComparison.OrdinalIgnoreCase)
            || l.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));

    /// <summary>Registers the platform's native engine (Apple Vision) the first time it's needed.</summary>
    private static void DiscoverNativeEngine()
    {
        if (!OperatingSystem.IsMacOS() || RegisteredEngines.Any(e => e.Id.Equals("vision", StringComparison.OrdinalIgnoreCase)))
            return;
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Filee.Platform.MacOS");
            if (asm is null)
            {
                try
                {
                    asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(new System.Reflection.AssemblyName("Filee.Platform.MacOS"));
                }
                catch
                {
                    var baseDir = AppContext.BaseDirectory;
                    var path = System.IO.Path.Combine(baseDir, "Filee.Platform.MacOS.dll");
                    if (System.IO.File.Exists(path))
                        asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                }
            }

            var macEngineType = asm?.GetType("Filee.Platform.MacOS.MacOsVisionOcrEngine")
                ?? Type.GetType("Filee.Platform.MacOS.MacOsVisionOcrEngine, Filee.Platform.MacOS");
            if (macEngineType is not null && Activator.CreateInstance(macEngineType) is IOcrEngine macEngine && macEngine.IsAvailable)
                RegisteredEngines.Add(macEngine);
        }
        catch
        {
            // Ignore discovery failure
        }
    }

    public static IOcrEngine? GetById(string id)
    {
        lock (Sync)
        {
            return RegisteredEngines.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }
}
