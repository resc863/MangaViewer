using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using MangaViewer.Services;

namespace MangaViewer
{
    /// <summary>
    /// 애플리케이션 진입/수명 관리.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        public MainWindow? MainWindowInstance => _window as MainWindow;

        public static ILoggerFactory LoggerFactory { get; private set; } = null!;

        public App()
        {
            InitializeComponent();
            ConfigureLogging();
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
        }
    }
}
