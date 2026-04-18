# Thumbnails subsystem

Components
- `IThumbnailProvider`
  - Abstraction for decoding thumbnail `ImageSource` instances from either a file path or raw in-memory bytes.
- `ManagedThumbnailProvider`
  - Default provider.
  - Uses `BitmapImage` decoding on the UI thread, supports both disk files and `mem:` gallery bytes, and writes results into `ThumbnailCacheService`.
- `ThumbnailCacheService`
  - Thread-safe LRU cache keyed by `path + decodeWidth`.
  - Enforces both entry-count and soft byte limits.
- `ThumbnailDecodeScheduler`
  - Central queue for thumbnail work.
  - Prioritizes items near the selected thumbnail index and gives bookmark thumbnails higher priority than normal page thumbnails.
- `ThumbnailProviderFactory`
  - Returns the single active thumbnail provider instance.
- `ThumbnailOptions`
  - Static convenience access for `DecodePixelWidth` and the progressive high-quality decode delay.
- `ThumbnailSettingsService`
  - Persists the configured decode width and raises `SettingsChanged` when it changes.

Current public API highlights
- `IThumbnailProvider`
  - `GetForPathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim, CancellationToken ct)`
  - `GetForBytesAsync(DispatcherQueue dispatcher, byte[] data, int maxDecodeDim, CancellationToken ct)`
- `ThumbnailCacheService`
  - `Get(string path, int decodeWidth)`
  - `Add(string path, int decodeWidth, ImageSource image)`
  - `Remove(string path, int decodeWidth)`
  - `Clear()`
  - `GetStats()`
- `ThumbnailDecodeScheduler`
  - `Enqueue(...)`
  - `EnqueueBookmark(...)`
  - `UpdateSelectedIndex(int selectedIndex)`
  - `ReplacePendingWithViewportFirst(...)`
  - `SetMaxConcurrency(int value)`
- `ThumbnailOptions`
  - `DecodePixelWidth`
  - `ProgressiveDecodeDelay`
- `ThumbnailSettingsService`
  - `DecodeWidth`
  - `SettingsChanged`

Data flow
- `MangaPageViewModel.EnsureThumbnailAsync(...)` checks `ThumbnailCacheService` first.
- On a miss, it requests a low-resolution thumbnail, optionally upgrades to a higher-resolution thumbnail after a short delay, and updates the UI on the dispatcher.
- `ThumbnailDecodeScheduler` keeps the queue centered around the current reader position and trims far-away work to reduce scroll hitching.
- Bookmark thumbnails use a dedicated higher-priority path so the bookmark pane populates quickly.

Change notes
- Keep concurrency low enough to avoid starving the UI thread; the scheduler currently defaults to a small CPU-based window.
- When changing decode width behavior, clear cached thumbnails so old and new sizes do not mix.
- `ManagedThumbnailProvider` currently caches path-based thumbnails directly in `ThumbnailCacheService`; byte-based thumbnails are produced on demand.
- If a new provider is introduced, keep the `DispatcherQueue` contract intact because WinUI image objects still require UI-thread creation.
