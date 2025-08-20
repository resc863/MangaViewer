using MangaViewer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using MangaViewer.Services;

namespace MangaViewer.Pages
{
    public sealed partial class GalleryDetailsPage : Page
    {
        public GalleryItemViewModel Item { get; private set; } = null!;

        public double TagFontSize
        {
            get => (double)GetValue(TagFontSizeProperty);
            set => SetValue(TagFontSizeProperty, value);
        }
        public static readonly DependencyProperty TagFontSizeProperty = DependencyProperty.Register(
            nameof(TagFontSize), typeof(double), typeof(GalleryDetailsPage), new PropertyMetadata(13d));

        public GalleryDetailsPage()
        {
            InitializeComponent();
            TagSettingsService.Instance.TagFontSizeChanged += OnTagFontSizeChanged;
            TagFontSize = TagSettingsService.Instance.TagFontSize; // init
            Unloaded += (_, _) => TagSettingsService.Instance.TagFontSizeChanged -= OnTagFontSizeChanged;
        }

        private void OnTagFontSizeChanged(object? sender, EventArgs e) =>
            TagFontSize = TagSettingsService.Instance.TagFontSize; // DP 업데이트로 바인딩 자동 반영

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is GalleryItemViewModel vm)
            {
                Item = vm;
                Populate();
            }
        }

        private void Populate()
        {
            if (Item == null) return;
            TitleBlock.Text = Item.Title ?? string.Empty;
            IdBlock.Text = Item.GalleryId ?? string.Empty;
            ThumbImage.Source = Item.Thumbnail;
            if (Uri.TryCreate(Item.GalleryUrl ?? string.Empty, UriKind.Absolute, out var u))
                OpenLinkButton.NavigateUri = u;

            BindSimple(LangItems, Item.Languages, null, prefix: "language");
            BindSimple(ArtistsItems, Item.Artists, ArtistsPanel, prefix: "artist");
            BindSimple(GroupsItems, Item.Groups, GroupsPanel, prefix: "group");
            BindSimple(ParodiesItems, Item.Parodies, ParodiesPanel, prefix: "parody");
            BindSimple(MaleTagItems, Item.MaleTags, MaleTagPanel, prefix: "male");
            BindSimple(FemaleTagItems, Item.FemaleTags, FemaleTagPanel, prefix: "female");
            var other = Item.SearchableTags.ToList();
            BindSimple(OtherTagItems, other, OtherTagPanel, prefix: null);
        }

        private void BindSimple(ItemsControl ctl, System.Collections.IEnumerable src, FrameworkElement? section = null, string? prefix = null)
        {
            var list = src.Cast<string>().ToList();
            ctl.ItemsSource = list;
            if (section != null)
                section.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            ctl.Loaded -= OnItemsControlLoaded;
            ctl.Loaded += OnItemsControlLoaded;
            void OnItemsControlLoaded(object sender, RoutedEventArgs e)
            {
                if (sender is not ItemsControl ic) return;
                for (int i = 0; i < list.Count; i++)
                {
                    if (ic.ContainerFromIndex(i) is not FrameworkElement container) continue;
                    container.Tapped -= TagTapped;
                    container.Tapped += TagTapped;
                    container.Tag = prefix;
                }
            }
        }

        private async void TagTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            if (fe.DataContext is not string text || string.IsNullOrWhiteSpace(text)) return;
            string? prefix = fe.Tag as string;
            string query = BuildEhSearchQuery(prefix, text);
            MainWindow.TryNavigate(typeof(SearchPage), MainWindow.RootViewModel);
            if (SearchPage.LastInstance != null)
                await SearchPage.LastInstance.ExternalSearchAsync(query);
        }

        private static string BuildEhSearchQuery(string? prefix, string raw)
        {
            // 따옴표 포함 태그 이스케이프, artist/group 등은 artist:"name$" 형태
            string escaped = raw.Replace("\"", "\\\"");
            if (!string.IsNullOrEmpty(prefix))
                return $"{prefix}:\"{escaped}$\""; // suffix exact match
            return $"\"{escaped}\""; // generic tag search
        }
    }
}
