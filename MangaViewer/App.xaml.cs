// Project: MangaViewer
// File: App.xaml.cs
// Purpose: Application entry and lifetime management. Configures logging and initializes
//          services that require a UI DispatcherQueue.

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using MangaViewer.Services;
using System;
using System.Globalization;
using System.Linq;
using Windows.Globalization;
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

        public static ILoggerFactory LoggerFactory { get; private set; } = null!;
        private static readonly string[] SupportedLanguageTags = ["ko-KR", "en-US", "ja-JP"];

        public App()
        {
            ApplyAppLanguage();
            InitializeComponent();
            ConfigureLogging();
            
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

        private static void ConfigureLogging()
        {
            LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            {
                builder
                    .SetMinimumLevel(LogLevel.Information)
                    .AddDebug(); // Debug output window
            });
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();

            // Initialize UI dispatcher for services needing UI thread access
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            ImageCacheService.Instance.InitializeUI(dispatcher);
            OcrService.Instance.InitializeManualOnnxExecutionProvidersOnStartup();
            
            // Handle window closing to cleanup resources
            _window.Closed += OnWindowClosed;
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
                var logger = LoggerFactory.CreateLogger<App>();
                logger.LogError(e.Exception, "Unhandled exception occurred");
            }
            catch { }
        }
    }
}
