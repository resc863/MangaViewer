# Services overview

Purpose
- Non-UI application logic for image loading, OCR, translation, gallery streaming, settings persistence, library management, diagnostics, and clipboard access.

Main areas
- Image and page management
  - `ImageCacheService`: singleton for decoded `BitmapImage` caching, viewport-fit display caching, `mem:` byte storage delegation, and background prefetch.
  - `MemoryImageCache`: raw byte cache used for streamed gallery pages.
  - `MangaManager`: owns the page list, natural-sort folder loading, placeholder creation for streaming, RTL/cover page mapping, and page navigation.
- OCR
  - `OcrService`: singleton OCR entry point with `Hybrid` and `Vlm` backends, language/grouping/writing settings, paragraph gap tuning, Ollama model settings, ONNX execution-provider options, adjacent-page prefetch controls, and OCR result caching.
  - `OllamaOcrProtocol`: prompt/schema/response parsing helper for Ollama-compatible OCR.
  - `OllamaVlmOcrBackend`: full-image VLM OCR orchestration helper owned by `OcrService`.
  - `HybridOcrBackend`: ONNX layout + crop OCR orchestration helper owned by `OcrService`.
  - `DocLayoutOnnxBackend`: DocLayout ONNX session/EP/compile helper owned by `OcrService`.
  - `LlmEndpointCompatibility`: normalizes Ollama-compatible endpoints and probes llama-server style slot limits.
  - `OllamaRequestLoadCoordinator`: coordinates concurrent OCR requests against Ollama/llama-server.
- Translation
  - `TranslationSettingsService`: persists provider selection, provider-specific model/prompt/API key settings, target language, thinking level, adjacent-page prefetch settings, and overlay sizing settings.
  - `TranslationProviderDescriptor`: central provider metadata registry entry with settings accessors, capability flags, thinking normalization, and client construction.
  - `TranslationProviders` and `TranslationProviderSettingsSnapshot`: normalize provider names and project the active provider configuration.
  - `TranslationClientFactory`: builds the active `IChatClient` from the active provider descriptor.
  - `TranslationService`: owns translation request orchestration, caching, and JSON normalization for box translation.
  - `DelegatingChatClientBase`: shared provider wrapper base for adapters that delegate to an inner `IChatClient`.
  - `GoogleGenAIChatClient`, `OpenAIChatClient`, `AnthropicChatClient`, `OllamaChatClient`: provider adapters built on `Microsoft.Extensions.AI`.
  - `ThinkingLevelHelper`: provider-specific thinking normalization and budget helpers.
- Remote gallery streaming
  - `EhentaiService`: fetches gallery pages, downloads images concurrently, stores them as `mem:` keys, caches completed/partial galleries, and yields ordered batches for streaming playback.
- App state and persistence
  - `SettingsProvider`: JSON-backed key/value store under `%LocalAppData%\\MangaViewer\\settings.json` with DPAPI secret support.
  - `LibraryService`: persists library roots and scans top-level manga folders.
  - `BookmarkService`: persists per-folder bookmarks to `bookmarks.json`.
  - `TagSettingsService`: persists tag font size for gallery details and tag-heavy views.
- Utility services
  - `ClipboardService`: best-effort text copy wrapper.
  - `DiagnosticsService`: aggregates success/failure counts and average latency by operation name.

Key public API highlights
- `ImageCacheService`
  - `InitializeUI(DispatcherQueue)`
  - `Get(string path)`, `GetAsync(string path)`
  - `Prefetch(IEnumerable<string>)`
  - `AddMemoryImage(string key, byte[] data)`, `TryGetMemoryImageBytes(...)`
  - `ClearMemoryImages()`, `ClearGalleryMemory(string galleryId)`
  - `SetMemoryLimits(int? maxCount, long? maxBytes)`, `SetDecodedCacheCapacity(int capacity)`
- `MangaManager`
  - Events: `MangaLoaded`, `PageChanged`
  - Loading: `LoadFolderAsync(string)`, `LoadFolderAsync(StorageFolder)`
  - Streaming: `SetExpectedTotal(int)`, `AddDownloadedFiles(IEnumerable<string>)`, `Clear()`
  - Navigation/state: `CurrentPageIndex`, `IsRightToLeft`, `IsCoverSeparate`, `GoToNextPage()`, `GoToPreviousPage()`, `GoToPage(int)`, `SetCurrentPageFromImageIndex(int)`
  - Mapping: `GetImagePathsForPage(int)`, `GetImagePathsForCurrentPage()`, `GetPrimaryImageIndexForPage(int)`
