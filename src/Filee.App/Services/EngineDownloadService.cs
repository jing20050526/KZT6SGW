// Optional engine downloads for the UI: one observable state per package, one install at a time, and a catalog
// refresh when an engine appears or disappears so donut slices and routes update immediately.

using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public enum EnginePackageStatus
{
    NotInstalled,
    Queued,
    Downloading,
    Unpacking,
    Installed,
    Failed,
    /// <summary>The user's own copy (Settings → Engines) or one found on the PC is used, nothing downloaded.</summary>
    OwnCopy,
}

/// <summary>Install state of one optional engine package, bound by the Engines page and the first-run setup.</summary>
public sealed partial class EnginePackageState : ObservableObject
{
    private readonly ILocalizer _loc;

    public EnginePackageState(EnginePackage package, ILocalizer loc)
    {
        Package = package;
        _loc = loc;
    }

    public EnginePackage Package { get; }
    public string Name => _loc[$"engines.package.{Package.Id}.name"];

    /// <summary>Whether the package card has a Settings button (LibreOffice: which conversions to PDF it takes over).</summary>
    public bool HasSettings => Package.Id == "libreoffice";
    public string Description => _loc[$"engines.package.{Package.Id}.description"];

    public string Sizes => EngineDownloads.CanDownload(Package)
        ? _loc.Format("engines.package.size", FormatBytes(EngineDownloads.DownloadSize(Package)), FormatBytes(Package.InstalledSize))
        : CanSystemInstall(Package)
            ? _loc.Format("engines.package.size", FormatBytes(350_000_000), FormatBytes(Package.InstalledSize))
            : _loc["engines.package.system_required"];

