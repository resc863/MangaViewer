using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using MangaViewer.ViewModels;

namespace MangaViewer.Services
{
    /// <summary>
    /// OCR 서비스: 이미지 파일 / 인메모리(mem:) 이미지 OCR + 결과 캐시 + 그룹핑. (디스크 임시 저장 미사용)
    /// </summary>
    public sealed class OcrService
    {
        public enum OcrGrouping { Word = 0, Line = 1, Paragraph = 2 }
        public enum WritingMode { Auto = 0, Horizontal = 1, Vertical = 2 }

        public static OcrService Instance { get; } = new();

        // Diagnostics toggle
        public static bool DiagnosticVerbose { get; set; } = true;

        private OcrEngine? _ocrEngine; // mutable (recreated on language change)
        private readonly ConcurrentDictionary<string, List<BoundingBoxViewModel>> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task<List<BoundingBoxViewModel>>> _inflight = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _gate = new(1); // 엔진 단일 접근

        private OcrGrouping _grouping = OcrGrouping.Word;
        private WritingMode _writingMode = WritingMode.Auto;

        public double ParagraphGapFactorVertical { get; private set; } = 0.8;
        public double ParagraphGapFactorHorizontal { get; private set; } = 0.8;
        public OcrGrouping GroupingMode => _grouping;
        public WritingMode TextWritingMode => _writingMode;
        public string CurrentLanguage { get; private set; } = "auto"; // "auto" = user profile languages

        public event EventHandler? SettingsChanged;

        private OcrService() => _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();

