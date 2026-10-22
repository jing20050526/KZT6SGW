// OCR through Apple's Vision framework (VNRecognizeTextRequest), called directly via the Objective-C runtime so
// Filee.Platform.MacOS stays free of an Objective-C bridging library. Zero install size and GPU/NPU accelerated;
// the preferred engine on macOS whenever its SupportedLanguages cover what was asked for.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Filee.Core.Ocr;

namespace Filee.Platform.MacOS;

/// <summary>Optical Character Recognition engine powered by Apple's Vision framework (macOS only).</summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsVisionOcrEngine : IOcrEngine
{
    private static readonly Lazy<bool> VisionAvailable = new(() =>
    {
        if (!OperatingSystem.IsMacOS())
            return false;
        try
        {
            NativeLibrary.Load("/System/Library/Frameworks/Vision.framework/Vision");
            return objc_getClass("VNRecognizeTextRequest") != 0;
        }
        catch
        {
            return false;
        }
    });

    public string Id => "vision";

    public string DisplayName => "Apple Vision";

    public bool IsAvailable => VisionAvailable.Value;

    public IReadOnlyList<string> SupportedLanguages { get; } =
    [
        "en-US", "fr-FR", "it-IT", "de-DE", "es-ES", "pt-BR", "zh-Hans", "zh-Hant", "ja-JP", "ko-KR", "ru-RU", "uk-UA"
    ];

    public Task<OcrPageResult> RecognizeAsync(string imagePath, string? language = null, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return Task.FromResult(OcrPageResult.Empty());

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pool = new AutoreleasePool();

            var (width, height) = GetImageDimensions(imagePath);
            if (width <= 0 || height <= 0)
                (width, height) = (1000, 1000);

            var nsPath = CFStringCreateWithCString(0, imagePath, 0x08000100);
            if (nsPath == 0)
                return OcrPageResult.Empty(width, height);

            try
            {
                var urlClass = objc_getClass("NSURL");
                var fileUrl = Send(urlClass, sel_registerName("fileURLWithPath:"), nsPath);

                var reqClass = objc_getClass("VNRecognizeTextRequest");
                var request = Send(Send(reqClass, sel_registerName("alloc")), sel_registerName("init"));
                SendInteger(request, sel_registerName("setRecognitionLevel:"), 0); // 0 = VNRequestTextRecognitionLevelAccurate
                SendBool(request, sel_registerName("setUsesLanguageCorrection:"), true);

                if (!string.IsNullOrEmpty(language))
                {
                    var langString = CFStringCreateWithCString(0, language, 0x08000100);
                    if (langString != 0)
                    {
                        var arrayClass = objc_getClass("NSArray");
                        var langsArray = Send(arrayClass, sel_registerName("arrayWithObject:"), langString);
                        Send(request, sel_registerName("setRecognitionLanguages:"), langsArray);
                        CFRelease(langString);
                    }
                }

                var dictClass = objc_getClass("NSDictionary");
                var emptyDict = Send(dictClass, sel_registerName("dictionary"));
                var handlerClass = objc_getClass("VNImageRequestHandler");
                var handler = Send(Send(handlerClass, sel_registerName("alloc")), sel_registerName("initWithURL:options:"), fileUrl, emptyDict);

                var arrayClass2 = objc_getClass("NSArray");
                var requestsArray = Send(arrayClass2, sel_registerName("arrayWithObject:"), request);

                if (!SendPerform(handler, sel_registerName("performRequests:error:"), requestsArray, out _))
                    return OcrPageResult.Empty(width, height);

                var results = Send(request, sel_registerName("results"));
                if (results == 0)
                    return OcrPageResult.Empty(width, height);

                var count = (int)CFArrayGetCount(results);
                var lines = new List<OcrLine>();
                var allWords = new List<OcrWord>();
                var fullText = new StringBuilder();

                for (var i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var obs = CFArrayGetValueAtIndex(results, i);
                    var candidates = Send(obs, sel_registerName("topCandidates:"), 1);
                    if (candidates == 0 || CFArrayGetCount(candidates) == 0)
                        continue;

                    var cand = CFArrayGetValueAtIndex(candidates, 0);
                    var nsStr = Send(cand, sel_registerName("string"));
                    var utf8Ptr = Send(nsStr, sel_registerName("UTF8String"));
                    var text = Marshal.PtrToStringUTF8(utf8Ptr);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var conf = SendFloat(cand, sel_registerName("confidence"));
                    var normBox = SendRect(obs, sel_registerName("boundingBox"));

                    // Apple Vision returns normalized coordinates with bottom-left origin:
                    // normBox.X (0..1 from left), normBox.Y (0..1 from bottom),
                    // normBox.Width, normBox.Height.
                    // Convert to pixel coordinates with top-left origin:
                    var pixelLeft = normBox.X * width;
                    var pixelRight = (normBox.X + normBox.Width) * width;
                    var pixelTop = (1.0 - normBox.Y - normBox.Height) * height;
                    var pixelBottom = (1.0 - normBox.Y) * height;
                    var lineBox = new OcrRect(pixelLeft, pixelTop, pixelRight, pixelBottom);

                    // Estimate word boxes along the line
                    var rawWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var wordsInLine = new List<OcrWord>();
                    if (rawWords.Length == 1)
                    {
                        var word = new OcrWord(rawWords[0], lineBox, conf);
                        wordsInLine.Add(word);
                        allWords.Add(word);
                    }
                    else if (rawWords.Length > 1)
                    {
                        var totalChars = rawWords.Sum(w => w.Length);
                        var currentX = pixelLeft;
                        var usableWidth = Math.Max(0, lineBox.Width);
                        for (var w = 0; w < rawWords.Length; w++)
                        {
                            var wStr = rawWords[w];
                            var wWidth = totalChars > 0 ? (usableWidth * wStr.Length / (double)totalChars) : (usableWidth / rawWords.Length);
                            var wBox = new OcrRect(currentX, pixelTop, currentX + wWidth, pixelBottom);
                            var word = new OcrWord(wStr, wBox, conf);
                            wordsInLine.Add(word);
                            allWords.Add(word);
                            currentX += wWidth;
                        }
                    }

                    lines.Add(new OcrLine(text, wordsInLine, lineBox));
                    fullText.AppendLine(text);
                }

                return new OcrPageResult(fullText.ToString().TrimEnd(), lines, allWords, width, height, language);
            }
            finally
            {
                CFRelease(nsPath);
            }
        }, cancellationToken);
    }

    private static (int Width, int Height) GetImageDimensions(string path)
    {
        try
        {
            var nsImageClass = objc_getClass("NSImage");
            var nsPath = CFStringCreateWithCString(0, path, 0x08000100);
            if (nsPath == 0)
                return (0, 0);
            try
            {
                var alloc = Send(nsImageClass, sel_registerName("alloc"));
                var image = Send(alloc, sel_registerName("initWithContentsOfFile:"), nsPath);
                if (image == 0)
                    return (0, 0);
                var size = SendSize(image, sel_registerName("size"));
                return ((int)Math.Round(size.Width), (int)Math.Round(size.Height));
            }
            finally
            {
                CFRelease(nsPath);
            }
        }
        catch
        {
            return (0, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    private readonly struct AutoreleasePool : IDisposable
    {
        private readonly nint _pool = Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));
        public AutoreleasePool() { }
        public void Dispose() => SendVoid(_pool, sel_registerName("drain"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(nint array);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetValueAtIndex(nint array, nint index);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint obj);

    [DllImport(ObjC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint arg1, nint arg2);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendInteger(nint receiver, nint selector, nint value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendPerform(nint receiver, nint selector, nint requests, out nint error);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern float SendFloat(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern CGRect SendRect(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern CGSize SendSize(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);
}
