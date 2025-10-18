# Thumbnails subsystem

Components
- `IThumbnailProvider`: abstraction for producing thumbnail images from a source path or memory key.
- `ManagedThumbnailProvider`: default implementation using `ImageCacheService` and decode hints from `ThumbnailOptions`.
- `ThumbnailCacheService`: caches generated thumbnail bitmaps.
- `ThumbnailDecodeScheduler`: prioritizes decode tasks around the `SelectedThumbnailIndex` to keep UI responsive.
- `ThumbnailProviderFactory`: returns provider instances based on configuration.
- `ThumbnailSettingsService`: persists thumbnail-related options.

Public API highlights
- `ThumbnailDecodeScheduler.Instance.UpdateSelectedIndex(int)`
- `ThumbnailCacheService.GetOrAddAsync(string path, Size desired)` (adjust to actual implementation)
- `IThumbnailProvider.GetAsync(string path, ThumbnailOptions options, CancellationToken)`

Data flow (text)
- Thumbnail list binding ¡æ scroll/selection changes ¡æ `UpdateSelectedIndex` reorders the decode queue ¡æ
  provider pulls from `ImageCacheService` or decodes bytes into a smaller bitmap ¡æ result stored in `ThumbnailCacheService` ¡æ UI updates.

Change notes
- Keep decode concurrency around 2?4 to avoid starving the UI thread.
- Avoid full-size decodes for small thumbnails; pass size hints via `ThumbnailOptions` where possible.
- Cancel/skip far-off items to save CPU/battery.
