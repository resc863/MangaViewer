using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MangaViewer.Services
{
    public class OcrResult
    {
        public string Text { get; set; }
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
                    var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                    var ocrResult = await _ocrEngine.RecognizeAsync(softwareBitmap);

                    var results = new List<OcrResult>();
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
                // Log or handle the exception appropriately
                Console.WriteLine("OCR recognition failed: " + ex.Message);
                return new List<OcrResult>();
            }
        }
    }
}
