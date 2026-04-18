# ViewModels

Primary view models
- `MangaViewModel`
  - Shared root view model used across reader, search, and library navigation.
  - Owns current page images, OCR state, translation state, thumbnail selection, bookmark list, library view model, and streaming gallery state.
  - Exposes commands for folder open, page navigation, reading-direction toggles, cover-mode toggles, pane toggles, and bookmark actions.
  - Coordinates `MangaManager`, `ImageCacheService`, `OcrService`, `BookmarkService`, and translation settings.
- `LibraryViewModel`
  - Wraps `LibraryService` scanning and exposes `MangaFolders`, `IsLoading`, `IsLoaded`, and `RefreshCommand`.
- `SearchViewModel`
  - Executes E-Hentai searches, parses pagination, loads thumbnails, and exposes gallery detail loading.
- `GalleryItemViewModel`
  - Represents a single search result with title, thumbnail, category-tag collections, and an `OpenCommand` event bridge.
- `MangaPageViewModel`
  - Represents a page entry in thumbnail and bookmark lists.
  - Tracks `FilePath`, progressive thumbnail loading state, cancellation, and stale-result versioning.
- `MangaFolderViewModel`
  - Lightweight library tile model with `FolderPath`, `FolderName`, and `ThumbnailPath`.
- `BoundingBoxViewModel`
  - Holds OCR box geometry, normalized/display coordinates, source image size, original text, translated text, and estimated OCR font size.

Important `MangaViewModel` surface
- Image/page state
  - `LeftImageFilePath`, `RightImageFilePath`
  - `LeftImageSource`, `RightImageSource`
  - `IsSinglePageMode`, `IsTwoPageMode`
  - `SelectedThumbnailIndex`, `Thumbnails`, `Bookmarks`
- OCR/translation state
  - `IsOcrRunning`, `IsOcrActive`, `IsTranslationActive`, `IsTranslationVisible`
  - `LeftOcrBoxes`, `RightOcrBoxes`
  - `LeftOcrText`, `RightOcrText`, `TranslatedLeftOcrText`, `TranslatedRightOcrText`
  - `OcrStatusMessage`, `IsInfoBarOpen`, `OcrSeverity`
- Reader chrome state
  - `IsPaneOpen`, `IsBookmarkPaneOpen`, `IsNavOpen`, `IsLoading`, `IsStreamingGallery`
- Events
  - `PageViewChanged`
  - `PageSlideRequested`
  - `OcrCompleted`
  - `MangaFolderLoaded`
- Streaming/load entry points
  - `BeginStreamingGallery()`
  - `AddDownloadedFiles(IEnumerable<string>)`
  - `LoadLocalFilesAsync(IReadOnlyList<string>)`
  - `LoadMangaFolderAsync(string)`

Data flow
- `MangaManager.PageChanged` -> `MangaViewModel` updates current left/right page state, selected thumbnail index, prefetch work, bookmark/OCR state, and translation visibility.
- `SearchViewModel` supplies gallery metadata to `SearchPage`; once the user opens a gallery, `MangaViewModel.BeginStreamingGallery()` and `AddDownloadedFiles(...)` take over.
- `MangaPageViewModel` performs lazy progressive thumbnail loading so page lists remain responsive during scrolling.

Change notes
- Keep UI-bound collection mutations on the UI thread.
- When adding new state that affects command enablement, remember to call `RaiseCanExecuteChanged` on the related commands.
- `GalleryItemViewModel` stores derived tag collections in multiple shapes; update `RebuildDerived()`/`ApplyTag(...)` together if tag categories change.
- `BoundingBoxViewModel` is used by both overlay layout and translation rendering, so geometry changes can affect reader visuals even if OCR parsing stays the same.
