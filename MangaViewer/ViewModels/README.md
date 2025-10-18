# ViewModels

Core VM
- `MangaViewModel`
  - State: `Left/RightImageFilePath`, `Left/RightImageSource`, `IsSinglePageMode`, `IsTwoPageMode`, `IsPaneOpen`, `IsNavOpen`, `IsLoading`, `IsOcrRunning`, `SelectedThumbnailIndex`, `Thumbnails`
  - Commands: `OpenFolder`, `NextPage`, `PrevPage`, `ToggleDirection`, `ToggleCover`, `TogglePane`, `ToggleNavPane`, `GoLeft`, `GoRight`, `RunOcr`
  - Events: `PageViewChanged`, `PageSlideRequested(int delta)`, `OcrCompleted`
  - Methods: `BeginStreamingGallery`, `AddDownloadedFiles(IEnumerable<string>)`, `LoadLocalFilesAsync(IReadOnlyList<string>)`, `CreatePlaceholderPages(int)`, `ReplacePlaceholderWithFile(int, string)`, `SetExpectedTotalPages(int)`

- `MangaPageViewModel`
  - State: `FilePath` (null indicates placeholder during streaming). Extend with thumbnail/metadata as needed.

- `SearchViewModel`
  - Manages search parameters/results and triggers streaming start.

- `BoundingBoxViewModel`
  - Holds OCR rectangle coordinates/text/confidence, etc.

Data flow (text)
- `MangaManager.PageChanged` ¡æ `MangaViewModel.OnPageChanged` ¡æ update left/right image paths/sources ¡æ sync thumbnail selection ¡æ prefetch ¡æ reset OCR state.

Change notes
- Command availability: call `RaiseCanExecuteChanged` on state flips.
- Modify UI collections (`_leftOcrBoxes`, `_rightOcrBoxes`) on the UI thread only.
- On RTL/cover toggles, keep the visible primary image on the same page by recomputing page index accordingly.
