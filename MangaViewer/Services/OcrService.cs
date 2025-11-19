using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MangaViewer.ViewModels;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using MangaViewer.Services.Logging;

namespace MangaViewer.Services
{
    public interface IOcrEngineFacade
    {
        Task<Windows.Media.Ocr.OcrResult?> RecognizeAsync(SoftwareBitmap bitmap, CancellationToken token);
    }
    internal sealed class WinRtOcrEngineFacade : IOcrEngineFacade
    {
        private readonly OcrEngine? _engine;
        public WinRtOcrEngineFacade(OcrEngine? engine) => _engine = engine;
        public async Task<Windows.Media.Ocr.OcrResult?> RecognizeAsync(SoftwareBitmap bitmap, CancellationToken token)
        {
            if (_engine == null) return null;
            token.ThrowIfCancellationRequested();
            return await _engine.RecognizeAsync(bitmap).AsTask(token);
        }
    }

    public class OcrResult
    {
        public string? Text { get; set; }
        public Windows.Foundation.Rect BoundingBox { get; set; }
    }

    /// <summary>
    /// OcrService
    /// Purpose: Provide OCR processing for images with configurable language, grouping, and paragraph detection heuristics.
    /// Features:
    ///  - Language selection (auto or explicit tag) with engine caching per language tag.
    ///  - Grouping modes: Word, Line, Paragraph (paragraph uses gap heuristics for vertical/horizontal layouts).
    ///  - Paragraph grouping configurable gap factors for vertical/horizontal detection.
    ///  - Debounced SettingsChanged event to avoid excessive reprocessing on rapid UI adjustments.
    /// Caching: Results keyed by path + current OCR settings; invalidated whenever settings change.
    /// Threading: Decoding & OCR executed asynchronously; UI thread only needed for event invocation (SynchronizationContext captured).
    /// Robustness: Swallows decoding/engine failures; returns empty list on errors.
    /// Extension Ideas:
    ///  - Add multi-language auto-detection on a per-image basis.
    ///  - Persist cache to disk across launches for large galleries.
    ///  - Provide bounding box confidence scores (if underlying API exposes them).
    /// </summary>
    public class OcrService
    {
        private static readonly Lazy<OcrService> _instance = new(() => new OcrService());
        public static OcrService Instance => _instance.Value;

        public enum OcrGrouping { Word = 0, Line = 1, Paragraph = 2 }
        public enum WritingMode { Auto = 0, Horizontal = 1, Vertical = 2 }

        public event EventHandler? SettingsChanged;
        public string CurrentLanguage { get; private set; } = "auto";
        public OcrGrouping GroupingMode { get; private set; } = OcrGrouping.Word;
        public WritingMode TextWritingMode { get; private set; } = WritingMode.Auto;
        public double ParagraphGapFactorVertical { get; private set; } = 1.50;
        public double ParagraphGapFactorHorizontal { get; private set; } = 1.25;

        private readonly Dictionary<string, OcrEngine?> _engineCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<BoundingBoxViewModel>> _ocrCache = new(StringComparer.OrdinalIgnoreCase);
        private EventHandler? _settingsChangedDebounced;
        private readonly SynchronizationContext? _syncContext;
        private TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(250);

        private readonly IImageDecoder _decoder = new WinRtImageDecoder();

        private OcrService()
        {
            _engineCache["auto"] = TryCreateEngineForLanguage("auto");
            _syncContext = SynchronizationContext.Current;
            RebuildDebouncedHandler();
        }

        /// <summary>
        /// Set the language for OCR processing.
        /// </summary>
        public void SetLanguage(string languageTag)
        {
            languageTag ??= "auto";
            if (!string.Equals(CurrentLanguage, languageTag, StringComparison.OrdinalIgnoreCase))
            {
                CurrentLanguage = languageTag;
                if (!_engineCache.ContainsKey(languageTag))
                    _engineCache[languageTag] = TryCreateEngineForLanguage(languageTag);
                OnSettingsChanged();
            }
        }
        /// <summary>
        /// Set the grouping mode for OCR results.
        /// </summary>
        public void SetGrouping(OcrGrouping grouping)
        {
            if (GroupingMode != grouping)
            {
                GroupingMode = grouping;
                OnSettingsChanged();
            }
        }
        /// <summary>
        /// Set the text writing mode (horizontal/vertical) for OCR processing.
        /// </summary>
        public void SetWritingMode(WritingMode mode)
        {
            if (TextWritingMode != mode)
            {
                TextWritingMode = mode;
                OnSettingsChanged();
            }
        }
        /// <summary>
        /// Set the paragraph gap factor for vertical paragraph detection.
        /// </summary>
        public void SetParagraphGapFactorVertical(double value)
        {
            value = Math.Clamp(value, 0.05, 10.0);
            if (Math.Abs(ParagraphGapFactorVertical - value) > 0.0001)
            {
                ParagraphGapFactorVertical = value;
                OnSettingsChanged();
            }
        }
        /// <summary>
        /// Set the paragraph gap factor for horizontal paragraph detection.
        /// </summary>
        public void SetParagraphGapFactorHorizontal(double value)
        {
            value = Math.Clamp(value, 0.05, 10.0);
            if (Math.Abs(ParagraphGapFactorHorizontal - value) > 0.0001)
            {
                ParagraphGapFactorHorizontal = value;
                OnSettingsChanged();
            }
        }