    /// <summary>
    /// How to get an engine Filee doesn't download on this system (FFmpeg and Ghostscript from Homebrew on macOS,
    /// the distribution's packages on Linux), in the app's language; null when Filee can download it.
    /// </summary>
    public string? InstallGuidance
    {
        get
        {
            if (EngineDownloads.CanDownload(Package) || CanSystemInstall(Package))
                return null;
            var system = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "windows";
            var key = $"engines.guidance.{system}.{Package.Id}";
            var text = _loc[key];
            return text != key ? text : _loc.Format($"engines.guidance.{system}", Name);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsInstalled), nameof(IsBusy), nameof(CanInstall), nameof(CanRemove),
        nameof(UsesOwnCopy), nameof(CanUseOwnCopy), nameof(ShowsCopyFolder), nameof(CanUpdate))]
    private EnginePackageStatus _status;

    /// <summary>
    /// Filee's download is installed but older than the version this Filee pins (engines.json changed with an app
    /// update): <see cref="CanUpdate"/> offers the new one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanUpdate))]
    private bool _isOutdated;

    /// <summary>The version Filee downloads now, e.g. "8.0" for FFmpeg (the package's main component).</summary>
    public string? Version => EngineDownloads.Components.TryGetValue(Package.Components[0], out var component) ? component.Version : null;

    /// <summary>Filee's download is installed in an older version than the one Filee downloads now.</summary>
    public bool CanUpdate => Status == EnginePackageStatus.Installed && IsOutdated && EngineDownloads.CanDownload(Package);

    /// <summary>Folder of the copy in use while the status is <see cref="EnginePackageStatus.OwnCopy"/>.</summary>
    [ObservableProperty]
    private string? _ownCopyFolder;

    /// <summary>The copy in use was found on the PC (PATH, Program Files), not chosen by the user.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanInstall), nameof(UsesOwnCopy), nameof(CanUseOwnCopy))]
    private bool _fromSystem;

    /// <summary>"Update recommended: 7.1 here, Filee is tested with 9.0" for an older copy, else null.</summary>
    [ObservableProperty]
    private string? _updateNote;

    /// <summary>0..100 while downloading or unpacking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _error;

    /// <summary>"120 MB / 420 MB · 8.4 MB/s · 36 s left" while downloading, empty otherwise.</summary>
    [ObservableProperty]
    private string _progressDetail = "";

    /// <summary>Ticked in the first-run setup.</summary>
    [ObservableProperty]
    private bool _selected;

    /// <summary>The engine is there: Filee's download or the user's own copy.</summary>
    public bool IsInstalled => Status is EnginePackageStatus.Installed or EnginePackageStatus.OwnCopy;
    public bool CanRemove => Status == EnginePackageStatus.Installed || (Package.Id == "tesseract" && Status == EnginePackageStatus.OwnCopy && FromSystem);
    /// <summary>The user chose this copy ("Stop using it" applies).</summary>
    public bool UsesOwnCopy => Status == EnginePackageStatus.OwnCopy && !FromSystem;
    public bool ShowsCopyFolder => Status == EnginePackageStatus.OwnCopy;
    public bool CanUseOwnCopy => Status is EnginePackageStatus.NotInstalled or EnginePackageStatus.Failed or EnginePackageStatus.Installed
                                 || (Status == EnginePackageStatus.OwnCopy && FromSystem);
    public bool IsBusy => Status is EnginePackageStatus.Queued or EnginePackageStatus.Downloading or EnginePackageStatus.Unpacking;
    /// <summary>Also when a copy on the PC is used: Filee's download is the tested version and wins once installed.</summary>
    public bool CanInstall => (EngineDownloads.CanDownload(Package) && (Status is EnginePackageStatus.NotInstalled or EnginePackageStatus.Failed || (Status == EnginePackageStatus.OwnCopy && FromSystem)))
                              || (CanSystemInstall(Package) && Status is EnginePackageStatus.NotInstalled or EnginePackageStatus.Failed);

    /// <summary>
    /// Whether the app can drive an in-place install of <paramref name="package"/> on this machine: Homebrew on
    /// macOS, winget on Windows. Linux has no handler here (package managers vary too much to drive one
    /// generically), so it keeps the same guidance-text + "Use my copy…" flow as every other engine.
    /// </summary>
    internal static bool CanSystemInstall(EnginePackage package) =>
        package.Id == "tesseract" && (OperatingSystem.IsMacOS() ? LocateBrew() is not null : OperatingSystem.IsWindows());

    /// <summary>Homebrew's own binary, at its two standard prefixes or on PATH; null when it isn't installed.</summary>
    internal static string? LocateBrew()
    {
        if (File.Exists("/opt/homebrew/bin/brew")) return "/opt/homebrew/bin/brew";
        if (File.Exists("/usr/local/bin/brew")) return "/usr/local/bin/brew";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (var part in path.Split(':'))
            {
                var candidate = Path.Combine(part, "brew");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public string StatusText => Status switch
    {
        EnginePackageStatus.Queued => _loc["engines.state.queued"],
        EnginePackageStatus.Downloading => _loc.Format("engines.state.downloading", (int)Progress),
        EnginePackageStatus.Unpacking => _loc["engines.state.unpacking"],
        EnginePackageStatus.Installed => IsOutdated ? _loc.Format("engines.state.update_available", Version ?? "") : _loc["engines.state.installed"],
        EnginePackageStatus.OwnCopy => _loc[FromSystem ? "engines.state.system_copy" : "engines.state.own_copy"],
        EnginePackageStatus.Failed => _loc.Format("engines.state.failed", Error ?? ""),
        _ => _loc["engines.state.not_installed"],
    };

    internal CancellationTokenSource? Cancellation { get; set; }

    /// <summary>Re-reads localized texts after a language change.</summary>
    internal void Relocalize() => OnPropertyChanged(string.Empty);

    /// <summary>"420 MB", "1.2 GB".</summary>
    public static string FormatBytes(long bytes) => bytes >= 1_000_000_000
        ? $"{bytes / 1_000_000_000.0:0.0} GB"
        : $"{Math.Max(1, bytes / 1_000_000)} MB";

    /// <summary>Download amounts and speeds with one decimal where it matters: "850 KB", "12.3 MB", "1.23 GB".</summary>
    public static string FormatAmount(double bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000:0.00} GB",
        >= 100_000_000 => $"{bytes / 1_000_000:0} MB",
        >= 1_000_000 => $"{bytes / 1_000_000:0.0} MB",
        _ => $"{Math.Max(0, bytes / 1_000):0} KB",
    };

    /// <summary>Updates <see cref="ProgressDetail"/> from a download progress report.</summary>
    internal void ShowTransfer(long done, long total, TransferRate rate)
    {
        var remaining = rate.Remaining(done, total);
        var eta = remaining switch
        {
            null => _loc["engines.eta.estimating"],
            { TotalSeconds: < 60 } time => _loc.Format("engines.eta.seconds", Math.Max(1, (int)Math.Ceiling(time.TotalSeconds))),
            var time => _loc.Format("engines.eta.minutes", (int)Math.Ceiling(time.Value.TotalMinutes)),
        };
        ProgressDetail = _loc.Format("engines.progress_detail",
            FormatAmount(done), FormatAmount(total), FormatAmount(rate.BytesPerSecond), eta);
    }
}

