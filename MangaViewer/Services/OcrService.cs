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

namespace MangaViewer.Services
{
    public class OcrResult
    {
        public string? Text { get; set; }
        public Windows.Foundation.Rect BoundingBox { get; set; }
    }

    public class OcrService
    {
        // Singleton
        private static readonly Lazy<OcrService> _instance = new(() => new OcrService());
        public static OcrService Instance => _instance.Value;

        // Settings enums (nested to match call sites like OcrService.OcrGrouping)
        public enum OcrGrouping { Word = 0, Line = 1, Paragraph = 2 }
        public enum WritingMode { Auto = 0, Horizontal = 1, Vertical = 2 }

        // Settings + event
        public event EventHandler? SettingsChanged;
        public string CurrentLanguage { get; private set; } = "auto"; // "auto", "ja", "ko", "en", ...
        public OcrGrouping GroupingMode { get; private set; } = OcrGrouping.Word;
        public WritingMode TextWritingMode { get; private set; } = WritingMode.Auto;
        public double ParagraphGapFactorVertical { get; private set; } = 1.50;   // heuristic
        public double ParagraphGapFactorHorizontal { get; private set; } = 1.25; // heuristic

        // OCR engine cache per language code ("auto" uses user profile)
        private readonly Dictionary<string, OcrEngine?> _engineCache = new(StringComparer.OrdinalIgnoreCase);

        // Simple OCR results cache (per-path + settings key)
        private readonly Dictionary<string, List<BoundingBoxViewModel>> _ocrCache = new(StringComparer.OrdinalIgnoreCase);

        private OcrService()
        {
            // warm up default engine
            _engineCache["auto"] = TryCreateEngineForLanguage("auto");
        }

        public void SetLanguage(string languageTag)
        {
            languageTag ??= "auto";
            if (!string.Equals(CurrentLanguage, languageTag, StringComparison.OrdinalIgnoreCase))
            {
                CurrentLanguage = languageTag;
                // ensure engine available (lazy)
                if (!_engineCache.ContainsKey(languageTag))
                    _engineCache[languageTag] = TryCreateEngineForLanguage(languageTag);
                OnSettingsChanged();
            }
        }
        public void SetGrouping(OcrGrouping grouping)
        {
            if (GroupingMode != grouping)
            {
                GroupingMode = grouping;
                OnSettingsChanged();
            }
        }
        public void SetWritingMode(WritingMode mode)
        {
            if (TextWritingMode != mode)
            {
                TextWritingMode = mode;
                OnSettingsChanged();
            }
        }
        public void SetParagraphGapFactorVertical(double value)
        {
            value = Math.Clamp(value, 0.05, 10.0);
            if (Math.Abs(ParagraphGapFactorVertical - value) > 0.0001)
            {
                ParagraphGapFactorVertical = value;
                OnSettingsChanged();
            }
        }
        public void SetParagraphGapFactorHorizontal(double value)
        {
            value = Math.Clamp(value, 0.05, 10.0);
            if (Math.Abs(ParagraphGapFactorHorizontal - value) > 0.0001)
            {
                ParagraphGapFactorHorizontal = value;
                OnSettingsChanged();
            }
        }

        public void ClearCache()
        {
            _ocrCache.Clear();
        }

        private void OnSettingsChanged()
        {
            // settings affect layout -> invalidate OCR cache
            ClearCache();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        private OcrEngine? TryCreateEngineForLanguage(string tag)
        {
            try
            {
                if (string.Equals(tag, "auto", StringComparison.OrdinalIgnoreCase))
                    return OcrEngine.TryCreateFromUserProfileLanguages();
                var lang = new Language(tag);
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
        /// 이미지 파일에서 OCR 수행. 내부적으로 지원 포맷(Gray8)으로 변환 및 EXIF Orientation 적용.
        /// 매우 큰 이미지는 최대 한 변 4000px 로 스케일 다운하여 성능/메모리 사용을 줄입니다.
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

                    // 스케일 결정 (너비/높이 중 큰 값이 4000 초과면 축소)
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

                    // Decode to BGRA8 to maximize decoder support, then convert to Gray8 if possible
                    using var oriented = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        transform,
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.DoNotColorManage);

                    SoftwareBitmap toOcr;
                    try { toOcr = SoftwareBitmap.Convert(oriented, BitmapPixelFormat.Gray8); }
                    catch { toOcr = oriented; }

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
                Debug.WriteLine($"[OCR] Failed for file '{imageFile.Path}': {ex}");
                return new List<OcrResult>();
            }
        }

