# Services overview

Purpose
- Non-UI application logic: decoded/memory caches, thumbnail pipeline, OCR, EHentai integration, logging, tag settings.

Public API highlights
- `ImageCacheService`
  - `Instance` (singleton), `InitializeUI(DispatcherQueue)`, `Get(string path) : BitmapImage?`, `Prefetch(IEnumerable<string>)`,
    `AddMemoryImage(string key, byte[] data)`, `ClearMemoryImages()`, `ClearGalleryMemory(string galleryId)`,
    `SetMemoryLimits(int? maxCount, long? maxBytes)`, `SetDecodedCacheCapacity(int capacity)`.
- `MangaManager`
  - Events: `MangaLoaded`, `PageChanged`
  - Load: `LoadFolderAsync(StorageFolder)`; streaming: `SetExpectedTotal(int)`, `AddDownloadedFiles(IEnumerable<string>)`, `ReplaceFileAtIndex(int, string)`, `CreatePlaceholders(int)`
  - State/navigation: `CurrentPageIndex`, `IsRightToLeft`, `IsCoverSeparate`, `GoToNext/Previous/GoToPage`,
    `GetImagePathsForPage(int)`, `GetImagePathsForCurrentPage()`, `GetPrimaryImageIndexForPage(int)`
- `OcrService`
  - Event: `SettingsChanged`
  - `GetOcrAsync(string, CancellationToken) : List<BoundingBoxViewModel>` (see implementation), `ClearCache()`
- `EhentaiService` (summary)
  - Search/detail/download streaming APIs that feed `BeginStreamingGallery`/`AddDownloadedFiles`.

Data flow (text diagram)
- Folder load
  App/Window ¡æ MangaViewModel.OpenFolder ¡æ MangaManager.LoadFolderAsync
   ? (batched add with UI marshalling) ¡æ MangaManager.PageChanged ¡æ MangaViewModel.OnPageChanged
   ¡æ ImageCacheService.Get/Prefetch ¡æ UI updates

- Streaming
  Search (EhentaiService) ¡æ BeginStreamingGallery ¡æ SetExpectedTotalPages ¡æ AddDownloadedFiles(mem:/path)
   ? MangaManager.AddDownloadedFiles (index extraction/replacement) ¡æ PageChanged ¡æ View updates

- OCR
  RunOcr ¡æ OcrService.GetOcrAsync(path) ¡¿ [L/R] ¡æ update box collections ¡æ overlay renders

Change notes
- Threading
  - Modify UI-bound `ObservableCollection` only on the UI thread. Use `DispatcherQueue.TryEnqueue` in services.
  - Create `BitmapImage` on the UI thread via `ImageCacheService.CreateBitmapOnUi`.
- Cache consistency
  - When removing `mem:gid:*` via `ClearGalleryMemory`, also remove from decoded LRU (already implemented).
  - When changing capacity policies, review both memory and decoded caches and their eviction ordering.
- Sorting/indexing
  - `MangaManager.ToNaturalSortKey`/numeric index replacement relies on filenames. If rules change, review replacement and page mapping.

Extension points
- New source/provider: add under `Services/Thumbnails` and register in `ThumbnailProviderFactory`.
- Different OCR engine: introduce an interface for `OcrService` and inject via DI.
- Remote sources: follow the `EhentaiService` pattern and reuse the `MangaViewModel` streaming path.
