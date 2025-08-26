using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
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
        private readonly OcrEngine _ocrEngine;

        public OcrService()
        {
            // Try to create an OCR engine for the user's preferred language
            _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        }

        /// <summary>
        /// 이미지 파일에서 OCR 수행. 내부적으로 지원 포맷(Gray8)으로 변환 및 EXIF Orientation 적용.
        /// 매우 큰 이미지는 최대 한 변 4000px 로 스케일 다운하여 성능/메모리 사용을 줄입니다.
        /// </summary>
        public async Task<List<OcrResult>> RecognizeAsync(StorageFile imageFile)
        {
            if (_ocrEngine == null)
            {
                // Handle case where OCR engine initialization failed
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

                    // Gray8 로 바로 변환 + EXIF Orientation 적용
                    using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Gray8,
                        BitmapAlphaMode.Ignore,
                        transform,
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.DoNotColorManage);

                    var ocrResult = await _ocrEngine.RecognizeAsync(softwareBitmap);

                    var results = new List<OcrResult>(capacity: Math.Max(8, ocrResult.Lines.Count * 4));
                    foreach (var line in ocrResult.Lines)
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
                // 상세 정보 로그 (디버그 환경)
                Debug.WriteLine($"[OCR] Failed for file '{imageFile.Path}': {ex}");
                Console.WriteLine("OCR recognition failed: " + ex.Message);
                return new List<OcrResult>();
            }
        }
    }
}
