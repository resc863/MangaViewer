using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using MangaViewer.ViewModels;

namespace MangaViewer.Services
{
    /// <summary>
    /// OCR service (singleton) supporting:
    ///  - Language selection (ja / ko / en)
    ///  - Grouping mode (Word / Line / Paragraph)
    ///  - Writing mode (Horizontal / Vertical / Auto heuristic)
    ///  - Large image downscale guard
    ///  - Simple grayscale pre-processing
    ///  - In-memory cache keyed by path+settings
    /// </summary>
    public sealed class OcrService
    {
        public enum OcrGrouping { Word, Line, Paragraph }
        public enum WritingMode { Auto, Horizontal, Vertical }

        private static readonly Lazy<OcrService> _instance = new(() => new OcrService());
        public static OcrService Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, (IReadOnlyList<BoundingBoxViewModel> Boxes, DateTime Timestamp)> _cache = new(StringComparer.OrdinalIgnoreCase);
        private OcrEngine _engine = null!;
        private string _currentLangTag = "ja";

        public static readonly string[] SupportedLanguages = { "ja", "ko", "en" };

        public OcrGrouping GroupingMode { get; private set; } = OcrGrouping.Line;
        public WritingMode TextWritingMode { get; private set; } = WritingMode.Auto;
        public string CurrentLanguage => _currentLangTag; // requested language tag

        // Separate paragraph gap factors for horizontal vs vertical (default: horizontal 0.5, vertical 0.35)
        private double _paragraphGapFactorHorizontal = 0.5;
        private double _paragraphGapFactorVertical = 0.35;
        public double ParagraphGapFactorHorizontal => _paragraphGapFactorHorizontal;
        public double ParagraphGapFactorVertical => _paragraphGapFactorVertical;

        private OcrService() => InitializeEngine(_currentLangTag);

        private void InitializeEngine(string langTag)
        {
            try
            {
                var lang = new Language(langTag);
                var eng = OcrEngine.TryCreateFromLanguage(lang);
                if (eng != null)
                {
                    _engine = eng;
                    _currentLangTag = langTag;
                    Debug.WriteLine($"[OCR] Engine initialized ({langTag})");
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] Engine init failed {langTag}: {ex.Message}");
            }
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
            Debug.WriteLine("[OCR] Fallback to profile languages.");
        }

        private string BuildCacheKey(string path) => string.Concat(path, "|", _currentLangTag, "|", GroupingMode, "|", TextWritingMode, "|", _paragraphGapFactorHorizontal.ToString("F2"), "|", _paragraphGapFactorVertical.ToString("F2"));

        public bool SetLanguage(string langTag)
        {
            if (string.Equals(_currentLangTag, langTag, StringComparison.OrdinalIgnoreCase)) return true;
            if (!SupportedLanguages.Contains(langTag)) return false;
            InitializeEngine(langTag);
            ClearCache();
            return true;
        }

        public void SetGrouping(OcrGrouping mode)
        {
            if (GroupingMode == mode) return;
            GroupingMode = mode;
            ClearCache();
        }

        public void SetWritingMode(WritingMode mode)
        {
            if (TextWritingMode == mode) return;
            TextWritingMode = mode;
            ClearCache();
        }

        public void SetParagraphGapFactorHorizontal(double factor)
        {
            factor = Math.Clamp(factor, 0.1, 1.5);
            if (Math.Abs(factor - _paragraphGapFactorHorizontal) < 0.0001) return;
            _paragraphGapFactorHorizontal = factor;
            ClearCache();
        }
        public void SetParagraphGapFactorVertical(double factor)
        {
            factor = Math.Clamp(factor, 0.1, 1.5);
            if (Math.Abs(factor - _paragraphGapFactorVertical) < 0.0001) return;
            _paragraphGapFactorVertical = factor;
            ClearCache();
        }

        public async Task<IReadOnlyList<BoundingBoxViewModel>> GetOcrAsync(string path, CancellationToken ct, int maxSide = 2600)
        {
            if (string.IsNullOrWhiteSpace(path)) return Array.Empty<BoundingBoxViewModel>();
            string key = BuildCacheKey(path);
            if (_cache.TryGetValue(key, out var cached)) return cached.Boxes;
            try
            {
                ct.ThrowIfCancellationRequested();
                var file = await StorageFile.GetFileFromPathAsync(path);
                using IRandomAccessStream s = await file.OpenAsync(FileAccessMode.Read);
                var decoder = await BitmapDecoder.CreateAsync(s);
                uint pw = decoder.PixelWidth; uint ph = decoder.PixelHeight;
                double scale = 1.0;
                if (Math.Max(pw, ph) > maxSide)
                    scale = maxSide / (double)Math.Max(pw, ph);
                SoftwareBitmap bmp;
                if (scale < 0.999)
                {
                    uint tw = (uint)Math.Round(pw * scale);
                    uint th = (uint)Math.Round(ph * scale);
                    var transform = new BitmapTransform { ScaledWidth = tw, ScaledHeight = th, InterpolationMode = BitmapInterpolationMode.Fant };
                    bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
                }
                else
                {
                    bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                }
                ct.ThrowIfCancellationRequested();

                bmp = Preprocess(bmp);
                var result = await _engine.RecognizeAsync(bmp);
                ct.ThrowIfCancellationRequested();
                int pixelW = (int)pw; int pixelH = (int)ph;
                var boxes = GroupResult(result, scale, pixelW, pixelH);
                _cache[key] = (boxes, DateTime.UtcNow);
                return boxes;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] Fail '{path}': {ex.Message}");
                return Array.Empty<BoundingBoxViewModel>();
            }
        }

