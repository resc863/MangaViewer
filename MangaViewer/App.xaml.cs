// Project: MangaViewer
// File: App.xaml.cs
// Purpose: Application entry and lifetime management. Initializes
//          services that require a UI DispatcherQueue.

using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using MangaViewer.Pages;
using MangaViewer.Services;
using MangaViewer.Services.Logging;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Storage;
using Windows.System.UserProfile;

namespace MangaViewer
{
    /// <summary>
    /// 애플리케이션 진입/수명 관리.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        public MainWindow? MainWindow => _window as MainWindow;

        private static readonly string[] SupportedLanguageTags = ["ko-KR", "en-US", "ja-JP"];

        public App()
        {
            ApplyAppLanguage();
            InitializeComponent();
            
            // Handle application exit to cleanup resources
            this.UnhandledException += OnUnhandledException;
        }

        public static string ApplyAppLanguage()
        {
            var selected = SettingsProvider.Get("AppLanguage", "auto");
            if (string.IsNullOrWhiteSpace(selected) || string.Equals(selected, "auto", StringComparison.OrdinalIgnoreCase))
            {
                ApplicationLanguages.PrimaryLanguageOverride = ResolveSystemLanguageOverride();
            }
            else
            {
                ApplicationLanguages.PrimaryLanguageOverride = selected;
            }

            var cultureName = string.IsNullOrWhiteSpace(ApplicationLanguages.PrimaryLanguageOverride)
                ? (ApplicationLanguages.Languages.FirstOrDefault() ?? CultureInfo.CurrentUICulture.Name)
                : ApplicationLanguages.PrimaryLanguageOverride;

            try
            {
                var culture = new CultureInfo(cultureName);
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch
            {
            }

            return cultureName;
        }

        private static string ResolveSystemLanguageOverride()
        {
            foreach (var language in GlobalizationPreferences.Languages)
            {
                var match = SupportedLanguageTags.FirstOrDefault(tag =>
                    string.Equals(tag, language, StringComparison.OrdinalIgnoreCase) ||
                    tag.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase) ||
                    language.StartsWith(tag + "-", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }

            var cultureName = CultureInfo.CurrentUICulture.Name;
            var cultureMatch = SupportedLanguageTags.FirstOrDefault(tag =>
                string.Equals(tag, cultureName, StringComparison.OrdinalIgnoreCase) ||
                tag.StartsWith(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName + "-", StringComparison.OrdinalIgnoreCase));

            return cultureMatch ?? "en-US";
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            if (IsDocLayoutDownloadSelfTest(args))
            {
                _ = RunDocLayoutDownloadSelfTestAsync();
                return;
            }

            if (IsDocLayoutModelSelfTest(args))
            {
                _ = RunDocLayoutModelSelfTestAsync();
                return;
            }

            if (IsHybridOcrPipelineSelfTest(args))
            {
                _ = RunHybridOcrPipelineSelfTestAsync(recognizeText: false);
                return;
            }

            if (IsHybridOcrFullSelfTest(args))
            {
                _ = RunHybridOcrPipelineSelfTestAsync(recognizeText: true);
                return;
            }

            if (IsOcrOverlayMappingSelfTest(args))
            {
                _ = RunOcrOverlayMappingSelfTestAsync();
                return;
            }

            if (IsOnnxEpModeSwitchSelfTest(args))
            {
                _ = RunOnnxEpModeSwitchSelfTestAsync();
                return;
            }

            _window = new MainWindow();
            _window.Activate();

            // Initialize UI dispatcher for services needing UI thread access
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            ImageCacheService.Instance.InitializeUI(dispatcher);
            OcrService.Instance.InitializeManualOnnxExecutionProvidersOnStartup();
            
            // Handle window closing to cleanup resources
            _window.Closed += OnWindowClosed;
        }

        private static bool IsDocLayoutDownloadSelfTest(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            string launchArgs = args.Arguments ?? string.Empty;
            if (launchArgs.Contains("--doclayout-download-selftest", StringComparison.OrdinalIgnoreCase))
                return true;

            return Environment.GetCommandLineArgs()
                .Any(arg => string.Equals(arg, "--doclayout-download-selftest", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsDocLayoutModelSelfTest(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            string launchArgs = args.Arguments ?? string.Empty;
            if (launchArgs.Contains("--doclayout-model-selftest", StringComparison.OrdinalIgnoreCase))
                return true;

            return Environment.GetCommandLineArgs()
                .Any(arg => string.Equals(arg, "--doclayout-model-selftest", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsHybridOcrPipelineSelfTest(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            string launchArgs = args.Arguments ?? string.Empty;
            if (launchArgs.Contains("--hybrid-ocr-pipeline-selftest", StringComparison.OrdinalIgnoreCase))
                return true;

            return Environment.GetCommandLineArgs()
                .Any(arg => string.Equals(arg, "--hybrid-ocr-pipeline-selftest", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsHybridOcrFullSelfTest(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            string launchArgs = args.Arguments ?? string.Empty;
            if (launchArgs.Contains("--hybrid-ocr-full-selftest", StringComparison.OrdinalIgnoreCase))
                return true;

            return Environment.GetCommandLineArgs()
                .Any(arg => string.Equals(arg, "--hybrid-ocr-full-selftest", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsOcrOverlayMappingSelfTest(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            string launchArgs = args.Arguments ?? string.Empty;
            if (launchArgs.Contains("--ocr-overlay-mapping-selftest", StringComparison.OrdinalIgnoreCase))
                return true;

            return Environment.GetCommandLineArgs()
                .Any(arg => string.Equals(arg, "--ocr-overlay-mapping-selftest", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsOnnxEpModeSwitchSelfTest(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            string launchArgs = args.Arguments ?? string.Empty;
            if (launchArgs.Contains("--onnx-ep-mode-switch-selftest", StringComparison.OrdinalIgnoreCase))
                return true;

            return Environment.GetCommandLineArgs()
                .Any(arg => string.Equals(arg, "--onnx-ep-mode-switch-selftest", StringComparison.OrdinalIgnoreCase));
        }

        private async Task RunDocLayoutDownloadSelfTestAsync()
        {
            string resultPath;
            try
            {
                resultPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "doclayout-download-selftest.txt");
            }
            catch
            {
                resultPath = Path.Combine(Path.GetTempPath(), "MangaViewer-doclayout-download-selftest.txt");
            }

            try
            {
                await File.WriteAllTextAsync(resultPath, $"START {DateTimeOffset.Now:O}{Environment.NewLine}");
                var ocr = OcrService.Instance;
                await ocr.DownloadDocLayoutModelAsync(default);

                string modelPath = ocr.GetDocLayoutModelPath();
                long modelBytes = File.Exists(modelPath) ? new FileInfo(modelPath).Length : 0;
                await File.AppendAllTextAsync(
                    resultPath,
                    $"SUCCESS {DateTimeOffset.Now:O}{Environment.NewLine}ModelPath={modelPath}{Environment.NewLine}Bytes={modelBytes}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                try
                {
                    await File.AppendAllTextAsync(
                        resultPath,
                        $"FAIL {DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}");
                }
                catch
                {
                }
            }
            finally
            {
                Exit();
            }
        }

        private async Task RunDocLayoutModelSelfTestAsync()
        {
            string resultPath;
            try
            {
                resultPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "doclayout-model-selftest.txt");
            }
            catch
            {
                resultPath = Path.Combine(Path.GetTempPath(), "MangaViewer-doclayout-model-selftest.txt");
            }

            try
            {
                await File.WriteAllTextAsync(resultPath, $"START {DateTimeOffset.Now:O}{Environment.NewLine}");
                string report = await OcrService.Instance.RunDocLayoutModelSelfTestAsync(default);
                await File.AppendAllTextAsync(
                    resultPath,
                    $"SUCCESS {DateTimeOffset.Now:O}{Environment.NewLine}{report}");
            }
            catch (Exception ex)
            {
                try
                {
                    await File.AppendAllTextAsync(
                        resultPath,
                        $"FAIL {DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}");
                }
                catch
                {
                }
            }
            finally
            {
                Exit();
            }
        }

        private async Task RunHybridOcrPipelineSelfTestAsync(bool recognizeText)
        {
            string resultPath;
            try
            {
                string fileName = recognizeText ? "hybrid-ocr-full-selftest.txt" : "hybrid-ocr-pipeline-selftest.txt";
                resultPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, fileName);
            }
            catch
            {
                string fileName = recognizeText ? "MangaViewer-hybrid-ocr-full-selftest.txt" : "MangaViewer-hybrid-ocr-pipeline-selftest.txt";
                resultPath = Path.Combine(Path.GetTempPath(), fileName);
            }

            try
            {
                await File.WriteAllTextAsync(resultPath, $"START {DateTimeOffset.Now:O}{Environment.NewLine}");
                string report = await OcrService.Instance.RunHybridOcrPipelineSelfTestAsync(default, recognizeText);
                await File.AppendAllTextAsync(
                    resultPath,
                    $"SUCCESS {DateTimeOffset.Now:O}{Environment.NewLine}{report}");
            }
            catch (Exception ex)
            {
                try
                {
                    await File.AppendAllTextAsync(
                        resultPath,
                        $"FAIL {DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}");
                }
                catch
                {
                }
            }
            finally
            {
                Exit();
            }
        }

        private async Task RunOcrOverlayMappingSelfTestAsync()
        {
            string resultPath;
            try
            {
                resultPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "ocr-overlay-mapping-selftest.txt");
            }
            catch
            {
                resultPath = Path.Combine(Path.GetTempPath(), "MangaViewer-ocr-overlay-mapping-selftest.txt");
            }

            try
            {
                await File.WriteAllTextAsync(resultPath, $"START {DateTimeOffset.Now:O}{Environment.NewLine}");
                string report = MangaReaderPage.RunOcrOverlayMappingSelfTest();
                await File.AppendAllTextAsync(
                    resultPath,
                    $"SUCCESS {DateTimeOffset.Now:O}{Environment.NewLine}{report}");
            }
            catch (Exception ex)
            {
                try
                {
                    await File.AppendAllTextAsync(
                        resultPath,
                        $"FAIL {DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}");
                }
                catch
                {
                }
            }
            finally
            {
                Exit();
            }
        }

        private async Task RunOnnxEpModeSwitchSelfTestAsync()
        {
            string resultPath;
            try
            {
                resultPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "onnx-ep-mode-switch-selftest.txt");
            }
            catch
            {
                resultPath = Path.Combine(Path.GetTempPath(), "MangaViewer-onnx-ep-mode-switch-selftest.txt");
            }

            try
            {
                await File.WriteAllTextAsync(resultPath, $"START {DateTimeOffset.Now:O}{Environment.NewLine}");
                string report = await OcrService.Instance.RunOnnxEpModeSwitchSelfTestAsync(default);
                await File.AppendAllTextAsync(
                    resultPath,
                    $"SUCCESS {DateTimeOffset.Now:O}{Environment.NewLine}{report}");
            }
            catch (Exception ex)
            {
                try
                {
                    await File.AppendAllTextAsync(
                        resultPath,
                        $"FAIL {DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}");
                }
                catch
                {
                }
            }
            finally
            {
                Exit();
            }
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            // Cleanup services
            try
            {
                ImageCacheService.Instance.Dispose();
            }
            catch { }
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // Log unhandled exceptions
            try
            {
                Log.Error(e.Exception, "Unhandled exception occurred");
            }
            catch { }
        }
    }
}