        /// <summary>
        /// Clear the cached OCR results.
        /// </summary>
        public void ClearCache() => _ocrCache.Clear();

        private void OnSettingsChanged()
        {
            ClearCache();
            try { _settingsChangedDebounced?.Invoke(this, EventArgs.Empty); } catch (Exception ex) { Log.Error(ex, "SettingsChanged debounced dispatch failed"); }
        }

        /// <summary>
        /// Set the debounce delay for the SettingsChanged event.
        /// </summary>
        public void SetSettingsChangedDebounce(TimeSpan delay)
        {
            _debounceDelay = delay;
            RebuildDebouncedHandler();
        }

        private void RebuildDebouncedHandler() => _settingsChangedDebounced = DebounceOnContext((s, e) => SettingsChanged?.Invoke(s, e), _debounceDelay, _syncContext);

        /// <summary>
        /// Build debounced EventHandler using PeriodicTimer; schedules single invocation after silence period.
        /// </summary>
        public static EventHandler DebounceOnContext(EventHandler handler, TimeSpan delay, SynchronizationContext? context)
        {
            object gate = new();
            CancellationTokenSource? cts = null;
            PeriodicTimer? timer = null;
            return (s, e) =>
            {
                lock (gate)
                {
                    cts?.Cancel();
                    cts?.Dispose();
                    cts = new CancellationTokenSource();
                    timer?.Dispose();
                    timer = new PeriodicTimer(delay);
                    var localCts = cts;
                    var localTimer = timer;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (localTimer == null || localCts == null) return;
                            // Wait one tick then invoke
                            if (await localTimer.WaitForNextTickAsync(localCts.Token))
                            {
                                if (context != null)
                                    context.Post(_ => { try { handler(s, e); } catch { } }, null);
                                else
                                    try { handler(s, e); } catch { }
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { Log.Error(ex, "DebounceOnContext handler failed"); }
                        finally
                        {
                            localTimer?.Dispose();
                        }
                    });
                }
            };
        }

        private OcrEngine? TryCreateEngineForLanguage(string tag)
        {
            try
            {
                if (string.Equals(tag, "auto", StringComparison.OrdinalIgnoreCase))
                    return OcrEngine.TryCreateFromUserProfileLanguages();
                var lang = new Windows.Globalization.Language(tag);
                return OcrEngine.TryCreateFromLanguage(lang);
            }
            catch { return null; }
        }

        private OcrEngine? GetActiveEngine()
        {
            if (_engineCache.TryGetValue(CurrentLanguage, out var e)) return e;
            e = TryCreateEngineForLanguage(CurrentLanguage);
            _engineCache[CurrentLanguage] = e;
            return e;
        }