public sealed class EngineDownloadService
{
    private readonly ILocalizer _loc;
    private readonly EngineInstaller _installer;
    private readonly ConverterCatalog _catalog;
    private readonly ILogger<EngineDownloadService> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private int _warmingUp;

    public EngineDownloadService(ConverterCatalog catalog, ILocalizer loc, ILogger<EngineDownloadService> logger)
    {
        _catalog = catalog;
        _logger = logger;
        _loc = loc;
        // Redirects are followed by the installer (mirrors may use plain HTTP; files are verified by SHA-256).
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Filee/{typeof(EngineDownloadService).Assembly.GetName().Version}");
        _installer = new EngineInstaller(http, logger: logger);
        Packages = EngineDownloads.Packages.Select(p => new EnginePackageState(p, loc)).ToList();
        if (loc is LocalizationService localization)
            localization.LanguageChanged += (_, _) => Packages.ToList().ForEach(p => p.Relocalize());
        RefreshStatus();
    }

    public IReadOnlyList<EnginePackageState> Packages { get; }

    /// <summary>True while any package is downloading or waiting.</summary>
    public bool IsBusy => Packages.Any(p => p.IsBusy);

    /// <summary>Re-reads what is installed (e.g. after the user deleted a folder).</summary>
    public void RefreshStatus()
    {
        foreach (var state in Packages.Where(p => !p.IsBusy))
            state.Status = StatusOf(state);
    }

    /// <summary>
    /// In the order the engines look: the user's own copy, Filee's download, a copy on the PC. A copy Filee didn't
    /// download gets its version checked in the background (<see cref="EnginePackageState.UpdateNote"/>).
    /// </summary>
    private EnginePackageStatus StatusOf(EnginePackageState state)
    {
        var id = state.Package.Id;
        var own = EngineEnvironment.OwnCopyFolder(id);
        var system = own is null && !_installer.IsInstalled(state.Package) ? EngineEnvironment.SystemCopyFolder(id) : null;
        state.OwnCopyFolder = own ?? system;
        state.FromSystem = system is not null;
        state.UpdateNote = null;
        state.IsOutdated = state.OwnCopyFolder is null && _installer.IsInstalled(state.Package) && !_installer.IsUpToDate(state.Package);
        if (state.OwnCopyFolder is { } folder)
            _ = CheckVersionAsync(state, folder);
        return state.OwnCopyFolder is not null ? EnginePackageStatus.OwnCopy
            : _installer.IsInstalled(state.Package) ? EnginePackageStatus.Installed
            : EnginePackageStatus.NotInstalled;
    }

    /// <summary>Runs the copy's version check off the UI thread (it starts the program once) and notes an old one.</summary>
    private async Task CheckVersionAsync(EnginePackageState state, string folder)
    {
        var id = state.Package.Id;
        var (installed, expected) = await Task.Run(() => (CopyVersions.Installed(id, folder), CopyVersions.Expected(id)));
        if (installed is null || expected is null || installed >= expected || state.OwnCopyFolder != folder)
            return;
        await Dispatcher.UIThread.InvokeAsync(() =>
            state.UpdateNote = _loc.Format("engines.update_recommended", installed.ToString(2), expected.ToString(2)));
    }

