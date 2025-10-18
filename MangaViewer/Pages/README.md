# Pages

Composition
- `MangaReaderPage`: main reading surface. Binds to `MangaViewModel`, shows one or two images with OCR overlays, reflects `PageSlideRequested` animations.
- `SearchPage`: search and start streaming gallery. Use `BeginStreamingGallery` ¡æ `SetExpectedTotalPages` ¡æ `AddDownloadedFiles` sequence.
- `SettingsPage`: app settings such as thumbnail/OCR options.
- `GalleryDetailsPage`: gallery detail view.

Data flow (text)
- `NavigationView` in `MainWindow` ¡æ page switch ¡æ `Frame.Navigate(type, viewModel)` to share the single view model instance.
- On reader page load, subscribe to `MangaViewModel.PageViewChanged` ¡æ update element sizes/layout.

Change notes
- XAML bindings must match property names in `ViewModels`. Binding errors are not compile-time errors?double-check at runtime.
- Performance: for large images, consider `Image.DecodePixelWidth/Height` (if applicable) and virtualization strategies.
