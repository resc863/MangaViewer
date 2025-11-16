# MangaViewer (WinUI 3)

A WinUI 3-based manga (image) reader for Windows that supports local folders, single/two-page view, reading direction toggle, cover separation, a right-side thumbnail pane, OCR overlays/copy, and e-hentai search with streaming read.

## Features

- Reader
  - Single/two-page view with auto layout
  - Reading direction toggle (LTR/RTL)
  - Cover page mode (single/two)
  - Right-side thumbnail pane (resizable, persists width/state)
  - Slide animation and prefetch when navigating pages
  - Mica titlebar and translucent UI

- OCR (Windows.Media.Ocr)
  - Languages: Auto/JA/KO/EN
  - Grouping: Word/Line/Paragraph (vertical/horizontal, gap heuristics)
  - Tap overlay boxes to copy recognized text to clipboard

- Search + Streaming
  - e-hentai search results grid (infinite scroll, delayed thumbnail decoding)
  - Tile zoom: Ctrl + mouse wheel (smoothed)
  - Context menu: open/details/re-search by artist/group/tag
  - Streaming download: in-memory keys (`mem:gid:####.ext`) with order preserved → immediate switch to reader

- Details
  - Title/thumbnail/language/artist/group/parody/male/female/other tags
  - Tap tags to run a new search with that condition

- Settings
  - OCR language/grouping/writing mode/paragraph gap
  - Tag font size, thumbnail decode width
  - Image memory cache stats/limits (count/size), clear all/per gallery

## UI Overview

- `MainWindow`: navigation to Reader/Search/Settings
- `MangaReaderPage`: reader (thumbnail pane + main page view + OCR canvas)
- `SearchPage`: query, results grid, infinite scroll, streaming open
- `GalleryDetailsPage`: item metadata
- `SettingsPage`: OCR/thumbnail/cache/tag options

## Shortcuts

- Left/Right arrows: previous/next page (logical with direction)
- Ctrl + mouse wheel: zoom tiles in the search grid

## Requirements

- OS: Windows 11 recommended
  - TargetFramework: `net10.0-windows10.0.26100.0` (Windows SDK 26100)
  - SupportedOSPlatformVersion: `10.0.22621.0`
- SDK/Tooling
  - .NET SDK 10.0+
  - Windows 11 SDK 10.0.26100.x
  - Windows App SDK 1.8.x (via NuGet)

## Build & Run

VS Code tasks (updated):
- `restore`
- `build Debug x64` / `x86` / `ARM64` targeting `net10.0-windows10.0.26100.0`

PowerShell (pwsh) manual commands:

```pwsh
# Restore
dotnet restore .\MangaViewer\MangaViewer.csproj

# Build (x64 Debug)
dotnet build .\MangaViewer\MangaViewer.csproj -c Debug -f net10.0-windows10.0.26100.0 -p:Platform=x64

# Run (x64 Debug)
dotnet run --project .\MangaViewer\MangaViewer.csproj -c Debug -p:Platform=x64
```

## Usage

- Reader: open a folder → navigate via thumbnails/pages; run OCR and tap boxes to copy text
- Search: run a query → click an item to start streaming (memory cache only, no disk save)
- Settings: adjust OCR/thumbnail/cache/tag options

## Project Structure (short)

```
MangaViewer/
  App.xaml, App.xaml.cs
  MainWindow.xaml, MainWindow.xaml.cs
  Pages/
    MangaReaderPage.xaml(.cs)
    SearchPage.xaml(.cs)
    GalleryDetailsPage.xaml(.cs)
    SettingsPage.xaml(.cs)
  ViewModels/
    MangaViewModel.cs
    SearchViewModel.cs (+ GalleryItemViewModel)
    MangaPageViewModel.cs, BoundingBoxViewModel.cs
  Services/
    MangaManager.cs               # local folder/page mapping
    OcrService.cs                 # Windows.Media.Ocr + grouping/writing
    EhentaiService.cs             # search → image streaming (memory)
    ImageCacheService.cs          # image/memory cache, prefetch
    Thumbnails/*                  # decode scheduling/cache
    TagSettingsService.cs         # tag font size (SettingsProvider-backed)
  Controls/
    TagWrapPanel.cs, ParagraphGapSliderControl.cs
  Converters/
    BooleanToVisibilityConverter.cs, etc.
```

## Notes

- Streaming images are kept in memory only (not saved to disk).
- Windows OCR language packs are required for better recognition.
- Large galleries may consume more memory; adjust cache limits or clear cache in Settings.
- This app is a client example for browsing external websites (e-hentai). Follow the site’s ToS and local laws. Trademarks and copyrights belong to their owners.

## Modernization notes
- Folder picker migrated to Windows App SDK `Microsoft.Windows.Storage.Pickers.FolderPicker(WindowId)` (no `InitializeWithWindow`).
- Tag settings now persist via `.NET`-based `SettingsProvider` (no `ApplicationData.Current.LocalSettings`).

## License

See `LICENSE.txt`.

# MangaViewer