- `OcrService`
  - State/settings: `Backend`, `CurrentLanguage`, `GroupingMode`, `TextWritingMode`, `OllamaModel`, `OllamaThinkingLevel`, `HybridOnnxFallbackEnabled`
  - Settings mutators: `SetBackend(...)`, `SetLanguage(...)`, `SetGrouping(...)`, `SetWritingMode(...)`, `SetParagraphGapFactorHorizontal(...)`, `SetParagraphGapFactorVertical(...)`
  - Runtime helpers: `GetOcrAsync(...)`, `ClearCache()`, `RefreshLlamaServerSlotLimitAsync(...)`, `GetCompatibleOnnxExecutionProviders()`
- `TranslationSettingsService`
  - Provider state: `Provider`, `TargetLanguage`, `ThinkingLevel`
  - Provider-specific settings: `GetModelForProvider(...)`, `SetModelForProvider(...)`, `GetSystemPromptForProvider(...)`, `SetSystemPromptForProvider(...)`, `GetApiKeyForProvider(...)`, `SetApiKeyForProvider(...)`
  - Overlay/prefetch state: `PrefetchAdjacentPagesEnabled`, `PrefetchAdjacentPageCount`, `OverlayFontSize`, `OverlayBoxScaleHorizontal`, `OverlayBoxScaleVertical`
- `EhentaiService`
  - `GetAllPageUrlsAsync(...)`
  - `DownloadPagesStreamingOrderedAsync(...)`
  - `TryGetCachedGallery(...)`, `TryGetInProgressOrdered(...)`, `TryGetPartialGallery(...)`
  - `CancelDownload(...)`, `CancelAllExcept(...)`, `CleanupSessions()`

Data flow
- Local folder reading
  - `MangaViewModel` -> `MangaManager.LoadFolderAsync(...)` -> page list updates -> `ImageCacheService` resolves images/prefetch -> reader UI refreshes.
- Remote streaming
  - `SearchPage`/`SearchViewModel` -> `EhentaiService` -> `ImageCacheService.AddMemoryImage(mem:...)` -> `MangaManager.SetExpectedTotal/AddDownloadedFiles` -> placeholder replacement in the reader.
- OCR and translation
  - `MangaViewModel.RunOcrAsync` -> `OcrService.GetOcrAsync(...)` -> `BoundingBoxViewModel` collections update.
  - `OcrService` delegates VLM, Hybrid, and DocLayout ONNX responsibilities to internal backend helpers while keeping shared cache/state in one place.
  - If translation is enabled, `TranslationService` resolves the active provider through `TranslationClientFactory`, executes the request, caches the result, and maps translated text back onto OCR boxes and overlay text.

Change notes
- UI-thread affinity matters. `BitmapImage` creation and `ObservableCollection` mutations must stay on the UI thread.
- Streaming replacement logic depends on numeric page extraction from filenames and `mem:` keys. If naming rules change, review `MangaManager.AddDownloadedFiles`.
- OCR hybrid fallback to VLM defaults to off. If behavior changes, update both `OcrService` defaults and Settings UI expectations.
- Provider-specific secrets should continue to flow through `SettingsProvider.GetSecret/SetSecret`; avoid storing new keys as plaintext settings.
- OCR prompt/schema/structured-response changes should stay inside `OllamaOcrProtocol`; avoid reintroducing parsing logic into `MangaViewModel` or UI code.

Extension points
- Add a new translation provider by extending `TranslationProviderKind` and registering a new `TranslationProviderDescriptor`.
- Add a new thumbnail implementation under `Services/Thumbnails` and expose it through `ThumbnailProviderFactory`.
- Add a new remote source by following the ordered streaming pattern used by `EhentaiService` and feeding `MangaViewModel.BeginStreamingGallery()` / `AddDownloadedFiles(...)`.

See also
- `AI-ARCHITECTURE.md`: concise map of the current OCR/translation service split.