    /// <summary>Filee's downloads that have a newer version now (only engines the user installed).</summary>
    public IReadOnlyList<EnginePackageState> Outdated => Packages.Where(p => p.CanUpdate).ToList();

    /// <summary>
    /// Downloads and installs a package, or the new version of an outdated one; packages queue up and install one
    /// after another.
    /// </summary>
    public async Task InstallAsync(EnginePackageState state)
    {
        if (!state.CanInstall && !state.CanUpdate)
            return;
        using var cancellation = new CancellationTokenSource();
        state.Cancellation = cancellation;
        state.Error = null;
        state.Progress = 0;
        state.Status = EnginePackageStatus.Queued;
        try
        {
            await _oneAtATime.WaitAsync(cancellation.Token);
            try
            {
                if (state.Package.Id == "tesseract" && !EngineDownloads.CanDownload(state.Package))
                {
                    await InstallTesseractSystemAsync(state, cancellation.Token);
                }
                else
                {
                    // Progress<T> reports on the UI thread it was created on.
                    var clock = Stopwatch.StartNew();
                    var rate = new TransferRate();
                    var progress = new Progress<EngineInstallProgress>(p =>
                    {
                        if (!state.IsBusy)
                            return;
                        state.Status = p.Stage == EngineInstallStage.Downloading ? EnginePackageStatus.Downloading : EnginePackageStatus.Unpacking;
                        state.Progress = p.Fraction * 100;
                        if (p.Stage == EngineInstallStage.Downloading && p.BytesTotal > 0)
                        {
                            rate.Add(clock.Elapsed, p.BytesDone);
                            state.ShowTransfer(p.BytesDone, p.BytesTotal, rate);
                        }
                        else
                        {
                            state.ProgressDetail = "";
                        }
                    });
                    await Task.Run(() => _installer.InstallAsync(state.Package, progress, cancellation.Token), cancellation.Token);
                }
            }
            finally
            {
                _oneAtATime.Release();
            }
            EngineEnvironment.ForgetSystemCopies();
            state.Status = StatusOf(state);
            _catalog.Refresh();
            if (state.Package.ConverterIds.Contains("libreoffice"))
                WarmUpInBackground(TimeSpan.Zero);
        }
        catch (OperationCanceledException)
        {
            state.Status = StatusOf(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installing engine package {Package} failed", state.Package.Id);
            state.Error = ex is HttpRequestException ? ex.Message : ex.GetBaseException().Message;
            state.Status = EnginePackageStatus.Failed;
        }
        finally
        {
            state.Cancellation = null;
            state.ProgressDetail = "";
        }
    }

    private static async Task InstallTesseractSystemAsync(EnginePackageState state, CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var brew = EnginePackageState.LocateBrew();
                if (brew is null)
                    throw new InvalidOperationException("Homebrew was not found. Install Homebrew from https://brew.sh first.");

                state.Status = EnginePackageStatus.Downloading;
                state.Progress = 5;
                state.ProgressDetail = "Starting Homebrew install...";

                var psi = new ProcessStartInfo
                {
                    FileName = brew,
                    ArgumentList = { "install", "tesseract", "tesseract-lang" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var brewDir = Path.GetDirectoryName(brew);
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (brewDir is not null && !currentPath.Contains(brewDir))
                    psi.Environment["PATH"] = brewDir + ":" + currentPath;
                psi.Environment["HOMEBREW_NO_AUTO_UPDATE"] = "1";
                psi.Environment["HOMEBREW_NO_ENV_HINTS"] = "1";

                using var process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            var line = e.Data.Trim();
                            state.ProgressDetail = line;
                            if (line.StartsWith("==> Downloading", StringComparison.OrdinalIgnoreCase))
                            {
                                state.Status = EnginePackageStatus.Downloading;
                                if (state.Progress < 50) state.Progress += 10;
                            }
                            else if (line.StartsWith("==> Pouring", StringComparison.OrdinalIgnoreCase) ||
                                     line.StartsWith("==> Installing", StringComparison.OrdinalIgnoreCase))
                            {
                                state.Status = EnginePackageStatus.Unpacking;
                                if (state.Progress < 90) state.Progress += 10;
                            }
                        });
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            state.ProgressDetail = e.Data.Trim();
                        });
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using (cancellationToken.Register(() =>
                {
                    try { process.Kill(true); } catch { }
                }))
                {
                    await process.WaitForExitAsync(cancellationToken);
                }

                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"Homebrew install failed with exit code {process.ExitCode}.");

                state.Progress = 100;
                state.ProgressDetail = "";
            }
            else if (OperatingSystem.IsWindows())
            {
                state.Status = EnginePackageStatus.Downloading;
                state.Progress = 10;
                state.ProgressDetail = "Installing Tesseract with winget...";

                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    ArgumentList = { "install", "UB-Mannheim.TesseractOCR", "--accept-source-agreements", "--accept-package-agreements" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            state.ProgressDetail = e.Data.Trim();
                        });
                    }
                };

                process.Start();
                process.BeginOutputReadLine();

                using (cancellationToken.Register(() =>
                {
                    try { process.Kill(true); } catch { }
                }))
                {
                    await process.WaitForExitAsync(cancellationToken);
                }

                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"winget install failed with exit code {process.ExitCode}.");
            }
            else
            {
                throw new PlatformNotSupportedException("Automatic installation is not supported on this platform. Please install tesseract using your package manager.");
            }
        }
        finally
        {
            state.Cancellation = null;
            state.ProgressDetail = "";
        }
    }

    public void Cancel(EnginePackageState state) => state.Cancellation?.Cancel();

    /// <summary>
    /// Prepares LibreOffice's worker profiles in the background, so the first document conversion is as quick as the
    /// following ones (LibreOffice's first start with a new profile takes 2-3× longer). One run at a time; does
    /// nothing when LibreOffice is missing or already prepared.
    /// </summary>
    public void WarmUpInBackground(TimeSpan delay)
    {
        if (Interlocked.Exchange(ref _warmingUp, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                foreach (var libreOffice in _catalog.All.OfType<LibreOfficeConverter>())
                {
                    var prepared = await libreOffice.WarmUpAsync();
                    if (prepared > 0)
                        _logger.LogInformation("Prepared {Count} LibreOffice profile(s)", prepared);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Preparing LibreOffice failed");
            }
            finally
            {
                Interlocked.Exchange(ref _warmingUp, 0);
            }
        });
    }

    /// <summary>Removes a package. Returns false when its files are in use (a conversion is running).</summary>
    public bool Uninstall(EnginePackageState state)
    {
        if (state.IsBusy)
            return false;
        try
        {
            if (state.Package.Id == "tesseract")
            {
                if (OperatingSystem.IsMacOS())
                {
                    var brew = EnginePackageState.LocateBrew();
                    if (brew is not null)
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = brew,
                            ArgumentList = { "uninstall", "tesseract", "tesseract-lang" },
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        var brewDir = Path.GetDirectoryName(brew);
                        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                        if (brewDir is not null && !currentPath.Contains(brewDir))
                            psi.Environment["PATH"] = brewDir + ":" + currentPath;
                        psi.Environment["HOMEBREW_NO_AUTO_UPDATE"] = "1";
                        psi.Environment["HOMEBREW_NO_ENV_HINTS"] = "1";
                        using var process = Process.Start(psi);
                        process?.WaitForExit();
                    }
                }
                else if (OperatingSystem.IsWindows())
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = "winget",
                        ArgumentList = { "uninstall", "UB-Mannheim.TesseractOCR" },
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    process?.WaitForExit();
                }
                EngineEnvironment.ForgetSystemCopies();
            }
            _installer.Uninstall(state.Package);
            state.Status = StatusOf(state);
            _catalog.Refresh();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Removing engine package {Package} failed", state.Package.Id);
            RefreshStatus();
            return false;
        }
    }
}