        // New API used by MangaViewModel
        public async Task<List<BoundingBoxViewModel>> GetOcrAsync(string imagePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return new List<BoundingBoxViewModel>();

            string cacheKey = BuildCacheKey(imagePath);
            if (_ocrCache.TryGetValue(cacheKey, out var cached))
                return new List<BoundingBoxViewModel>(cached);

            var engine = GetActiveEngine();
            if (engine == null) return new List<BoundingBoxViewModel>();

            try
            {
                using var rs = await OpenAsRandomAccessStreamAsync(imagePath, cancellationToken);
                if (rs == null) return new List<BoundingBoxViewModel>();

                var decoder = await BitmapDecoder.CreateAsync(rs);
                cancellationToken.ThrowIfCancellationRequested();

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

                using var oriented = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                SoftwareBitmap toOcr;
                try { toOcr = SoftwareBitmap.Convert(oriented, BitmapPixelFormat.Gray8); }
                catch { toOcr = oriented; }

                cancellationToken.ThrowIfCancellationRequested();
                var ocr = await engine.RecognizeAsync(toOcr);
                cancellationToken.ThrowIfCancellationRequested();

                int imgW = toOcr.PixelWidth;
                int imgH = toOcr.PixelHeight;

                // Collect base words and per-line rects
                var wordBoxes = new List<(string Text, Rect Rect, int LineIndex)>();
                var lineRects = new List<Rect>();
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
                Debug.WriteLine($"[OCR] GetOcrAsync error: {ex.Message}\n\n{ex} ");
                return new List<BoundingBoxViewModel>();
            }
        }

        private string BuildCacheKey(string path)
        {
            // Include major settings that affect grouping layout
            return $"{path}|lang={CurrentLanguage}|grp={(int)GroupingMode}|wm={(int)TextWritingMode}|gv={ParagraphGapFactorVertical:F2}|gh={ParagraphGapFactorHorizontal:F2}";
        }

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
                // simple heuristic: if average width < height -> horizontal text
                double avgW = lineRects.Where(r => !IsEmpty(r)).DefaultIfEmpty(new Rect()).Average(r => r.Width);
                double avgH = lineRects.Where(r => !IsEmpty(r)).DefaultIfEmpty(new Rect()).Average(r => r.Height);
                vertical = avgH > avgW * 1.5; // crude guess
            }

            var indices = Enumerable.Range(0, lineRects.Count).Where(i => !IsEmpty(lineRects[i])).ToList();
            if (!indices.Any()) return results;

            if (!vertical)
            {
                // horizontal paragraph grouping by Y gaps
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
                // vertical paragraph grouping by X gaps (columns)
                indices.Sort((a, b) => lineRects[a].X != lineRects[b].X ? lineRects[b].X.CompareTo(lineRects[a].X) : lineRects[a].Y.CompareTo(lineRects[b].Y)); // right->left then top->bottom
                double avgWidth = indices.Average(i => lineRects[i].Width);
                double threshold = avgWidth * ParagraphGapFactorVertical;
                var current = new List<int> { indices[0] };
                for (int k = 1; k < indices.Count; k++)
                {
                    var prev = lineRects[indices[k - 1]];
                    var cur = lineRects[indices[k]];
                    double gap = prev.X - (cur.X + cur.Width); // distance between columns (right-to-left)
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
                // Support in-memory images (mem: keys)
                if (path.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
                {
                    if (ImageCacheService.Instance.TryGetMemoryImageBytes(path, out var bytes) && bytes != null)
                    {
                        var ms = new MemoryStream(bytes, writable: false);
                        return ms.AsRandomAccessStream();
                    }
                    return null;
                }

                // Disk file path -> read into memory stream for RandomAccessStream interop
                byte[] data = await File.ReadAllBytesAsync(path, token);
                var mem = new MemoryStream(data, writable: false);
                return mem.AsRandomAccessStream();
            }
            catch { return null; }
        }
    }
}
