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
    /// OCR 서비스: 이미지 파일 OCR + 결과 캐시 + 그룹핑.
    /// </summary>
    public sealed class OcrService
    {
        public enum OcrGrouping { Word = 0, Line = 1, Paragraph = 2 }
        public enum WritingMode { Auto = 0, Horizontal = 1, Vertical = 2 }

        public static OcrService Instance { get; } = new();

        private readonly OcrEngine? _ocrEngine;
        private readonly ConcurrentDictionary<string, List<BoundingBoxViewModel>> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task<List<BoundingBoxViewModel>>> _inflight = new(StringComparer.OrdinalIgnoreCase); // 중복 OCR 방지
        private readonly SemaphoreSlim _gate = new(1); // 엔진 동시 접근 제한 (엔진 내부 thread-unsafe 가정)

        private OcrGrouping _grouping = OcrGrouping.Word;
        private WritingMode _writingMode = WritingMode.Auto;

        public double ParagraphGapFactorVertical { get; private set; } = 0.8;
        public double ParagraphGapFactorHorizontal { get; private set; } = 0.8;

        public OcrGrouping GroupingMode => _grouping;
        public WritingMode TextWritingMode => _writingMode;
        public string CurrentLanguage { get; private set; } = "auto"; // 단순 추적용 (엔진 재생성 생략)

        private OcrService() => _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();

        #region Configuration
        public void SetLanguage(string lang) { if (!string.IsNullOrWhiteSpace(lang)) CurrentLanguage = lang; ClearCache(); }
        public void SetGrouping(OcrGrouping grouping) { if (_grouping != grouping) { _grouping = grouping; ClearCache(); } }
        public void SetWritingMode(WritingMode mode) { if (_writingMode != mode) { _writingMode = mode; ClearCache(); } }
        public void SetParagraphGapFactorVertical(double v) { ParagraphGapFactorVertical = Math.Clamp(v, 0.1, 3.0); if (_grouping == OcrGrouping.Paragraph) ClearCache(); }
        public void SetParagraphGapFactorHorizontal(double v) { ParagraphGapFactorHorizontal = Math.Clamp(v, 0.1, 3.0); if (_grouping == OcrGrouping.Paragraph) ClearCache(); }
        public void ClearCache() { _cache.Clear(); _inflight.Clear(); }
        #endregion

        /// <summary>
        /// OCR 수행 (캐시 / 중복요청 병합 / 취소 지원)
        /// </summary>
        public Task<List<BoundingBoxViewModel>> GetOcrAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(path) || _ocrEngine == null)
                return Task.FromResult(new List<BoundingBoxViewModel>());

            if (_cache.TryGetValue(path, out var cached))
                return Task.FromResult(cached);

            // 중복 OCR 요청 병합
            return _inflight.GetOrAdd(path, _ => RunOcrInternalAsync(path, ct));
        }

        private async Task<List<BoundingBoxViewModel>> RunOcrInternalAsync(string path, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
                var decoder = await BitmapDecoder.CreateAsync(stream);

                var transform = CreateScaleTransform(decoder.PixelWidth, decoder.PixelHeight);
                using var softwareBitmap = await GetSupportedBitmapAsync(decoder, transform, ct);

                ct.ThrowIfCancellationRequested();
                await _gate.WaitAsync(ct);
                Windows.Media.Ocr.OcrResult ocrRaw;
                try { ocrRaw = await _ocrEngine!.RecognizeAsync(softwareBitmap); }
                finally { _gate.Release(); }

                int pixelW = (int)softwareBitmap.PixelWidth;
                int pixelH = (int)softwareBitmap.PixelHeight;
                var list = _grouping switch
                {
                    OcrGrouping.Line => BuildLineGroups(ocrRaw, pixelW, pixelH),
                    OcrGrouping.Paragraph => BuildParagraphGroups(ocrRaw, pixelW, pixelH),
                    _ => BuildWordGroups(ocrRaw, pixelW, pixelH)
                };
                _cache[path] = list; // 캐시 저장
                return list;
            }
            catch (OperationCanceledException)
            {
                throw; // 호출측에서 구분 처리
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] Failed '{path}': {ex.Message}");
                return new List<BoundingBoxViewModel>();
            }
            finally
            {
                // 완료/실패 후 inflight 제거하여 후속 요청 허용
                _inflight.TryRemove(path, out _);
            }
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
            // 1차 Gray8
            try
            {
                var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
                if (sb != null) return sb;
            }
            catch (Exception ex) { Debug.WriteLine("[OCR] Gray8 decode failed -> fallback: " + ex.Message); }
            // 2차 Bgra8
            try
            {
                var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
                if (sb != null) return sb;
            }
            catch (Exception ex) { Debug.WriteLine("[OCR] Bgra8 decode failed: " + ex.Message); }
            // 3차 원본 후 변환
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