Overview
- A WinUI3 (.NET10) manga reader.
- Features: folder-based reading, dual-page (cover split/merged), RTL/LTR switching, thumbnail decode/prefetch, OCR overlay, streaming gallery via `mem:` keys.

Entry points (public surface)
- `App`: application startup, logging configuration, calls `ImageCacheService.InitializeUI(DispatcherQueue)`.
- `MainWindow`: hosts a single `MangaViewModel`, handles navigation/keyboard, enables the Mica backdrop.
- `Pages/*`: views bound to view models.
- `ViewModels`: exposes app state/commands/events.
- `Services`: IO/cache/business logic.

Data flow (high level)

1) Load folder
- UI (OpenFolder) → `MangaViewModel.OpenFolderCommand` → `MangaManager.LoadFolderAsync` (natural sort) → `PageChanged` event → `MangaViewModel` updates current image paths → `ImageCacheService.Get/Prefetch`.

2) Page navigation
- Keyboard/buttons → `MangaViewModel` commands → `MangaManager.GoToNext/Prev/Toggle*` → `PageChanged` → update left/right images, sync thumbnail selection, prefetch.

3) Streaming
- Search/download → `BeginStreamingGallery` → `SetExpectedTotalPages` (placeholders) → `AddDownloadedFiles(mem:/path)` sequential feed → display progressively.

4) OCR
- `RunOcrCommand` → align left/right paths (consider RTL) → `OcrService.GetOcrAsync` → update `BoundingBoxViewModel` collections.

Change checklist
- UI thread: XAML objects (`BitmapImage`, `ObservableCollection`) must be created/modified on the UI thread. Use `DispatcherQueue` from services.
- Cancellation/errors: long-running tasks should accept `CancellationToken`. Log errors and surface user-friendly status (`Services/Logging/Log`).
- Performance: keep prefetch/thumbnail decode concurrency low (2–4). Use small delays to avoid UI starvation.
- Mapping: `MangaManager` rules for cover split/RTL and page↔image mapping must stay consistent. If you change rules, update `GetImagePathsForPage`, `GetPrimaryImageIndexForPage`, and `GetPageIndexFromImageIndex` together.
- Cache: decoded LRU capacity and memory byte cache interact. When removing memory items, evict from decoded cache too.

Test guide
- Natural sort across mixed names (numeric/alphabetic/mixed).
- Verify page mapping across cover split/merged and RTL combinations.
- Streaming `mem:` mixed with local files (replacement/placeholders).
- OCR settings auto re-run when idle.

# Bookmark feature

Purpose
- Save/manage bookmarks per folder for the page you are reading.
- Auto-restore bookmarks when the same folder is opened again.

Usage (UI)
- Open/close the bookmark panel: top toolbar bookmark toggle (`IsBookmarkPaneOpen`).
- Add current page to bookmarks: "Add bookmark" button (`AddBookmarkCommand`).
- Navigate to a bookmark: click an item (`NavigateToBookmarkCommand`).
- Remove a bookmark: click the "Remove" button on the item (`RemoveBookmarkCommand`).
- Resize the panel: drag the left border (width is persisted in settings).

Persistence
- Scope: per folder.
- Storage file: `bookmarks.json` inside the selected folder (auto created/updated).
- JSON schema: `{ "bookmarks": [ "full_path_1", "full_path_2", ... ] }`
- De-duplication: adding an existing path is ignored.
- Error tolerant: read/write errors are ignored to avoid breaking the app.

Behavior
- On folder load completion, `bookmarks.json` is read and the list is rebuilt.
- Items can appear even if the actual file is currently missing; thumbnail/navigation may fail until the file exists again.
- When starting streaming gallery (`BeginStreamingGallery`), the bookmark list is cleared.

Developer notes
- Service: `BookmarkService` (singleton)
 - `LoadForFolder(string? folderPath)` — load current folder bookmarks into memory
 - `GetAll() : IReadOnlyList<string>` — return all bookmark paths
 - `Contains(string path) : bool`
 - `Add(string path) : bool` — persists immediately on success
 - `Remove(string path) : bool` — persists immediately on success
- ViewModel: `MangaViewModel`
 - Collection: `Bookmarks (ObservableCollection<MangaPageViewModel>)`
 - Panel toggle: `IsBookmarkPaneOpen (bool)`
 - Commands: `AddBookmarkCommand`, `RemoveBookmarkCommand`, `NavigateToBookmarkCommand`, `ToggleBookmarkPaneCommand`
 - Folder load timing: `OnMangaLoaded` → `BookmarkService.LoadForFolder(...)` → `RebuildBookmarksFromStore()`
- UI
 - Toolbar toggle/button: `MainWindow.xaml`
 - Panel/list/buttons: `MangaReaderPage.xaml` `BookmarksList`
 - Thumbnails: `ThumbnailDecodeScheduler.EnqueueBookmark(...)` asynchronously decodes bookmark thumbnails (separate from the viewport queue)
 - Settings key (width): `BookmarkPaneWidth` (via `SettingsProvider`)

Limitations
- Bookmarks are path-based. If files/folders are renamed or moved, navigation and thumbnail loading for those items may fail.