        #region Configuration
        public void SetLanguage(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return;
            if (string.Equals(CurrentLanguage, lang, StringComparison.OrdinalIgnoreCase)) return;
            CurrentLanguage = lang;
            try
            {
                OcrEngine? newEngine = null;
                if (!string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var language = new Windows.Globalization.Language(lang);
                        newEngine = OcrEngine.TryCreateFromLanguage(language);
                        if (DiagnosticVerbose) Debug.WriteLine($"[OCR][LANG] Create engine lang={lang} success?={(newEngine!=null)}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OCR][LANG] CreateFromLanguage fail lang={lang} ex={ex.Message}");
                    }
                }
                _ocrEngine = newEngine ?? OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR][LANG] unexpected error: {ex.Message}");
            }
            ClearCache();
            OnSettingsChanged();
        }
        public void SetGrouping(OcrGrouping grouping)
        {
            if (_grouping == grouping) return;
            _grouping = grouping;
            ClearCache();
            OnSettingsChanged();
        }
        public void SetWritingMode(WritingMode mode)
        {
            if (_writingMode == mode) return;
            _writingMode = mode;
            ClearCache();
            OnSettingsChanged();
        }
        public void SetParagraphGapFactorVertical(double v)
        {
            double nv = Math.Clamp(v, 0.1, 3.0);
            if (Math.Abs(nv - ParagraphGapFactorVertical) < 0.0001) return;
            ParagraphGapFactorVertical = nv;
            if (_grouping == OcrGrouping.Paragraph) { ClearCache(); OnSettingsChanged(); }
        }
        public void SetParagraphGapFactorHorizontal(double v)
        {
            double nv = Math.Clamp(v, 0.1, 3.0);
            if (Math.Abs(nv - ParagraphGapFactorHorizontal) < 0.0001) return;
            ParagraphGapFactorHorizontal = nv;
            if (_grouping == OcrGrouping.Paragraph) { ClearCache(); OnSettingsChanged(); }
        }
        public void ClearCache()
        {
            _cache.Clear();
            _inflight.Clear();
        }
        private void OnSettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);
        #endregion

        /// <summary>OCR 실행 (mem: 지원, 디스크 임시 파일 금지)</summary>
        public Task<List<BoundingBoxViewModel>> GetOcrAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(path) || _ocrEngine == null)
                return Task.FromResult(new List<BoundingBoxViewModel>());
            if (_cache.TryGetValue(path, out var cached)) return Task.FromResult(cached);
            return _inflight.GetOrAdd(path, _ => RunOcrInternalAsync(path, ct));
        }

        private async Task<List<BoundingBoxViewModel>> RunOcrInternalAsync(string path, CancellationToken ct)
        {
            var swTotal = Stopwatch.StartNew();
            try
            {
                ct.ThrowIfCancellationRequested();
                IRandomAccessStream? stream = null;
                SoftwareBitmap? decoded = null;
                SoftwareBitmap? working = null;
                try
                {
                    if (DiagnosticVerbose) Debug.WriteLine($"[OCR][START] {path}");
                    stream = await OpenImageStreamAsync(path, ct);
                    if (stream == null) { if (DiagnosticVerbose) Debug.WriteLine($"[OCR][NO-DATA] {path}"); return new List<BoundingBoxViewModel>(); }
                    if (DiagnosticVerbose) Debug.WriteLine($"[OCR][STREAM] {path} size={stream.Size}");
                    var decSw = Stopwatch.StartNew();
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    if (DiagnosticVerbose) Debug.WriteLine($"[OCR][DECODER] {path} fmt={decoder.BitmapPixelFormat} w={decoder.PixelWidth} h={decoder.PixelHeight}");
                    var transform = CreateScaleTransform(decoder.PixelWidth, decoder.PixelHeight);
                    decoded = await GetSupportedBitmapAsync(decoder, transform, ct);
                    if (DiagnosticVerbose) Debug.WriteLine($"[OCR][DECODED] {path} fmt={decoded.BitmapPixelFormat} w={decoded.PixelWidth} h={decoded.PixelHeight} durMs={decSw.ElapsedMilliseconds}");
                    working = SoftwareBitmap.Copy(decoded);
                    if (DiagnosticVerbose) Debug.WriteLine($"[OCR][CLONE] {path} cloned fmt={working.BitmapPixelFormat} w={working.PixelWidth} h={working.PixelHeight}");
                }
                finally
                {
                    decoded?.Dispose(); // Keep working only
                    stream?.Dispose();
                }

                if (working == null) return new List<BoundingBoxViewModel>();

                Windows.Media.Ocr.OcrResult ocrRaw;
                var swOcr = Stopwatch.StartNew();
                await _gate.WaitAsync(ct);
                try
                {
                    if (DiagnosticVerbose) Debug.WriteLine($"[OCR][CALL] {path} start");
                    ocrRaw = await _ocrEngine!.RecognizeAsync(working);
                    if (DiagnosticVerbose) Debug.WriteLine($"[OCR][CALL-END] {path} durMs={swOcr.ElapsedMilliseconds}");
                }
                catch (ObjectDisposedException ode)
                {
                    Debug.WriteLine($"[OCR][DISPOSED] During RecognizeAsync path={path} msg={ode.Message} working.IsDisposed?={IsBitmapDisposed(working)}");
                    throw;
                }
                finally
                {
                    _gate.Release();
                }

                int pixelW = (int)working.PixelWidth;
                int pixelH = (int)working.PixelHeight;
                var buildSw = Stopwatch.StartNew();
                var list = _grouping switch
                {
                    OcrGrouping.Line => BuildLineGroups(ocrRaw, pixelW, pixelH),
                    OcrGrouping.Paragraph => BuildParagraphGroups(ocrRaw, pixelW, pixelH),
                    _ => BuildWordGroups(ocrRaw, pixelW, pixelH)
                };
                if (DiagnosticVerbose) Debug.WriteLine($"[OCR][BUILT] {path} boxes={list.Count} durMs={buildSw.ElapsedMilliseconds} totalMs={swTotal.ElapsedMilliseconds}");
                _cache[path] = list;
                return list;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR][FAIL] {path} ex={ex.GetType().Name} msg={ex.Message}\n{ex}");
                return new List<BoundingBoxViewModel>();
            }
            finally
            {
                _inflight.TryRemove(path, out _);
                if (DiagnosticVerbose) Debug.WriteLine($"[OCR][END] {path} totalMs={swTotal.ElapsedMilliseconds}");
            }
        }

        private static bool IsBitmapDisposed(SoftwareBitmap bmp)
        {
            try
            {
                _ = bmp.PixelWidth; // access property triggers ObjectDisposedException if disposed
                return false;
            }
            catch (ObjectDisposedException) { return true; }
            catch { return false; }
        }

        private static async Task<IRandomAccessStream?> OpenImageStreamAsync(string path, CancellationToken ct)
        {
            if (path.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
            {
                if (!ImageCacheService.Instance.TryGetMemoryImageBytes(path, out var data) || data == null || data.Length == 0)
                    return null;
                var mem = new InMemoryRandomAccessStream();
                // NOTE: DataWriter.Dispose will also dispose underlying stream if not detached.
                var writer = new DataWriter(mem);
                writer.WriteBytes(data);
                await writer.StoreAsync().AsTask(ct);
                await writer.FlushAsync().AsTask(ct);
                writer.DetachStream(); // keep mem alive
                writer.Dispose(); // safe: stream detached
                mem.Seek(0);
                if (DiagnosticVerbose) Debug.WriteLine($"[OCR][MEM] {path} bytes={data.Length}");
                return mem;
            }
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            return await file.OpenAsync(FileAccessMode.Read).AsTask(ct);
        }

        private static BitmapTransform CreateScaleTransform(uint width, uint height)
        {
            const uint MaxDim = 4000;
            if (width <= MaxDim && height <= MaxDim) return new BitmapTransform();
            double scale = width > height ? (double)MaxDim / width : (double)MaxDim / height;
            return new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale))
            };
        }

        private static async Task<SoftwareBitmap> GetSupportedBitmapAsync(BitmapDecoder decoder, BitmapTransform transform, CancellationToken ct)
        {
            bool sourceRgbaLike = decoder.BitmapPixelFormat == BitmapPixelFormat.Rgba8 || decoder.BitmapPixelFormat == BitmapPixelFormat.Bgra8;

            // Prefer BGRA8 first for WebP / RGBA sources to avoid unsupported Gray8 direct decode exceptions
            if (sourceRgbaLike)
            {
                try
                {
                    var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
                    if (sb != null) return sb;
                }
                catch (Exception ex) { Debug.WriteLine("[OCR] Bgra8 primary decode failed: " + ex.Message); }
                // Fallback attempt Gray8
                try
                {
                    var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
                    if (sb != null) return sb;
                }
                catch (Exception ex) { Debug.WriteLine("[OCR] Gray8 secondary decode failed: " + ex.Message); }
            }
            else
            {
                // Non‑RGBA sources: try Gray8 first (often cheapest), then BGRA8
                try
                {
                    var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
                    if (sb != null) return sb;
                }
                catch (Exception ex) { Debug.WriteLine("[OCR] Gray8 decode fallback: " + ex.Message); }
                try
                {
                    var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
                    if (sb != null) return sb;
                }
                catch (Exception ex) { Debug.WriteLine("[OCR] Bgra8 decode fallback: " + ex.Message); }
            }
            // Last resort: original
            var original = await decoder.GetSoftwareBitmapAsync().AsTask(ct);
            return original.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? original
                : SoftwareBitmap.Convert(original, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        #region Group Builders
        private static List<BoundingBoxViewModel> BuildWordGroups(Windows.Media.Ocr.OcrResult ocrRaw, int w, int h)
        {
            var list = new List<BoundingBoxViewModel>(Math.Max(8, ocrRaw.Lines.Count * 4));
            foreach (var line in ocrRaw.Lines)
                foreach (var word in line.Words)
                    list.Add(new BoundingBoxViewModel(word.Text, word.BoundingRect, w, h));
            return list;
        }
        private static List<BoundingBoxViewModel> BuildLineGroups(Windows.Media.Ocr.OcrResult ocrRaw, int w, int h)
        {
            var list = new List<BoundingBoxViewModel>(ocrRaw.Lines.Count);
            foreach (var line in ocrRaw.Lines)
            {
                if (line.Words.Count == 0) continue;
                var union = line.Words[0].BoundingRect;
                string text = line.Words[0].Text;
                for (int i = 1; i < line.Words.Count; i++)
                {
                    var wd = line.Words[i];
                    union = Union(union, wd.BoundingRect);
                    text += " " + wd.Text;
                }
                list.Add(new BoundingBoxViewModel(text, union, w, h));
            }
            return list;
        }
        private List<BoundingBoxViewModel> BuildParagraphGroups(Windows.Media.Ocr.OcrResult ocrRaw, int w, int h)
        {
            var lines = new List<(Windows.Foundation.Rect Rect, string Text)>();
            foreach (var line in ocrRaw.Lines)
            {
                if (line.Words.Count == 0) continue;
                var rect = line.Words[0].BoundingRect;
                string text = line.Words[0].Text;
                for (int i = 1; i < line.Words.Count; i++)
                {
                    rect = Union(rect, line.Words[i].BoundingRect);
                    text += " " + line.Words[i].Text;
                }
                lines.Add((rect, text));
            }
            if (lines.Count == 0) return new();
            bool vertical = ResolveVertical();
            double gapFactor = vertical ? ParagraphGapFactorVertical : ParagraphGapFactorHorizontal;
            lines = vertical ? lines.OrderBy(l => l.Rect.X).ToList() : lines.OrderBy(l => l.Rect.Y).ToList();
            double avgSpan = lines.Average(l => vertical ? l.Rect.Width : l.Rect.Height);
            double gapThreshold = avgSpan * gapFactor;
            var result = new List<BoundingBoxViewModel>(lines.Count / 2 + 1);
            var currentRect = lines[0].Rect;
            var currentText = lines[0].Text;
            for (int i = 1; i < lines.Count; i++)
            {
                double gap = vertical ? lines[i].Rect.X - currentRect.X - currentRect.Width
                                      : lines[i].Rect.Y - currentRect.Y - currentRect.Height;
                if (gap > gapThreshold)
                {
                    result.Add(new BoundingBoxViewModel(currentText, currentRect, w, h));
                    currentRect = lines[i].Rect;
                    currentText = lines[i].Text;
                }
                else
                {
                    currentRect = Union(currentRect, lines[i].Rect);
                    currentText += (vertical ? string.Empty : "\n") + lines[i].Text;
                }
            }
            result.Add(new BoundingBoxViewModel(currentText, currentRect, w, h));
            return result;
        }
        #endregion

        private bool ResolveVertical() => _writingMode == WritingMode.Vertical;

        private static Windows.Foundation.Rect Union(Windows.Foundation.Rect a, Windows.Foundation.Rect b)
        {
            double x = Math.Min(a.X, b.X);
            double y = Math.Min(a.Y, b.Y);
            double r = Math.Max(a.X + a.Width, b.X + b.Width);
            double btm = Math.Max(a.Y + a.Height, b.Y + b.Height);
            return new Windows.Foundation.Rect(x, y, r - x, btm - y);
        }
    }
}