        private IReadOnlyList<BoundingBoxViewModel> GroupResult(OcrResult result, double scale, int pixelW, int pixelH)
        {
            bool vertical = DetermineVertical(result);
            double gapFactor = vertical ? _paragraphGapFactorVertical : _paragraphGapFactorHorizontal;
            var list = new List<BoundingBoxViewModel>();

            if (GroupingMode == OcrGrouping.Word)
            {
                foreach (var line in result.Lines)
                    foreach (var w in line.Words)
                        list.Add(CreateBox(w.Text, w.BoundingRect, scale, pixelW, pixelH));
                return list;
            }

            // Build line aggregates
            var lines = new List<(string Text, Rect Rect)>();
            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0) continue;
                Rect rect = line.Words[0].BoundingRect;
                for (int i = 1; i < line.Words.Count; i++) rect = Union(rect, line.Words[i].BoundingRect);
                string text = line.Text?.Trim() ?? string.Join(string.Empty, line.Words.Select(w => w.Text));
                lines.Add((text, rect));
            }

            if (GroupingMode == OcrGrouping.Line)
            {
                foreach (var l in lines) list.Add(CreateBox(l.Text, l.Rect, scale, pixelW, pixelH));
                return list;
            }

            // Paragraph grouping
            var ordered = vertical
                ? lines.OrderBy(l => l.Rect.X).ThenBy(l => l.Rect.Y).ToList()
                : lines.OrderBy(l => l.Rect.Y).ThenBy(l => l.Rect.X).ToList();

            if (ordered.Count == 0) return list;
            string curText = ordered[0].Text;
            Rect curRect = ordered[0].Rect;
            double curPrimaryEnd = vertical ? curRect.X + curRect.Width : curRect.Y + curRect.Height;
            double avgSize = vertical ? curRect.Width : curRect.Height;
            int count = 1;
            for (int i = 1; i < ordered.Count; i++)
            {
                var l = ordered[i];
                double size = vertical ? l.Rect.Width : l.Rect.Height;
                double primaryStart = vertical ? l.Rect.X : l.Rect.Y;
                double gap = primaryStart - curPrimaryEnd;
                avgSize = (avgSize * count + size) / (++count);
                if (gap < avgSize * gapFactor)
                {
                    curText += "\n" + l.Text;
                    curRect = Union(curRect, l.Rect);
                    curPrimaryEnd = Math.Max(curPrimaryEnd, vertical ? l.Rect.X + l.Rect.Width : l.Rect.Y + l.Rect.Height);
                }
                else
                {
                    list.Add(CreateBox(curText, curRect, scale, pixelW, pixelH));
                    curText = l.Text;
                    curRect = l.Rect;
                    curPrimaryEnd = vertical ? curRect.X + curRect.Width : curRect.Y + curRect.Height;
                    avgSize = size;
                    count = 1;
                }
            }
            list.Add(CreateBox(curText, curRect, scale, pixelW, pixelH));
            return list;
        }

        private bool DetermineVertical(OcrResult result)
        {
            if (TextWritingMode == WritingMode.Horizontal) return false;
            if (TextWritingMode == WritingMode.Vertical) return true;
            var words = result.Lines.SelectMany(l => l.Words).Take(60).ToList();
            if (words.Count == 0) return false;
            double ratio = 0;
            foreach (var w in words) if (w.BoundingRect.Width > 0) ratio += w.BoundingRect.Height / w.BoundingRect.Width;
            ratio /= words.Count;
            return ratio > 1.2;
        }

        private BoundingBoxViewModel CreateBox(string text, Rect r, double scale, int pixelW, int pixelH)
        {
            if (scale < 0.999)
                r = new Rect(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);
            if (r.X < 0) r.X = 0; if (r.Y < 0) r.Y = 0;
            if (r.Width < 0) r.Width = 0; if (r.Height < 0) r.Height = 0;
            if (r.X + r.Width > pixelW) r.Width = Math.Max(0, pixelW - r.X);
            if (r.Y + r.Height > pixelH) r.Height = Math.Max(0, pixelH - r.Y);
            return new BoundingBoxViewModel(text, r, pixelW, pixelH);
        }

        private static Rect Union(Rect a, Rect b)
        {
            double x1 = Math.Min(a.X, b.X);
            double y1 = Math.Min(a.Y, b.Y);
            double x2 = Math.Max(a.X + a.Width, b.X + b.Width);
            double y2 = Math.Max(a.Y + a.Height, b.Y + b.Height);
            return new Rect(x1, y1, x2 - x1, y2 - y1);
        }

        private static SoftwareBitmap Preprocess(SoftwareBitmap bmp)
        {
            try
            {
                if (bmp.BitmapPixelFormat != BitmapPixelFormat.Gray8)
                    bmp = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] Preprocess skipped: {ex.Message}");
            }
            return bmp;
        }

        public void ClearCache() => _cache.Clear();
    }
}
