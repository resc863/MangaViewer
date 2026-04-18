# Pages

Composition
- `MangaReaderPage`
  - Main reading surface.
  - Binds to the shared `MangaViewModel`, renders one-page/two-page layouts, hosts OCR/translation overlays, manages page-slide animation, persists pane widths, and drives viewport-based thumbnail scheduling.
- `SearchPage`
  - Search UI for E-Hentai galleries.
  - Uses `SearchViewModel` for result paging and detail loading, then streams selected galleries into `MangaViewModel` and navigates to the reader.
- `SettingsPage`
  - Builds settings UI in code-behind.
  - Covers app language, library roots, OCR backend/model/options, translation provider settings, tag font size, thumbnail decode width, and cache management.
- `GalleryDetailsPage`
  - Displays a selected gallery's metadata and tag groups.
  - Supports tag-driven navigation back into `SearchPage`.
- `LibraryPage`
  - Displays library folders using `LibraryViewModel`.
  - Restores scroll position when the cached page is revisited.

Navigation and data flow
- `MainWindow` navigates pages with shared view model instances so reader/search/library pages can coordinate through the same `MangaViewModel` and nested `LibraryViewModel`.
- `SearchPage` starts remote gallery streaming and hands batches to `MangaViewModel` before navigating to `MangaReaderPage`.
- `LibraryPage` loads folders lazily on first navigation and opens the reader through `MainWindow.RootViewModel`.
- `GalleryDetailsPage` can trigger an external search by navigating to `SearchPage` and calling its cached instance.

Change notes
- Keep page-level event subscriptions balanced in `Loaded`/`Unloaded` or navigation hooks, especially on cached pages.
- `SettingsPage` is code-generated in C# rather than declared in XAML, so UI changes belong in the section builder methods.
- Reader overlay and thumbnail behavior depend on `MangaViewModel` property names; binding regressions are runtime-only, so verify end-to-end behavior after changes.
