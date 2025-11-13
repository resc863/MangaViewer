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
    /// <summary>
    /// OCR(Windows.Media.Ocr) 래퍼 서비스.
    /// - 언어/그룹핑/쓰기 방향/문단 간격 등의 설정을 관리하고 변경 이벤트를 발행합니다.
    /// - StorageFile 또는 파일 경로/메모리 스트림에서 이미지를 로드해 OCR을 수행합니다.
    /// - 단어/라인/문단 단위로 결과 바운딩 박스를 계산해 ViewModel로 제공합니다.
    /// </summary>
    public class OcrResult
    {
        public string? Text { get; set; }
        public Windows.Foundation.Rect BoundingBox { get; set; }
    }

    /// <summary>
    /// OCR 엔진과 결과 그룹핑/캐시를 관리하는 싱글톤 서비스.
    /// </summary>
    public class OcrService
    {
        // Singleton
        private static readonly Lazy<OcrService> _instance = new(() => new OcrService());
        /// <summary>전역 인스턴스.</summary>
        public static OcrService Instance => _instance.Value;

        // Settings enums (nested to match call sites like OcrService.OcrGrouping)
        public enum OcrGrouping { Word = 0, Line = 1, Paragraph = 2 }
        public enum WritingMode { Auto = 0, Horizontal = 1, Vertical = 2 }

        // Settings + event
        /// <summary>설정 변경 이벤트(디바운스 적용).</summary>
        public event EventHandler? SettingsChanged;
        /// <summary>현재 언어 태그("auto", "ja", "ko", "en" 등).</summary>
        public string CurrentLanguage { get; private set; } = "auto"; // "auto", "ja", "ko", "en", ...
        /// <summary>결과 그룹핑 모드(단어/줄/문단).</summary>
        public OcrGrouping GroupingMode { get; private set; } = OcrGrouping.Word;
        /// <summary>쓰기 방향(자동/가로/세로).</summary>
        public WritingMode TextWritingMode { get; private set; } = WritingMode.Auto;
        /// <summary>문단 그룹핑 임계값(세로 문자 기준 가로 간격 배수).</summary>
        public double ParagraphGapFactorVertical { get; private set; } = 1.50;   // heuristic
        /// <summary>문단 그룹핑 임계값(가로 문자 기준 세로 간격 배수).</summary>
        public double ParagraphGapFactorHorizontal { get; private set; } = 1.25; // heuristic

        // OCR engine cache per language code ("auto" uses user profile)
        private readonly Dictionary<string, OcrEngine?> _engineCache = new(StringComparer.OrdinalIgnoreCase);

        // Simple OCR results cache (per-path + settings key)
        private readonly Dictionary<string, List<BoundingBoxViewModel>> _ocrCache = new(StringComparer.OrdinalIgnoreCase);

        private EventHandler? _settingsChangedDebounced;
        private readonly SynchronizationContext? _syncContext;
        private TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(250);

        private OcrService()
        {
            // warm up default engine
            _engineCache["auto"] = TryCreateEngineForLanguage("auto");
            _syncContext = SynchronizationContext.Current; // capture UI context if created on UI thread
            RebuildDebouncedHandler();
        }

        /// <summary>
        /// OCR 언어를 변경합니다. "auto"는 사용자 프로필 언어를 사용합니다.
        /// </summary>
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
        /// <summary>
        /// OCR 결과 그룹핑 모드를 변경합니다(단어/줄/문단).
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
        /// 텍스트 쓰기 방향을 변경합니다(자동/가로/세로).
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
        /// 세로 문단 그룹핑 간격 계수를 설정합니다.
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
        /// 가로 문단 그룹핑 간격 계수를 설정합니다.
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
        /// OCR 결과 캐시를 초기화합니다(설정 변경 시 호출).
        /// </summary>
        public void ClearCache()
        {
            _ocrCache.Clear();
        }

        private void OnSettingsChanged()
        {
            // settings affect layout -> invalidate OCR cache
            ClearCache();
            try { _settingsChangedDebounced?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error(ex, "SettingsChanged debounced dispatch failed"); }
        }

        /// <summary>
        /// SettingsChanged 디바운스 간격을 설정합니다.
        /// </summary>
        public void SetSettingsChangedDebounce(TimeSpan delay)
        {
            _debounceDelay = delay;
            RebuildDebouncedHandler();
        }

        private void RebuildDebouncedHandler()
        {
            _settingsChangedDebounced = DebounceOnContext((s, e) => SettingsChanged?.Invoke(s, e), _debounceDelay, _syncContext);
        }

        /// <summary>
        /// 간단한 이벤트 핸들러용 디바운스 유틸리티. 주어진 컨텍스트(UI)에서 실행합니다.
        /// </summary>
        public static EventHandler DebounceOnContext(EventHandler handler, TimeSpan delay, SynchronizationContext? context)
        {
            object gate = new();
            System.Threading.Timer? timer = null;
            return (s, e) =>
            {
                lock (gate)
                {
                    timer?.Dispose();
                    timer = new System.Threading.Timer(_ =>
                    {
                        try
                        {
                            if (context != null)
                                context.Post(_ => handler(s, e), null);
                            else
                                handler(s, e);
                        }
                        catch { }
                    }, null, delay, Timeout.InfiniteTimeSpan);
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
        /// StorageFile 입력으로 단순 OCR을 수행합니다(라인-단어 단위 결과 반환).
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

                    // Decoding helper avoids unsupported pixel format exceptions/log noise.
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
        /// 경로(파일/메모리 키) 기반으로 OCR을 수행하고, 설정에 따라 단어/라인/문단 단위의 박스를 계산합니다.
        /// 결과는 캐시에 저장되며 동일 설정/경로 요청 시 재사용됩니다.
        /// </summary>
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

                cancellationToken.ThrowIfCancellationRequested();
                var toOcr = await DecodeForOcrAsync(decoder, transform);
                cancellationToken.ThrowIfCancellationRequested();
                var ocr = await engine.RecognizeAsync(toOcr);
                cancellationToken.ThrowIfCancellationRequested();

                int imgW = toOcr.PixelWidth;
                int imgH = toOcr.PixelHeight;

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

        /// <summary>
        /// 라인 직사각형 목록과 OCR 결과를 이용해 문단 단위 바운딩 박스를 생성합니다.
        /// 가로/세로 쓰기 방향을 고려해 간격 임계값을 적용합니다.
        /// </summary>
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

        /// <summary>
        /// 라인 인덱스 묶음을 하나의 문단 박스로 결합합니다.
        /// </summary>
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

        /// <summary>
        /// 파일 경로 또는 mem: 키로부터 읽기 전용 RandomAccessStream을 생성합니다.
        /// </summary>
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

        /// <summary>
        /// 디코더로부터 OCR에 적합한 SoftwareBitmap을 생성합니다.
        /// 원본 픽셀 포맷을 우선 사용 후 필요 시 Gray8/Bgra8으로 변환해 예외 및 WinRT unsupported 로그를 회피합니다.
        /// </summary>
        private static async Task<SoftwareBitmap> DecodeForOcrAsync(BitmapDecoder decoder, BitmapTransform transform)
        {
            SoftwareBitmap bitmap;
            // 1) 시도: 원본 포맷 (unsupported 예외 최소화)
            try
            {
                bitmap = await decoder.GetSoftwareBitmapAsync(decoder.BitmapPixelFormat, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            }
            catch
            {
                // 2) fallback: Bgra8 (일반적으로 대부분 지원)
                try
                {
                    bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
                }
                catch
                {
                    // 3) 최종 fallback: 기본 호출
                    bitmap = await decoder.GetSoftwareBitmapAsync();
                }
            }

            // 4) 포맷이 OCR 엔진 허용 포맷(Gray8/Bgra8)이 아닌 경우 변환 시도
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Gray8 && bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                try
                {
                    var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8);
                    bitmap = converted;
                }
                catch { /* 변환 실패시 원본 그대로 사용 */ }
            }
            return bitmap;
        }
    }
}
