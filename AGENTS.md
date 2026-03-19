# Project Guidelines

## Scope
- This repository is a WinUI 3 desktop manga reader for Windows built on .NET 10.
- Keep changes focused and local. Prefer matching the existing MVVM and singleton-service design instead of introducing new infrastructure.
- Treat runtime behavior as important as compilation. Many UI binding problems and threading issues only appear at runtime.

## Architecture
- Pages in `MangaViewer/Pages` are thin UI shells. Put state, commands, and page coordination in ViewModels.
- `MangaViewModel` is the central coordinator for reader state, OCR state, navigation, streaming galleries, and thumbnail synchronization.
- `MangaManager` owns page ordering, single/two-page layout rules, RTL/LTR behavior, cover separation, and placeholder replacement during streaming.
- Services in `MangaViewer/Services` are mostly singleton or static-style utilities. Preserve existing public APIs unless a wider refactor is necessary.
- Cache responsibilities are intentionally split:
  - `MemoryImageCache` stores raw streamed bytes for `mem:` keys.
  - `ImageCacheService` stores decoded `BitmapImage` instances and prefetches upcoming pages.

## UI And Threading Rules
- Only update UI-bound collections and WinUI objects on the UI thread.
- Create or mutate `BitmapImage`, `ObservableCollection`, and other UI-bound state through the existing dispatcher helpers or `DispatcherQueue` paths already used in the codebase.
- Keep XAML bindings in sync with ViewModel property names. Binding failures are runtime failures here.
- Avoid moving business logic into code-behind unless the logic is purely page-visual or control-lifecycle specific.

## ViewModel Conventions
- Inherit from `BaseViewModel` for observable state.
- Use `SetProperty` for mutable properties instead of hand-written notification logic.
- Use `RelayCommand` or `AsyncRelayCommand` for commands and update command availability when state changes.
- Preserve existing event-based coordination such as `PageViewChanged`, `PageSlideRequested`, and OCR completion events when extending reader behavior.

## Service Conventions
- Reuse existing services before adding new ones. Similar responsibilities already exist for bookmarks, settings, diagnostics, OCR, translation, thumbnails, and caching.
- `SettingsProvider` is the canonical persistence layer for app settings. Use its generic `Get<T>` and `Set<T>` methods.
- Translation providers already implement the provider boundary. New provider work should follow the existing chat-client pattern instead of branching UI logic per provider.
- Streaming gallery behavior relies on ordered placeholder replacement. Do not break filename/index-based replacement logic in `MangaManager`.

## Build And Validation
- Preferred restore command:

```pwsh
dotnet restore .\MangaViewer\MangaViewer.csproj
```

- Preferred build command:

```pwsh
dotnet build .\MangaViewer\MangaViewer.csproj -c Debug -f net10.0-windows10.0.26100.0 -p:Platform=x64
```

- The project file targets `net10.0-windows10.0.26100.0`. The current VS Code tasks still reference `net9.0-windows10.0.26100.0`; prefer the target declared in the csproj when running commands manually.
- For reader changes, validate RTL/LTR navigation, cover separation, thumbnail selection sync, and placeholder replacement.
- For OCR or translation changes, validate backend selection, per-page state restore, cancellation behavior, and translation cache reuse.
- For cache changes, validate both raw-memory eviction and decoded-image eviction paths.

## High-Value Files
- `MangaViewer/MainWindow.xaml(.cs)`: app shell and shared page navigation.
- `MangaViewer/Pages/MangaReaderPage.xaml(.cs)`: main reading surface and page-level UI behavior.
- `MangaViewer/ViewModels/MangaViewModel.cs`: reader orchestration.
- `MangaViewer/Services/MangaManager.cs`: page mapping and reading rules.
- `MangaViewer/Services/OcrService.cs`: OCR backends and grouping.
- `MangaViewer/Services/ImageCacheService.cs`: decoded image cache and prefetch.
- `MangaViewer/Services/MemoryImageCache.cs`: streamed byte cache.
- `MangaViewer/Services/EhentaiService.cs`: search, details, and streaming source integration.

## Change Discipline
- Do not rewrite unrelated UI or refactor service boundaries without a concrete need.
- Preserve persisted setting keys unless a migration is intentionally included.
- Prefer adding small targeted validation steps in the affected area over broad speculative cleanup.