        /// <summary>
        /// Legacy synchronous recognition (no internal caching). Provided for direct StorageFile usage.
        /// </summary>
        public async Task<List<OcrResult>> RecognizeAsync(StorageFile imageFile)
        {
            var engine = GetActiveEngine();
            if (engine == null)
            {
                return new List<OcrResult>();
            }

            try
            {
                using (IRandomAccessStream stream = await imageFile.OpenAsync(FileAccessMode.Read))
                {
                    var decoder = await BitmapDecoder.CreateAsync(stream);

                    var transform = new BitmapTransform();
                    uint width = decoder.PixelWidth;
                    uint height = decoder.PixelHeight;
                    const uint MaxDim = 4000;
                    if (width > MaxDim || height > MaxDim)
                    {
                        double scale = width > height ? (double)MaxDim / width : (double)MaxDim / height;
                        transform.ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale));
                        transform.ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale));
                    }

                    var toOcr = await DecodeForOcrAsync(decoder, transform);

                    var ocr = await engine.RecognizeAsync(toOcr);

                    var results = new List<OcrResult>(capacity: Math.Max(8, ocr.Lines.Count * 4));
                    foreach (var line in ocr.Lines)
                    {
                        foreach (var word in line.Words)
                        {
                            results.Add(new OcrResult
                            {
                                Text = word.Text,
                                BoundingBox = word.BoundingRect
                            });
                        }
                    }
                    return results;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[OCR] Failed for file '{imageFile.Path}'");
                return new List<OcrResult>();
            }
        }

        /// <summary>
        /// Main cached OCR entry; returns bounding boxes adapted to grouping & writing mode settings.
        /// </summary>
        public async Task<List<BoundingBoxViewModel>> GetOcrAsync(string imagePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return new List<BoundingBoxViewModel>();

            string cacheKey = BuildCacheKey(imagePath);
            if (_ocrCache.TryGetValue(cacheKey, out var cached))
                return new List<BoundingBoxViewModel>(cached);

            var engine = GetActiveEngine();
            IOcrEngineFacade facade = new WinRtOcrEngineFacade(engine);
            if (engine == null) return new List<BoundingBoxViewModel>();

            try
            {
                var toOcr = await _decoder.DecodeForOcrAsync(imagePath, cancellationToken);
                if (toOcr == null) return new List<BoundingBoxViewModel>();
                int imgW = toOcr.PixelWidth;
                int imgH = toOcr.PixelHeight;
                var ocr = await facade.RecognizeAsync(toOcr, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (ocr == null) return new List<BoundingBoxViewModel>();

                var wordBoxes = new List<(string Text, Rect Rect, int LineIndex)>(ocr.Lines.Sum(l => l.Words.Count));
                var lineRects = new List<Rect>(ocr.Lines.Count);
                for (int li = 0; li < ocr.Lines.Count; li++)
                {
                    var line = ocr.Lines[li];
                    Rect? lineRect = null;
                    foreach (var w in line.Words)
                    {
                        var rect = w.BoundingRect;
                        wordBoxes.Add((w.Text, rect, li));
                        lineRect = lineRect.HasValue ? Union(lineRect.Value, rect) : rect;
                    }
                    lineRects.Add(lineRect ?? new Rect());
                }

                var resultBoxes = new List<BoundingBoxViewModel>();
                switch (GroupingMode)
                {
                    case OcrGrouping.Word:
                        foreach (var w in wordBoxes)
                        {
                            if (IsEmpty(w.Rect)) continue;
                            resultBoxes.Add(new BoundingBoxViewModel(w.Text, w.Rect, imgW, imgH));
                        }
                        break;
                    case OcrGrouping.Line:
                        for (int i = 0; i < lineRects.Count; i++)
                        {
                            var r = lineRects[i];
                            if (IsEmpty(r)) continue;
                            string text = string.Join(' ', ocr.Lines[i].Words.Select(x => x.Text));
                            resultBoxes.Add(new BoundingBoxViewModel(text, r, imgW, imgH));
                        }
                        break;
                    case OcrGrouping.Paragraph:
                        foreach (var para in GroupParagraphs(lineRects, ocr, imgW, imgH))
                            resultBoxes.Add(para);
                        break;
                }

                _ocrCache[cacheKey] = new List<BoundingBoxViewModel>(resultBoxes);
                return resultBoxes;
            }
            catch (OperationCanceledException)
            {
                return new List<BoundingBoxViewModel>();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[OCR] GetOcrAsync error");
                return new List<BoundingBoxViewModel>();
            }
        }

        private string BuildCacheKey(string path) => $"{path}|lang={CurrentLanguage}|grp={(int)GroupingMode}|wm={(int)TextWritingMode}|gv={ParagraphGapFactorVertical:F2}|gh={ParagraphGapFactorHorizontal:F2}";

        private static Rect Union(Rect a, Rect b)
        {
            if (IsEmpty(a)) return b;
            if (IsEmpty(b)) return a;
            double x1 = Math.Min(a.X, b.X);
            double y1 = Math.Min(a.Y, b.Y);
            double x2 = Math.Max(a.X + a.Width, b.X + b.Width);
            double y2 = Math.Max(a.Y + a.Height, b.Y + b.Height);
            return new Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
        }
        private static bool IsEmpty(Rect r) => r.Width <= 0 || r.Height <= 0;

        private IEnumerable<BoundingBoxViewModel> GroupParagraphs(List<Rect> lineRects, Windows.Media.Ocr.OcrResult ocr, int imgW, int imgH)
        {
            var results = new List<BoundingBoxViewModel>();
            if (lineRects.Count == 0) return results;

            bool vertical = TextWritingMode == WritingMode.Vertical;
            if (TextWritingMode == WritingMode.Auto)
            {
                double avgW = lineRects.Where(r => !IsEmpty(r)).DefaultIfEmpty(new Rect()).Average(r => r.Width);
                double avgH = lineRects.Where(r => !IsEmpty(r)).DefaultIfEmpty(new Rect()).Average(r => r.Height);
                vertical = avgH > avgW * 1.5;
            }

            var indices = Enumerable.Range(0, lineRects.Count).Where(i => !IsEmpty(lineRects[i])).ToList();
            if (!indices.Any()) return results;

            if (!vertical)
            {
                indices.Sort((a, b) => lineRects[a].Y != lineRects[b].Y ? lineRects[a].Y.CompareTo(lineRects[b].Y) : lineRects[a].X.CompareTo(lineRects[b].X));
                double avgHeight = indices.Average(i => lineRects[i].Height);
                double threshold = avgHeight * ParagraphGapFactorHorizontal;
                var current = new List<int> { indices[0] };
                for (int k = 1; k < indices.Count; k++)
                {
                    var prev = lineRects[indices[k - 1]];
                    var cur = lineRects[indices[k]];
                    double gap = cur.Y - (prev.Y + prev.Height);
                    if (gap <= threshold)
                        current.Add(indices[k]);
                    else
                    {
                        results.Add(BuildParagraph(current, lineRects, ocr, imgW, imgH));
                        current.Clear(); current.Add(indices[k]);
                    }
                }
                results.Add(BuildParagraph(current, lineRects, ocr, imgW, imgH));
            }
            else
            {
                indices.Sort((a, b) => lineRects[a].X != lineRects[b].X ? lineRects[b].X.CompareTo(lineRects[a].X) : lineRects[a].Y.CompareTo(lineRects[b].Y));
                double avgWidth = indices.Average(i => lineRects[i].Width);
                double threshold = avgWidth * ParagraphGapFactorVertical;
                var current = new List<int> { indices[0] };
                for (int k = 1; k < indices.Count; k++)
                {
                    var prev = lineRects[indices[k - 1]];
                    var cur = lineRects[indices[k]];
                    double gap = prev.X - (cur.X + cur.Width);
                    if (gap <= threshold)
                        current.Add(indices[k]);
                    else
                    {
                        results.Add(BuildParagraph(current, lineRects, ocr, imgW, imgH));
                        current.Clear(); current.Add(indices[k]);
                    }
                }
                results.Add(BuildParagraph(current, lineRects, ocr, imgW, imgH));
            }

            return results;
        }

        private BoundingBoxViewModel BuildParagraph(List<int> lineIndexes, List<Rect> lineRects, Windows.Media.Ocr.OcrResult ocr, int imgW, int imgH)
        {
            Rect rect = new Rect();
            bool first = true;
            var parts = new List<string>();
            foreach (int i in lineIndexes)
            {
                rect = first ? lineRects[i] : Union(rect, lineRects[i]);
                first = false;
                string text = string.Join(' ', ocr.Lines[i].Words.Select(w => w.Text));
                parts.Add(text);
            }
            string paraText = string.Join(Environment.NewLine, parts);
            return new BoundingBoxViewModel(paraText, rect, imgW, imgH);
        }

        private static async Task<IRandomAccessStream?> OpenAsRandomAccessStreamAsync(string path, CancellationToken token)
        {
            try
            {
                if (path.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
                {
                    if (ImageCacheService.Instance.TryGetMemoryImageBytes(path, out var bytes) && bytes != null)
                    {
                        var ms = new MemoryStream(bytes, writable: false);
                        return ms.AsRandomAccessStream();
                    }
                    return null;
                }

                return await Windows.Storage.Streams.FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read).AsTask(token);
            }
            catch { return null; }
        }

        private static async Task<SoftwareBitmap> DecodeForOcrAsync(BitmapDecoder decoder, BitmapTransform transform)
        {
            SoftwareBitmap bitmap;
            try
            {
                bitmap = await decoder.GetSoftwareBitmapAsync(decoder.BitmapPixelFormat, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            }
            catch
            {
                try
                {
                    bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
                }
                catch
                {
                    bitmap = await decoder.GetSoftwareBitmapAsync();
                }
            }

            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Gray8 && bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                try { bitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8); } catch { }
            }
            return bitmap;
        }
    }
}
