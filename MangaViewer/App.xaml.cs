using Microsoft.UI.Xaml;

namespace MangaViewer
{
    /// <summary>
    /// 애플리케이션 진입/수명 관리.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        public App() => InitializeComponent();

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }
    }
}
