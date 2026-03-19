# Project Skills

This file is a project task catalog for humans and coding agents. It is not a VS Code auto-loaded skill primitive. If you want auto-discoverable Copilot skills later, add `.github/skills/<skill-name>/SKILL.md` folders that mirror the areas below.

## reader-navigation

Use when
- Changing page navigation, reading direction, cover separation, page pairing, thumbnail pane behavior, or reader animations.

Primary files
- `MangaViewer/Pages/MangaReaderPage.xaml`
- `MangaViewer/Pages/MangaReaderPage.xaml.cs`
- `MangaViewer/ViewModels/MangaViewModel.cs`
- `MangaViewer/Services/MangaManager.cs`

Key checks
- Single-page and two-page layout both work.
- RTL and LTR navigation keep the expected primary page visible.
- Thumbnail selection stays synchronized with the current page.
- Placeholder pages and streamed replacements do not break page numbering.

## search-streaming

Use when
- Changing EHentai search, gallery details, streaming downloads, result paging, or opening streamed galleries directly in the reader.

Primary files
- `MangaViewer/Pages/SearchPage.xaml`
- `MangaViewer/Pages/SearchPage.xaml.cs`
- `MangaViewer/Pages/GalleryDetailsPage.xaml`
- `MangaViewer/Pages/GalleryDetailsPage.xaml.cs`
- `MangaViewer/ViewModels/SearchViewModel.cs`
- `MangaViewer/Services/EhentaiService.cs`
- `MangaViewer/Services/MemoryImageCache.cs`

Key checks
- Search pagination and infinite scroll remain stable.
- Streamed files preserve intended order.
- `mem:` keys resolve correctly in the reader path.
- Opening a gallery before all pages arrive still works.

## ocr-translation

Use when
- Changing OCR backends, OCR grouping, overlay rendering, clipboard copy, translation workflows, or LLM provider integration.

Primary files
- `MangaViewer/Services/OcrService.cs`
- `MangaViewer/ViewModels/BoundingBoxViewModel.cs`
- `MangaViewer/Pages/MangaReaderPage.xaml.cs`
- `MangaViewer/Services/TranslationSettingsService.cs`
- `MangaViewer/Services/GoogleGenAIChatClient.cs`
- `MangaViewer/Services/OpenAIChatClient.cs`
- `MangaViewer/Services/AnthropicChatClient.cs`
- `MangaViewer/Services/OllamaChatClient.cs`

Key checks
- Windows OCR and Ollama OCR both preserve their intended behavior.
- OCR overlays stay aligned with the displayed image.
- Translation is cached by source text and provider/model combination.
- Revisiting a page restores its OCR and translation state correctly.

## caching-thumbnails

Use when
- Changing image decode behavior, thumbnail generation, cache eviction, prefetching, or memory limits.

Primary files
- `MangaViewer/Services/ImageCacheService.cs`
- `MangaViewer/Services/MemoryImageCache.cs`
- `MangaViewer/Services/Thumbnails/ThumbnailCacheService.cs`
- `MangaViewer/Services/Thumbnails/ManagedThumbnailProvider.cs`
- `MangaViewer/Services/Thumbnails/IThumbnailProvider.cs`

Key checks
- Decoded image cache and raw byte cache stay consistent.
- Clearing a streamed gallery removes both raw and decoded entries.
- Prefetch does not create UI-thread violations.
- Thumbnail decode width and cache settings from Settings still apply.

## settings-persistence

Use when
- Adding or changing app settings, provider configuration, cache limits, tag display options, or persisted UI state.

Primary files
- `MangaViewer/Pages/SettingsPage.xaml`
- `MangaViewer/Pages/SettingsPage.xaml.cs`
- `MangaViewer/Services/SettingsProvider.cs`
- `MangaViewer/Services/TagSettingsService.cs`
- `MangaViewer/Services/TranslationSettingsService.cs`
- `MangaViewer/Services/BookmarkService.cs`

Key checks
- Use `SettingsProvider.Get<T>` and `Set<T>` rather than reintroducing legacy wrappers.
- Existing keys remain compatible unless an explicit migration is added.
- Sensitive values such as API keys follow the existing storage pattern.

## library-bookmarks

Use when
- Changing local folder library management, reading progress persistence, or library screen behavior.

Primary files
- `MangaViewer/Pages/LibraryPage.xaml`
- `MangaViewer/Pages/LibraryPage.xaml.cs`
- `MangaViewer/ViewModels/LibraryViewModel.cs`
- `MangaViewer/ViewModels/MangaFolderViewModel.cs`
- `MangaViewer/Services/LibraryService.cs`
- `MangaViewer/Services/BookmarkService.cs`

Key checks
- Adding and removing folders updates persisted library state correctly.
- Bookmark restore returns the reader to the expected page.
- Folder operations do not block the UI thread.

## diagnostics-logging

Use when
- Adding instrumentation, investigating failures, or changing error-handling/reporting code.

Primary files
- `MangaViewer/Services/DiagnosticsService.cs`
- `MangaViewer/Services/Logging/Log.cs`
- `MangaViewer/App.xaml.cs`

Key checks
- Logging remains useful without spamming normal flows.
- Exceptions from background work are surfaced in a way the UI can handle.
- New diagnostics do not leak API keys or full sensitive payloads.

## ui-shell-and-navigation

Use when
- Changing top-level navigation, shared frame behavior, window setup, or page registration.

Primary files
- `MangaViewer/MainWindow.xaml`
- `MangaViewer/MainWindow.xaml.cs`
- `MangaViewer/App.xaml`
- `MangaViewer/App.xaml.cs`

Key checks
- Shared ViewModel instances still flow through navigation as intended.
- Navigation state changes do not reset reader data unexpectedly.
- Startup remains compatible with WinUI 3 desktop app initialization.