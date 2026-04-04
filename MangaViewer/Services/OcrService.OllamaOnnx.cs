using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MangaViewer.Services.Logging;
using MangaViewer.ViewModels;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MangaViewer.Services
{
    public partial class OcrService
    {
        private const string DocLayoutModelDownloadUrl = "https://huggingface.co/alex-dinh/PP-DocLayoutV3-ONNX/resolve/main/PP-DocLayoutV3.onnx";

        public string GetDocLayoutModelPath()
            => Path.Combine(AppContext.BaseDirectory, DocLayoutModelRelativePath.Replace('/', Path.DirectorySeparatorChar));

        public string GetDocLayoutEpContextModelPath()
        {
            string onnxPath = GetDocLayoutModelPath();
            string directory = Path.GetDirectoryName(onnxPath) ?? AppContext.BaseDirectory;
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(onnxPath);
            return Path.Combine(directory, fileNameWithoutExt + ".epctx.onnx");
        }

        public bool IsDocLayoutModelInstalled()
            => File.Exists(GetDocLayoutModelPath());

        public bool IsDocLayoutEpContextModelInstalled()
            => File.Exists(GetDocLayoutEpContextModelPath());

        public async Task DownloadDocLayoutModelAsync(CancellationToken cancellationToken)
        {
            string targetPath = GetDocLayoutModelPath();
            string? targetDirectory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(targetDirectory))
                throw new InvalidOperationException("Unable to resolve ONNX model directory.");

            Directory.CreateDirectory(targetDirectory);
            string tempPath = targetPath + ".download";

            try
            {
                using var response = await s_httpClient.GetAsync(DocLayoutModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempPath, targetPath, overwrite: true);
                InvalidateDocLayoutSession();
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }

                throw;
            }
        }

        public async Task<string> GetOllamaTextAsync(string imagePath, CancellationToken cancellationToken)
        {
            var result = await GetOllamaOcrAsync(imagePath, cancellationToken).ConfigureAwait(false);
            return result.Text;
        }

        public bool HasCachedCurrentBackendOcr(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return false;

            string mode = Backend == OcrBackend.Hybrid ? "hybrid" : "vlm";
            string cacheKey = BuildOllamaCacheKey(imagePath, mode);
            lock (_cacheGate)
            {
                if (!_ollamaCache.TryGetValue(cacheKey, out var cached))
                    return false;

                if (!ShouldReuseCachedOcrResult(cached))
                    return false;

                if (Backend == OcrBackend.Hybrid && HasIncompleteHybridTextBoxes(cached))
                    return false;

                return true;
            }
        }

        private static bool ShouldReuseCachedOcrResult(OllamaOcrResponse? response)
            => response != null && response.Boxes.Count > 0;

        private static bool HasIncompleteHybridTextBoxes(OllamaOcrResponse? response)
            => response != null && response.Boxes.Any(IsIncompleteHybridTextBox);

        private static bool IsIncompleteHybridTextBox(BoundingBoxViewModel box)
            => box != null && string.IsNullOrWhiteSpace(box.Text);

        private static List<BoundingBoxViewModel> GetIncompleteHybridTextBoxes(IReadOnlyList<BoundingBoxViewModel> boxes)
            => boxes.Where(IsIncompleteHybridTextBox).ToList();

        public async Task<OllamaOcrResponse> GetOllamaOcrAsync(string imagePath, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return new OllamaOcrResponse();

            // VLM-only OCR cache key path.
            // Hybrid pipeline uses a separate mode key in `GetHybridOcrAsync`.
            string cacheKey = BuildOllamaCacheKey(imagePath, "vlm");
            if (!forceRefresh)
            {
                lock (_cacheGate)
                {
                    if (_ollamaCache.TryGetValue(cacheKey, out var cached)
                        && ShouldReuseCachedOcrResult(cached))
                        return cached;
                }
            }

            try
            {
                int sourceImageWidth = 0;
                int sourceImageHeight = 0;
                int originalImageWidth = 0;
                int originalImageHeight = 0;

                byte[]? imageBytes = await TryGetOriginalImageBytesAndSizeAsync(imagePath, cancellationToken).ConfigureAwait(false);
                if (imageBytes != null && imageBytes.Length > 0)
                {
                    var originalSize = await TryGetImageSizeAsync(imageBytes, cancellationToken).ConfigureAwait(false);
                    sourceImageWidth = originalSize.Width;
                    sourceImageHeight = originalSize.Height;
                    originalImageWidth = originalSize.Width;
                    originalImageHeight = originalSize.Height;
                }

                if (imageBytes == null || imageBytes.Length == 0 || sourceImageWidth <= 0 || sourceImageHeight <= 0)
                {
                    var bitmap = await _decoder.DecodeForOcrAsync(imagePath, cancellationToken).ConfigureAwait(false);
                    if (bitmap == null) return new OllamaOcrResponse();

                    sourceImageWidth = bitmap.PixelWidth;
                    sourceImageHeight = bitmap.PixelHeight;

                    var originalSize = await TryGetOriginalImageSizeFromPathAsync(imagePath, cancellationToken).ConfigureAwait(false);
                    if (originalSize.Width > 0 && originalSize.Height > 0)
                    {
                        originalImageWidth = originalSize.Width;
                        originalImageHeight = originalSize.Height;
                    }
                    else
                    {
                        originalImageWidth = sourceImageWidth;
                        originalImageHeight = sourceImageHeight;
                    }

                    imageBytes = await EncodeSoftwareBitmapToJpegAsync(bitmap).ConfigureAwait(false);
                    if (imageBytes == null || imageBytes.Length == 0)
                        return new OllamaOcrResponse();
                }

                if (originalImageWidth <= 0 || originalImageHeight <= 0)
                {
                    originalImageWidth = sourceImageWidth;
                    originalImageHeight = sourceImageHeight;
                }

                cancellationToken.ThrowIfCancellationRequested();

                string responseText = await SendOllamaVisionOcrRequestAsync(imageBytes, sourceImageWidth, sourceImageHeight, cancellationToken).ConfigureAwait(false);

                OllamaOcrResponse result = ParseStructuredOllamaResponse(
                    responseText,
                    OllamaModel,
                    sourceImageWidth,
                    sourceImageHeight,
                    originalImageWidth,
                    originalImageHeight);

                lock (_cacheGate)
                {
                    if (ShouldReuseCachedOcrResult(result))
                        _ollamaCache[cacheKey] = result;
                    else
                        _ollamaCache.Remove(cacheKey);
                }

                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error(ex, "[OcrService] Ollama OCR failed");
                return new OllamaOcrResponse();
            }
        }

        public async Task<OllamaOcrResponse> GetHybridOcrAsync(
            string imagePath,
            CancellationToken cancellationToken,
            bool forceRefresh = false,
            Action<IReadOnlyList<BoundingBoxViewModel>>? onLayoutBoxesReady = null)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return new OllamaOcrResponse();

            // Hybrid OCR pipeline
            // 1) ONNX DocLayout detects reading regions on the original image.
            // 2) Regions are projected into `BoundingBoxViewModel` for immediate UI layout preview.
            // 3) Each region is cropped losslessly and recognized by GLM OCR in parallel (throttled).
            // 4) Final text/boxes are composed and cached.
            string cacheKey = BuildOllamaCacheKey(imagePath, "hybrid");
            OllamaOcrResponse? cachedHybrid = null;
            if (!forceRefresh)
            {
                lock (_cacheGate)
                {
                    if (_ollamaCache.TryGetValue(cacheKey, out var cached)
                        && ShouldReuseCachedOcrResult(cached))
                    {
                        cachedHybrid = cached;
                    }
                }

                if (cachedHybrid != null)
                {
                    onLayoutBoxesReady?.Invoke(cachedHybrid.Boxes);

                    var pendingBoxes = GetIncompleteHybridTextBoxes(cachedHybrid.Boxes);
                    if (pendingBoxes.Count == 0)
                        return cachedHybrid;

                    try
                    {
                        var sourceImage = await TryGetHybridSourceImageAsync(imagePath, cancellationToken).ConfigureAwait(false);
                        if (sourceImage.Bytes != null && sourceImage.Width > 0 && sourceImage.Height > 0)
                        {
                            await RecognizeLayoutBoxesWithGlmOcrAsync(
                                sourceImage.Bytes,
                                pendingBoxes,
                                cancellationToken).ConfigureAwait(false);
                        }

                        var resumed = BuildOllamaOcrResponse(cachedHybrid.Boxes);
                        lock (_cacheGate)
                        {
                            if (ShouldReuseCachedOcrResult(resumed))
                                _ollamaCache[cacheKey] = resumed;
                            else
                                _ollamaCache.Remove(cacheKey);
                        }

                        return resumed;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OcrService][ONNX] Hybrid OCR partial retry failed. Error={ex.Message}");
                        return cachedHybrid;
                    }
                }
            }

            try
            {
                var sourceImage = await TryGetHybridSourceImageAsync(imagePath, cancellationToken).ConfigureAwait(false);
                if (sourceImage.Bytes == null || sourceImage.Width <= 0 || sourceImage.Height <= 0)
                {
                    return await BuildHybridFallbackResponseAsync(
                        imagePath,
                        "Hybrid OCR source image unavailable",
                        cancellationToken,
                        forceRefresh,
                        null).ConfigureAwait(false);
                }

                var layoutBoxes = await DetectLayoutWithOnnxAsync(
                    sourceImage.Bytes,
                    sourceImage.Width,
                    sourceImage.Height,
                    cancellationToken).ConfigureAwait(false);
                if (layoutBoxes.Count == 0)
                {
                    return await BuildHybridFallbackResponseAsync(
                        imagePath,
                        "Hybrid OCR ONNX layout detection returned no boxes",
                        cancellationToken,
                        forceRefresh,
                        null).ConfigureAwait(false);
                }

                var resultBoxes = BuildLayoutBoundingBoxes(layoutBoxes, sourceImage.Width, sourceImage.Height);
                onLayoutBoxesReady?.Invoke(resultBoxes);

                await RecognizeLayoutBoxesWithGlmOcrAsync(
                    sourceImage.Bytes,
                    resultBoxes,
                    cancellationToken).ConfigureAwait(false);

                var result = BuildOllamaOcrResponse(resultBoxes);

                lock (_cacheGate)
                {
                    if (ShouldReuseCachedOcrResult(result))
                        _ollamaCache[cacheKey] = result;
                    else
                        _ollamaCache.Remove(cacheKey);
                }

                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error(ex, "[OcrService] Hybrid OCR failed");
                return await BuildHybridFallbackResponseAsync(
                    imagePath,
                    "Hybrid OCR failed while executing ONNX layout detection",
                    cancellationToken,
                    forceRefresh,
                    ex).ConfigureAwait(false);
            }
        }

        private async Task<OllamaOcrResponse> BuildHybridFallbackResponseAsync(
            string imagePath,
            string reason,
            CancellationToken cancellationToken,
            bool forceRefresh,
            Exception? ex)
        {
            string message = $"Hybrid OCR ONNX EP failed. Falling back to VLM OCR. Reason={reason}";
            if (ex != null)
                message += $", Error={ex.Message}";

            Debug.WriteLine($"[OcrService][ONNX] {message}");

            if (!HybridOnnxFallbackEnabled)
            {
                return new OllamaOcrResponse
                {
                    UsedFallback = false,
                    StatusMessage = message + ", FallbackDisabledByUser=true"
                };
            }

            try
            {
                var fallback = await GetOllamaOcrAsync(imagePath, cancellationToken, forceRefresh).ConfigureAwait(false);
                return new OllamaOcrResponse
                {
                    Text = fallback.Text,
                    Boxes = fallback.Boxes,
                    UsedFallback = true,
                    StatusMessage = message
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception fallbackEx)
            {
                Log.Error(fallbackEx, "[OcrService] Hybrid OCR fallback failed");
                return new OllamaOcrResponse
                {
                    UsedFallback = true,
                    StatusMessage = message + $", FallbackError={fallbackEx.Message}"
                };
            }
        }

        private async Task<(byte[]? Bytes, int Width, int Height)> TryGetHybridSourceImageAsync(string imagePath, CancellationToken cancellationToken)
        {
            byte[]? originalBytes = await TryGetOriginalImageBytesAndSizeAsync(imagePath, cancellationToken).ConfigureAwait(false);
            if (originalBytes == null || originalBytes.Length == 0)
                return (null, 0, 0);

            var originalSize = await TryGetImageSizeAsync(originalBytes, cancellationToken).ConfigureAwait(false);
            return (originalBytes, originalSize.Width, originalSize.Height);
        }

        private async Task<List<DocLayoutBox>> DetectLayoutWithOnnxAsync(byte[] imageBytes, int imageWidth, int imageHeight, CancellationToken cancellationToken)
        {
            try
            {
                return await RunDocLayoutDetectionAsync(imageBytes, imageWidth, imageHeight, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OcrService][ONNX] Primary EP layout detection failed. Retrying with CPU EP. Error={ex.Message}");
                return await RunDocLayoutDetectionWithCpuFallbackAsync(imageBytes, imageWidth, imageHeight, cancellationToken).ConfigureAwait(false);
            }
        }

        private static List<BoundingBoxViewModel> BuildLayoutBoundingBoxes(
            IReadOnlyList<DocLayoutBox> layoutBoxes,
            int imageWidth,
            int imageHeight)
        {
            // Keep ONNX read-order so follow-up OCR and UI overlay order stay deterministic.
            var resultBoxes = new List<BoundingBoxViewModel>(layoutBoxes.Count);
            foreach (var layoutBox in layoutBoxes.OrderBy(x => x.ReadOrder))
            {
                if (layoutBox.Rect.Width <= 0 || layoutBox.Rect.Height <= 0)
                    continue;

                resultBoxes.Add(new BoundingBoxViewModel(string.Empty, layoutBox.Rect, imageWidth, imageHeight));
            }

            return resultBoxes;
        }

        private async Task RecognizeLayoutBoxesWithGlmOcrAsync(
            byte[] originalBytes,
            IReadOnlyList<BoundingBoxViewModel> layoutBoxes,
            CancellationToken cancellationToken)
        {
            if (layoutBoxes.Count == 0)
                return;

            int maxParallelTextExtraction = Math.Clamp(HybridTextExtractionParallelism, 1, 8);
            using var throttler = new SemaphoreSlim(maxParallelTextExtraction);

            var tasks = layoutBoxes.Select(async box =>
            {
                await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Crop from original bytes to avoid quality loss from repeated re-encoding.
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[]? cropBytes = await EncodeCropToLosslessAsync(originalBytes, box.OriginalBoundingBox, cancellationToken).ConfigureAwait(false);
                    if (cropBytes == null || cropBytes.Length == 0)
                    {
                        box.Text = string.Empty;
                        return;
                    }

                    string cropText = await SendOllamaCropOcrRequestAsync(cropBytes, cancellationToken).ConfigureAwait(false);
                    box.Text = string.IsNullOrWhiteSpace(cropText) ? string.Empty : cropText.Trim();
                }
                finally
                {
                    throttler.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private static OllamaOcrResponse BuildOllamaOcrResponse(List<BoundingBoxViewModel> resultBoxes)
        {
            return new OllamaOcrResponse
            {
                Text = string.Join(Environment.NewLine, resultBoxes.Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t))),
                Boxes = resultBoxes
            };
        }

        private async Task<List<DocLayoutBox>> RunDocLayoutDetectionAsync(byte[] imageBytes, int imageWidth, int imageHeight, CancellationToken cancellationToken)
        {
            await EnsureOnnxExecutionProvidersReadyAsync(cancellationToken, force: false).ConfigureAwait(false);

            if (OnnxUseEpContextModel
                && OnnxAutoCompileEpContextModel
                && IsDocLayoutModelInstalled()
                && !IsDocLayoutEpContextModelInstalled())
            {
                await CompileDocLayoutEpContextModelAsync(cancellationToken).ConfigureAwait(false);
            }

            Debug.WriteLine($"[OcrService][ONNX] Bounding-box detection request started. InputImage={imageWidth}x{imageHeight}, Bytes={imageBytes?.Length ?? 0}");

            var session = GetOrCreateDocLayoutSession();
            if (session == null)
            {
                Debug.WriteLine("[OcrService][ONNX] Bounding-box detection completed. Returned=False, BoxCount=0 (session unavailable)");
                return new List<DocLayoutBox>();
            }

            return await RunDocLayoutDetectionCoreAsync(session, imageBytes, imageWidth, imageHeight, cancellationToken).ConfigureAwait(false);
        }

        private async Task<List<DocLayoutBox>> RunDocLayoutDetectionWithCpuFallbackAsync(byte[] imageBytes, int imageWidth, int imageHeight, CancellationToken cancellationToken)
        {
            string modelPath = GetDocLayoutModelPath();
            if (!File.Exists(modelPath))
                modelPath = ResolveDocLayoutLoadModelPath();

            if (!File.Exists(modelPath))
            {
                Debug.WriteLine($"[OcrService][ONNX] CPU fallback model file not found. Path={modelPath}");
                return new List<DocLayoutBox>();
            }

            using var options = CreateCpuOnlyDocLayoutSessionOptions();
            using var session = new InferenceSession(modelPath, options);
            Debug.WriteLine($"[OcrService][ONNX] CPU fallback session loaded. Path={modelPath}");
            return await RunDocLayoutDetectionCoreAsync(session, imageBytes, imageWidth, imageHeight, cancellationToken).ConfigureAwait(false);
        }

        private static SessionOptions CreateCpuOnlyDocLayoutSessionOptions()
        {
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                EnableMemoryPattern = true,
                EnableCpuMemArena = true
            };

            return options;
        }

        private async Task<List<DocLayoutBox>> RunDocLayoutDetectionCoreAsync(
            InferenceSession session,
            byte[] imageBytes,
            int imageWidth,
            int imageHeight,
            CancellationToken cancellationToken)
        {

            using var inputStream = new InMemoryRandomAccessStream();
            await inputStream.WriteAsync(imageBytes.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
            inputStream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(inputStream).AsTask(cancellationToken).ConfigureAwait(false);
            var transform = new BitmapTransform
            {
                // Model expects square fixed-size tensor.
                ScaledWidth = DocLayoutInputSize,
                ScaledHeight = DocLayoutInputSize,
                InterpolationMode = BitmapInterpolationMode.Linear
            };

            var resized = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);

            var inputBlob = BuildDocLayoutInputBlob(resized);
            float scaleH = imageHeight > 0 ? (float)DocLayoutInputSize / imageHeight : 0;
            float scaleW = imageWidth > 0 ? (float)DocLayoutInputSize / imageWidth : 0;

            var inputNames = session.InputMetadata.Keys.ToList();
            ResolveDocLayoutInputNames(inputNames, out string imShapeInputName, out string imageInputName, out string scaleInputName);
            string outputName = session.OutputMetadata.Keys.FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(outputName))
            {
                Debug.WriteLine("[OcrService][ONNX] Bounding-box detection completed. Returned=False, BoxCount=0 (output metadata missing)");
                return new List<DocLayoutBox>();
            }

            var imShapeTensor = new DenseTensor<float>(new[] { 1, 2 });
            imShapeTensor[0, 0] = DocLayoutInputSize;
            imShapeTensor[0, 1] = DocLayoutInputSize;

            var imageTensor = new DenseTensor<float>(new[] { 1, 3, DocLayoutInputSize, DocLayoutInputSize });
            inputBlob.AsSpan().CopyTo(imageTensor.Buffer.Span);

            var scaleTensor = new DenseTensor<float>(new[] { 1, 2 });
            scaleTensor[0, 0] = scaleH;
            scaleTensor[0, 1] = scaleW;

            var inputs = new List<NamedOnnxValue>(3)
            {
                NamedOnnxValue.CreateFromTensor(imShapeInputName, imShapeTensor),
                NamedOnnxValue.CreateFromTensor(imageInputName, imageTensor),
                NamedOnnxValue.CreateFromTensor(scaleInputName, scaleTensor)
            };

            using var results = session.Run(inputs);
            TryLogDocLayoutRuntimeExecutionProviders(session);
            var output = results.FirstOrDefault(r => string.Equals(r.Name, outputName, StringComparison.Ordinal)) ?? results.FirstOrDefault();
            if (output == null)
            {
                Debug.WriteLine("[OcrService][ONNX] Bounding-box detection completed. Returned=False, BoxCount=0 (output tensor missing)");
                return new List<DocLayoutBox>();
            }

            var tensor = output.AsTensor<float>();
            var values = tensor.ToArray();
            if (values.Length == 0)
            {
                Debug.WriteLine("[OcrService][ONNX] Bounding-box detection completed. Returned=False, BoxCount=0 (empty output)");
                return new List<DocLayoutBox>();
            }

            int columns = 7;
            var dims = tensor.Dimensions.ToArray();
            if (dims.Length > 0)
            {
                int shapeTail = dims[^1];
                if (shapeTail > 0) columns = shapeTail;
            }

            if (columns < 7)
            {
                Debug.WriteLine($"[OcrService][ONNX] Bounding-box detection completed. Returned=False, BoxCount=0 (invalid column count={columns})");
                return new List<DocLayoutBox>();
            }

            int rows = values.Length / columns;
            var boxes = new List<DocLayoutBox>(rows);
            for (int r = 0; r < rows; r++)
            {
                int offset = r * columns;
                float score = values[offset + 1];
                if (score <= DocLayoutScoreThreshold)
                    continue;

                float x1 = Math.Clamp(values[offset + 2], 0, Math.Max(0, imageWidth - 1));
                float y1 = Math.Clamp(values[offset + 3], 0, Math.Max(0, imageHeight - 1));
                float x2 = Math.Clamp(values[offset + 4], 0, Math.Max(0, imageWidth - 1));
                float y2 = Math.Clamp(values[offset + 5], 0, Math.Max(0, imageHeight - 1));
                int readOrder = (int)Math.Round(values[offset + 6]);

                double minX = Math.Min(x1, x2);
                double minY = Math.Min(y1, y2);
                double width = Math.Abs(x2 - x1);
                double height = Math.Abs(y2 - y1);
                if (width <= 0 || height <= 0)
                    continue;

                boxes.Add(new DocLayoutBox(score, new Rect(minX, minY, width, height), readOrder));
            }

            Debug.WriteLine($"[OcrService][ONNX] Bounding-box detection completed. Returned={(boxes.Count > 0)}, BoxCount={boxes.Count}");

            return boxes;
        }

        private static float[] BuildDocLayoutInputBlob(SoftwareBitmap bitmap)
        {
            // Convert BGRA byte layout into normalized CHW float tensor (R, G, B planes).
            var normalized = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? bitmap
                : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

            int width = normalized.PixelWidth;
            int height = normalized.PixelHeight;
            var rgbaBytes = new byte[width * height * 4];
            normalized.CopyToBuffer(rgbaBytes.AsBuffer());

            int planeSize = width * height;
            var chw = new float[3 * planeSize];
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int pixelIndex = rowOffset + x;
                    int srcOffset = pixelIndex * 4;

                    float b = rgbaBytes[srcOffset] / 255f;
                    float g = rgbaBytes[srcOffset + 1] / 255f;
                    float r = rgbaBytes[srcOffset + 2] / 255f;

                    chw[pixelIndex] = r;
                    chw[planeSize + pixelIndex] = g;
                    chw[(2 * planeSize) + pixelIndex] = b;
                }
            }

            return chw;
        }

        private static void ResolveDocLayoutInputNames(IReadOnlyList<string> inputNames, out string imShapeInputName, out string imageInputName, out string scaleInputName)
        {
            // Some exported ONNX models use different naming conventions.
            // Resolve by semantic keyword first, then stable positional fallback.
            imageInputName = inputNames.FirstOrDefault(n => n.Contains("image", StringComparison.OrdinalIgnoreCase))
                ?? inputNames.ElementAtOrDefault(1)
                ?? inputNames.FirstOrDefault()
                ?? "image";

            imShapeInputName = inputNames.FirstOrDefault(n => n.Contains("im_shape", StringComparison.OrdinalIgnoreCase))
                ?? inputNames.FirstOrDefault(n => n.Contains("imshape", StringComparison.OrdinalIgnoreCase))
                ?? inputNames.ElementAtOrDefault(0)
                ?? "im_shape";

            string resolvedImShapeInputName = imShapeInputName;

            scaleInputName = inputNames.FirstOrDefault(n => n.Contains("scale_factor", StringComparison.OrdinalIgnoreCase))
                ?? inputNames.FirstOrDefault(n => n.Contains("scale", StringComparison.OrdinalIgnoreCase) && !string.Equals(n, resolvedImShapeInputName, StringComparison.OrdinalIgnoreCase))
                ?? inputNames.ElementAtOrDefault(2)
                ?? "scale_factor";
        }

        private InferenceSession? GetOrCreateDocLayoutSession()
        {
            string modelPath = ResolveDocLayoutLoadModelPath();
            if (!File.Exists(modelPath))
            {
                Debug.WriteLine($"[OcrService][ONNX] Model file not found. Path={modelPath}");
                return null;
            }

            lock (_docLayoutSessionGate)
            {
                if (_docLayoutSession != null
                    && string.Equals(_docLayoutModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[OcrService][ONNX] Model already loaded. Success=True, Path={modelPath}");
                    return _docLayoutSession;
                }

                Debug.WriteLine($"[OcrService][ONNX] Loading model... Path={modelPath}");
                try
                {
                    _docLayoutSession?.Dispose();
                    var sessionOptions = CreateDocLayoutSessionOptions();
                    _docLayoutSession = new InferenceSession(modelPath, sessionOptions);
                    _docLayoutModelPath = modelPath;
                    _docLayoutRuntimeEpLogged = false;
                    Debug.WriteLine($"[OcrService][ONNX] Model loaded. Success=True, Inputs={_docLayoutSession.InputMetadata.Count}, Outputs={_docLayoutSession.OutputMetadata.Count}");
                    return _docLayoutSession;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OcrService][ONNX] Model loaded. Success=False, Error={ex.Message}");
                    throw;
                }
            }
        }

        private void InvalidateDocLayoutSession()
        {
            lock (_docLayoutSessionGate)
            {
                _docLayoutSession?.Dispose();
                _docLayoutSession = null;
                _docLayoutModelPath = null;
                _docLayoutRuntimeEpLogged = false;
            }
        }

        private SessionOptions CreateDocLayoutSessionOptions()
        {
            var sessionOptions = new SessionOptions();

            sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            sessionOptions.EnableMemoryPattern = true;
            sessionOptions.EnableCpuMemArena = true;

            var requestedProviders = ResolveRequestedExecutionProviders();
            if (requestedProviders.Count > 0)
            {
                Debug.WriteLine($"[OcrService][ONNX] Requested EP chain: {string.Join(" -> ", requestedProviders)}");
            }
            else
            {
                Debug.WriteLine("[OcrService][ONNX] Requested EP chain: <none> (runtime default registration will be used)");
            }

            var appendedProviders = new List<string>();
            foreach (string providerName in requestedProviders)
            {
                Dictionary<string, string>? providerOptions = BuildExecutionProviderOptions(providerName);
                if (TryAppendExecutionProviderByName(sessionOptions, providerName, providerOptions))
                    appendedProviders.Add(providerName);
            }

            if (appendedProviders.Count > 0)
                Debug.WriteLine($"[OcrService][ONNX] EP passed to SessionOptions: {string.Join(" -> ", appendedProviders)}");
            else
                Debug.WriteLine("[OcrService][ONNX] EP append API unavailable for named providers. Using registered certified EPs.");

            return sessionOptions;
        }

        private Dictionary<string, string>? BuildExecutionProviderOptions(string providerName)
        {
            if (string.IsNullOrWhiteSpace(providerName))
                return null;

            if (!providerName.Contains("tensorrtrtx", StringComparison.OrdinalIgnoreCase))
                return null;

            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["enable_cuda_graph"] = OnnxTrtRtxEnableCudaGraph ? "1" : "0"
            };

            string runtimeCachePath = OnnxTrtRtxRuntimeCachePath?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(runtimeCachePath))
            {
                try
                {
                    Directory.CreateDirectory(runtimeCachePath);
                    options["nv_runtime_cache_path"] = runtimeCachePath;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OcrService][ONNX] Failed to create runtime cache dir: {runtimeCachePath}, Error={ex.Message}");
                }
            }

            return options;
        }

        private List<string> ResolveRequestedExecutionProviders()
        {
            var providers = new List<string>();

            if (OnnxExecutionProviderMode == OnnxEpRegistrationMode.Manual && !string.IsNullOrWhiteSpace(OnnxExecutionProviderManualList))
            {
                providers.AddRange(OnnxExecutionProviderManualList
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                return providers;
            }

            var compatible = GetCompatibleOnnxExecutionProviders();
            foreach (var provider in compatible)
            {
                if (provider.ReadyState.Contains("Ready", StringComparison.OrdinalIgnoreCase))
                    providers.Add(provider.Name);
            }

            return providers;
        }

        private static bool TryAppendExecutionProviderByName(SessionOptions sessionOptions, string providerName, IReadOnlyDictionary<string, string>? providerOptions)
        {
            if (string.IsNullOrWhiteSpace(providerName))
                return false;

            if (TryAppendExecutionProviderByDevice(sessionOptions, providerName, providerOptions))
                return true;

            var methods = typeof(SessionOptions).GetMethods()
                .Where(m => string.Equals(m.Name, "AppendExecutionProvider", StringComparison.Ordinal))
                .ToArray();

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                try
                {
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    {
                        method.Invoke(sessionOptions, new object[] { providerName });
                        return true;
                    }

                    if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string))
                    {
                        object? argument = BuildProviderOptionsArgument(parameters[1].ParameterType, providerOptions);
                        if (argument == null && parameters[1].ParameterType.IsValueType)
                            argument = Activator.CreateInstance(parameters[1].ParameterType);
                        method.Invoke(sessionOptions, new[] { (object)providerName, argument! });
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OcrService][ONNX] Failed to append EP '{providerName}': {ex.Message}");
                }
            }

            return false;
        }

        private static bool TryAppendExecutionProviderByDevice(SessionOptions sessionOptions, string providerName, IReadOnlyDictionary<string, string>? providerOptions)
        {
            try
            {
                OrtEnv ortEnv = OrtEnv.Instance();
                var epDevices = ortEnv.GetEpDevices()
                    .Where(device => string.Equals(device.EpName, providerName, StringComparison.OrdinalIgnoreCase))
                    .Where(device => !providerName.Contains("tensorrtrtx", StringComparison.OrdinalIgnoreCase)
                        || device.HardwareDevice.Type == OrtHardwareDeviceType.GPU)
                    .ToList();

                if (epDevices.Count == 0)
                    return false;

                var options = providerOptions == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(providerOptions, StringComparer.OrdinalIgnoreCase);

                sessionOptions.AppendExecutionProvider(ortEnv, epDevices, options);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OcrService][ONNX] Failed to append EP '{providerName}' by OrtEnv device selection: {ex.Message}");
                return false;
            }
        }

        private static object? BuildProviderOptionsArgument(Type parameterType, IReadOnlyDictionary<string, string>? options)
        {
            if (options == null || options.Count == 0)
                return null;

            if (parameterType.IsAssignableFrom(typeof(Dictionary<string, string>)))
                return new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);

            if (parameterType.IsAssignableFrom(typeof(IReadOnlyDictionary<string, string>)))
                return new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);

            if (parameterType.IsAssignableFrom(typeof(IDictionary<string, string>)))
                return new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);

            return null;
        }

        private string ResolveDocLayoutLoadModelPath()
        {
            if (OnnxUseEpContextModel)
            {
                string epContextPath = GetDocLayoutEpContextModelPath();
                if (File.Exists(epContextPath))
                    return epContextPath;
            }

            return GetDocLayoutModelPath();
        }

        public async Task<bool> CompileDocLayoutEpContextModelAsync(CancellationToken cancellationToken)
        {
            string inputModelPath = GetDocLayoutModelPath();
            if (!File.Exists(inputModelPath))
            {
                OnnxExecutionProviderStatus = "EP context compile skipped: model file not found";
                return false;
            }

            string outputModelPath = GetDocLayoutEpContextModelPath();
            string? outputDirectory = Path.GetDirectoryName(outputModelPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            await EnsureOnnxExecutionProvidersReadyAsync(cancellationToken, force: false).ConfigureAwait(false);

            bool compiledByCompileApi = false;
            try
            {
                compiledByCompileApi = TryCompileDocLayoutEpContextWithCompileApi(inputModelPath, outputModelPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OcrService][ONNX] Compile API EP context generation failed: {ex.Message}");
            }

            if (!compiledByCompileApi)
            {
                try
                {
                    using var options = CreateDocLayoutSessionOptions();
                    options.OptimizedModelFilePath = outputModelPath;
                    using var _ = new InferenceSession(inputModelPath, options);
                    compiledByCompileApi = File.Exists(outputModelPath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OcrService][ONNX] Optimized model fallback generation failed: {ex.Message}");
                    compiledByCompileApi = false;
                }
            }

            if (!compiledByCompileApi)
            {
                OnnxExecutionProviderStatus = "EP context compile failed (Compile API unavailable and optimized-model fallback failed)";
                return false;
            }

            InvalidateDocLayoutSession();
            OnnxExecutionProviderStatus = $"EP context model ready: {Path.GetFileName(outputModelPath)}";
            return true;
        }

        private bool TryCompileDocLayoutEpContextWithCompileApi(string inputModelPath, string outputModelPath)
        {
            var ortAssembly = typeof(InferenceSession).Assembly;
            Type? modelCompilerType = ortAssembly.GetType("Microsoft.ML.OnnxRuntime.ModelCompiler");
            if (modelCompilerType == null)
                return false;

            using var options = CreateDocLayoutSessionOptions();
            object? modelCompiler = null;
            foreach (var ctor in modelCompilerType.GetConstructors())
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length == 2
                    && parameters[0].ParameterType == typeof(SessionOptions)
                    && parameters[1].ParameterType == typeof(string))
                {
                    modelCompiler = ctor.Invoke(new object[] { options, inputModelPath });
                    break;
                }
            }

            if (modelCompiler == null)
                return false;

            var compileToFileMethod = modelCompilerType.GetMethod("CompileToFile", new[] { typeof(string) })
                ?? modelCompilerType.GetMethod("Compile", new[] { typeof(string) });
            if (compileToFileMethod == null)
                return false;

            compileToFileMethod.Invoke(modelCompiler, new object[] { outputModelPath });
            return File.Exists(outputModelPath);
        }

        private void TryLogDocLayoutRuntimeExecutionProviders(InferenceSession session)
        {
            lock (_docLayoutSessionGate)
            {
                if (_docLayoutRuntimeEpLogged)
                    return;

                _docLayoutRuntimeEpLogged = true;
            }

            try
            {
                var method = session.GetType().GetMethod("GetExecutionProviderNames", Type.EmptyTypes);
                if (method?.Invoke(session, null) is IEnumerable<string> names)
                {
                    string epChain = string.Join(" -> ", names.Where(n => !string.IsNullOrWhiteSpace(n)));
                    Debug.WriteLine($"[OcrService][ONNX] Runtime EP execution chain: {epChain}");
                    return;
                }

                Debug.WriteLine("[OcrService][ONNX] Runtime EP execution chain: unavailable (GetExecutionProviderNames API not found)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OcrService][ONNX] Runtime EP execution chain logging failed: {ex.Message}");
            }
        }

        private static async Task<byte[]?> EncodeCropToLosslessAsync(byte[] imageBytes, Rect cropRect, CancellationToken cancellationToken)
        {
            // Lossless crop encoding keeps OCR-sensitive glyph edges intact.
            int x = Math.Max(0, (int)Math.Floor(cropRect.X));
            int y = Math.Max(0, (int)Math.Floor(cropRect.Y));
            int w = Math.Max(1, (int)Math.Ceiling(cropRect.Width));
            int h = Math.Max(1, (int)Math.Ceiling(cropRect.Height));

            using var source = new InMemoryRandomAccessStream();
            await source.WriteAsync(imageBytes.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
            source.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(source).AsTask(cancellationToken).ConfigureAwait(false);
            int maxW = (int)decoder.PixelWidth;
            int maxH = (int)decoder.PixelHeight;
            x = Math.Clamp(x, 0, Math.Max(0, maxW - 1));
            y = Math.Clamp(y, 0, Math.Max(0, maxH - 1));
            w = Math.Clamp(w, 1, Math.Max(1, maxW - x));
            h = Math.Clamp(h, 1, Math.Max(1, maxH - y));

            var transform = new BitmapTransform
            {
                Bounds = new BitmapBounds
                {
                    X = (uint)x,
                    Y = (uint)y,
                    Width = (uint)w,
                    Height = (uint)h
                }
            };

            var cropped = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);

            return await EncodeSoftwareBitmapToPngAsync(cropped).ConfigureAwait(false);
        }

        private static async Task<byte[]?> EncodeSoftwareBitmapToPngAsync(SoftwareBitmap bitmap)
        {
            try
            {
                var encodable = bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8
                    ? SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                    : bitmap;

                using var ms = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
                encoder.SetSoftwareBitmap(encodable);
                await encoder.FlushAsync();

                var reader = new DataReader(ms.GetInputStreamAt(0));
                uint size = (uint)ms.Size;
                await reader.LoadAsync(size);
                var bytes = new byte[size];
                reader.ReadBytes(bytes);
                reader.DetachStream();
                return bytes;
            }
            catch { return null; }
        }

        private async Task<string> SendOllamaCropOcrRequestAsync(byte[] imageBytes, CancellationToken cancellationToken)
        {
            bool thinkEnabled = false;
            object? think = null;
            try
            {
                OllamaModelCapabilities capabilities = await GetOllamaModelCapabilitiesAsync(OllamaModel, cancellationToken).ConfigureAwait(false);
                if (capabilities.Thinking)
                {
                    thinkEnabled = BuildThinkParameter(OllamaThinkingLevel);
                    think = thinkEnabled;
                }
            }
            catch
            {
            }

            string cropPrompt = BuildHybridCropOcrPrompt(OllamaModel);

            var message = new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = cropPrompt,
                ["images"] = new[] { Convert.ToBase64String(imageBytes) }
            };

            var payload = new Dictionary<string, object?>
            {
                ["model"] = OllamaModel,
                ["stream"] = false,
                ["messages"] = new[] { message },
                ["options"] = new Dictionary<string, object?>
                {
                    ["temperature"] = 0.0,
                    ["num_ctx"] = OllamaOcrContextLength
                }
            };

            if (think != null)
                payload["think"] = think;

            using var request = new HttpRequestMessage(HttpMethod.Post, OllamaEndpoint.TrimEnd('/') + "/api/chat")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            using var requestLease = OllamaRequestLoadCoordinator.Acquire(
                thinkEnabled ? OllamaThinkingRequestTimeout : OllamaRequestTimeout,
                thinkEnabled);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(requestLease.EffectiveTimeout);

            using var response = await s_httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.Object
                && messageElement.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                return StripThinkingContent(contentElement.GetString());
            }

            if (root.TryGetProperty("response", out var responseElement)
                && responseElement.ValueKind == JsonValueKind.String)
            {
                return StripThinkingContent(responseElement.GetString());
            }

            return string.Empty;
        }

        private static string BuildHybridCropOcrPrompt(string? modelName)
        {
            if (IsGlmFamilyModel(modelName))
            {
                return "Text Recognition:";
            }

            return @"Recognize all readable text in this cropped manga region.
Consider typical Japanese manga speech-bubble text writing/reading order.
Return only the recognized text as plain text.
Do not return JSON, markdown, labels, or explanations.
If no readable text exists, return an empty string.";
        }

        private static async Task<(int Width, int Height)> TryGetOriginalImageSizeFromPathAsync(string imagePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return (0, 0);

            try
            {
                if (imagePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
                {
                    if (ImageCacheService.Instance.TryGetMemoryImageBytes(imagePath, out var memBytes)
                        && memBytes is { Length: > 0 })
                    {
                        return await TryGetImageSizeAsync(memBytes, cancellationToken).ConfigureAwait(false);
                    }

                    return (0, 0);
                }

                using var stream = await FileRandomAccessStream.OpenAsync(imagePath, FileAccessMode.Read).AsTask(cancellationToken).ConfigureAwait(false);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
                return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
            }
            catch
            {
                return (0, 0);
            }
        }

        private static async Task<byte[]?> TryGetOriginalImageBytesAndSizeAsync(string imagePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;

            if (imagePath.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
            {
                if (ImageCacheService.Instance.TryGetMemoryImageBytes(imagePath, out var memBytes)
                    && memBytes is { Length: > 0 })
                {
                    return memBytes;
                }

                return null;
            }

            try
            {
                return await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<(int Width, int Height)> TryGetImageSizeAsync(byte[] imageBytes, CancellationToken cancellationToken)
        {
            if (imageBytes == null || imageBytes.Length == 0) return (0, 0);

            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(imageBytes.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
                stream.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
                return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
            }
            catch
            {
                return (0, 0);
            }
        }

        private static async Task<byte[]?> EncodeSoftwareBitmapToJpegAsync(SoftwareBitmap bitmap)
        {
            try
            {
                var encodable = bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8
                    ? SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                    : bitmap;

                using var ms = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);
                encoder.SetSoftwareBitmap(encodable);
                await encoder.FlushAsync();

                var reader = new DataReader(ms.GetInputStreamAt(0));
                uint size = (uint)ms.Size;
                await reader.LoadAsync(size);
                var bytes = new byte[size];
                reader.ReadBytes(bytes);
                reader.DetachStream();
                return bytes;
            }
            catch { return null; }
        }

        private string BuildOllamaCacheKey(string path, string mode)
            => $"{path}|mode={mode}|endpoint={OllamaEndpoint}|model={OllamaModel}|thinking={OllamaThinkingLevel}|structured={OllamaStructuredOutputEnabled}|temp={OllamaTemperature:F3}";

        private static string TrimMarkdownCodeFence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string trimmed = text.Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

            int firstLineBreak = trimmed.IndexOf('\n');
            if (firstLineBreak < 0) return trimmed.Trim('`').Trim();

            string body = trimmed[(firstLineBreak + 1)..];
            int fenceEnd = body.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
                body = body[..fenceEnd];
            return body.Trim();
        }

        private static string StripThinkingContent(string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return string.Empty;

            string text = content;
            const string thinkOpenTag = "<think>";
            const string thinkCloseTag = "</think>";

            while (true)
            {
                int openIndex = text.IndexOf(thinkOpenTag, StringComparison.OrdinalIgnoreCase);
                if (openIndex < 0) break;

                int closeIndex = text.IndexOf(thinkCloseTag, openIndex + thinkOpenTag.Length, StringComparison.OrdinalIgnoreCase);
                if (closeIndex < 0)
                {
                    text = text[..openIndex];
                    break;
                }

                text = text.Remove(openIndex, (closeIndex + thinkCloseTag.Length) - openIndex);
            }

            return text.Trim();
        }

        private static string TruncateForDebug(string text, int maxLength = 8000)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;

            return text[..maxLength] + " ... (truncated)";
        }

        private async Task<string> SendOllamaVisionOcrRequestAsync(byte[] imageBytes, int imageWidth, int imageHeight, CancellationToken cancellationToken)
        {
            OllamaVisionModelFamily modelFamily = GetOllamaVisionModelFamily(OllamaModel);
            bool isQwenFamilyModel = modelFamily == OllamaVisionModelFamily.Qwen;
            bool isGemmaFamilyModel = modelFamily == OllamaVisionModelFamily.Gemma;
            object? think = null;
            bool thinkEnabled = false;
            OllamaModelCapabilities capabilities;
            try
            {
                capabilities = await GetOllamaModelCapabilitiesAsync(OllamaModel, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                capabilities = new OllamaModelCapabilities { Vision = true, Tools = true, Thinking = false };
            }
            if (capabilities.Thinking)
            {
                thinkEnabled = BuildThinkParameter(OllamaThinkingLevel);
                think = thinkEnabled;
            }

            string thinkInstruction = thinkEnabled
                ? "Think briefly and answer quickly. Keep internal reasoning minimal and do not include it in the output."
                : string.Empty;
            string structuredInstruction;
            if (isQwenFamilyModel)
            {
                structuredInstruction = @"Return JSON only.
Use a simple shape like { ""result"": [ { ""text_content"": ""..."", ""bbox_2d"": [ymin, xmin, ymax, xmax] } ] }.
Include OCR text and location information in reading order.";
            }
            else if (isGemmaFamilyModel)
            {
                structuredInstruction = @"Recognize text and return JSON with bounding boxes.
Use a simple shape like { ""result"": [ { ""label"": ""..."", ""box_2d"": [ymin, xmin, ymax, xmax] } ] }.";
            }
            else
            {
                structuredInstruction = @"Include OCR text and location information when available.
When returning box coordinates, use [ymin, xmin, ymax, xmax].
Use normalized coordinates on a 0..1000 scale with top-left as (0,0), right edge x=1000, bottom edge y=1000.";
            }
            string contentFilterInstruction = "Include only dialogue or narration text that should be read by the user. Exclude non-dialogue text such as sound effects, onomatopoeia, decorative background text, and UI/watermark text.";
            string speechBubbleInstruction = "If dialogue is inside a speech bubble, set bounding box to the full speech bubble area (bubble boundary), not just the tight text glyph bounds.";

            string prompt = $@"Extract only dialogue/narration text in speech bubble from the image.
{structuredInstruction}
{contentFilterInstruction}
{speechBubbleInstruction}
{thinkInstruction}";

            var message = new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = prompt,
                ["images"] = new[] { Convert.ToBase64String(imageBytes) }
            };

            var payload = new Dictionary<string, object?>
            {
                ["model"] = OllamaModel,
                ["stream"] = false,
                ["messages"] = new[] { message }
            };

            if (isQwenFamilyModel || isGemmaFamilyModel)
            {
                payload["format"] = BuildOcrJsonSchema();
            }
            else
            {
                payload["format"] = "json";
            }
            payload["options"] = new Dictionary<string, object?>
            {
                ["temperature"] = OllamaTemperature,
                ["num_ctx"] = OllamaOcrContextLength
            };

            if (think != null)
                payload["think"] = think;

            string payloadJson = JsonSerializer.Serialize(payload);
            Debug.WriteLine($"[OcrService][Ollama] Request JSON: {TruncateForDebug(payloadJson)}");

            using var request = new HttpRequestMessage(HttpMethod.Post, OllamaEndpoint.TrimEnd('/') + "/api/chat")
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };

            using var requestLease = OllamaRequestLoadCoordinator.Acquire(
                thinkEnabled ? OllamaThinkingRequestTimeout : OllamaRequestTimeout,
                thinkEnabled);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(requestLease.EffectiveTimeout);

            using var response = await s_httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            Debug.WriteLine("[OcrService][Ollama] Response JSON received.");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.Object
                && messageElement.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                string finalContent = StripThinkingContent(contentElement.GetString());
                Debug.WriteLine($"[OcrService][Ollama] Final content: {TruncateForDebug(finalContent)}");
                return finalContent;
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("response", out var responseElement)
                && responseElement.ValueKind == JsonValueKind.String)
            {
                string finalContent = StripThinkingContent(responseElement.GetString());
                Debug.WriteLine($"[OcrService][Ollama] Final content: {TruncateForDebug(finalContent)}");
                return finalContent;
            }

            return string.Empty;
        }

        private async Task<OllamaModelCapabilities> GetOllamaModelCapabilitiesAsync(string model, CancellationToken cancellationToken)
        {
            lock (_cacheGate)
            {
                if (_ollamaCapabilities.TryGetValue(model, out var cached))
                    return cached;
            }

            var payload = new { model };
            using var request = new HttpRequestMessage(HttpMethod.Post, OllamaEndpoint.TrimEnd('/') + "/api/show")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            using var requestLease = OllamaRequestLoadCoordinator.Acquire(OllamaRequestTimeout, thinkingEnabled: false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(requestLease.EffectiveTimeout);

            using var response = await s_httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool vision = HasCapability(root, "vision");
            bool tools = HasCapability(root, "tools") || HasCapability(root, "tool") || HasCapability(root, "tool_calling") || HasCapability(root, "tool-calling");
            bool thinking = HasCapability(root, "thinking") || HasCapability(root, "think");

            var capabilities = new OllamaModelCapabilities
            {
                Vision = vision,
                Tools = tools,
                Thinking = thinking
            };

            lock (_cacheGate)
            {
                _ollamaCapabilities[model] = capabilities;
            }

            return capabilities;
        }

        private static bool HasCapability(JsonElement root, string capability)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            bool CheckArray(JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Array) return false;
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        && string.Equals(item.GetString(), capability, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            if (root.TryGetProperty("capabilities", out var capabilitiesElement) && CheckArray(capabilitiesElement))
                return true;

            if (root.TryGetProperty("details", out var detailsElement)
                && detailsElement.ValueKind == JsonValueKind.Object
                && detailsElement.TryGetProperty("capabilities", out var nestedCapabilities)
                && CheckArray(nestedCapabilities))
                return true;

            return false;
        }

        private static bool BuildThinkParameter(string thinkingLevel)
        {
            return !NormalizeOllamaThinkingLevel(thinkingLevel).Equals("Off", StringComparison.OrdinalIgnoreCase);
        }

        private static object BuildOcrJsonSchema()
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new Dictionary<string, object?>
                {
                    ["result"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["minItems"] = 0,
                        ["items"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = false,
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["bbox_2d"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "array",
                                    ["items"] = new Dictionary<string, object?> { ["type"] = "number" },
                                    ["minItems"] = 4,
                                    ["maxItems"] = 4
                                },
                                ["box_2d"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "array",
                                    ["items"] = new Dictionary<string, object?> { ["type"] = "number" },
                                    ["minItems"] = 4,
                                    ["maxItems"] = 4
                                },
                                ["text_content"] = new Dictionary<string, object?> { ["type"] = "string" },
                                ["text"] = new Dictionary<string, object?> { ["type"] = "string" },
                                ["label"] = new Dictionary<string, object?> { ["type"] = "string" }
                            }
                        }
                    }
                },
                ["required"] = new[] { "result" }
            };
        }

        private static OllamaOcrResponse ParseStructuredOllamaResponse(
            string responseText,
            string modelName,
            int sourceImageWidth,
            int sourceImageHeight,
            int originalImageWidth,
            int originalImageHeight)
        {
            // Expected primary shape:
            // [ { "bbox_2d": [x1,y1,x2,y2], "text_content": "..." }, ... ]
            // Legacy fallback shape is also supported via x/y/width/height fields.
            string jsonText = TrimMarkdownCodeFence(responseText);
            if (string.IsNullOrWhiteSpace(jsonText))
                return new OllamaOcrResponse();

            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;

                JsonElement boxesElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    boxesElement = root;
                }
                else if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("boxes", out var rootBoxes)
                    && rootBoxes.ValueKind == JsonValueKind.Array)
                {
                    boxesElement = rootBoxes;
                }
                else if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("result", out var rootResult)
                    && rootResult.ValueKind == JsonValueKind.Array)
                {
                    boxesElement = rootResult;
                }
                else
                {
                    return new OllamaOcrResponse { Text = responseText.Trim() };
                }

                var rawBoxes = new List<RawOllamaBox>();
                OllamaVisionModelFamily modelFamily = GetOllamaVisionModelFamily(modelName);
                bool useNormalizedYxOrder = true; // All models now use [ymin, xmin, ymax, xmax] format
                foreach (var item in boxesElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    string boxText = item.TryGetProperty("text_content", out var bt) && bt.ValueKind == JsonValueKind.String
                        ? (bt.GetString() ?? string.Empty)
                        : (item.TryGetProperty("text", out var legacyBt) && legacyBt.ValueKind == JsonValueKind.String
                            ? (legacyBt.GetString() ?? string.Empty)
                            : (item.TryGetProperty("label", out var labelBt) && labelBt.ValueKind == JsonValueKind.String
                                ? (labelBt.GetString() ?? string.Empty)
                                : string.Empty));

                    if (!TryReadBbox2D(item, out var x1, out var y1, out var x2, out var y2, useNormalizedYxOrder))
                    {
                        double legacyX = ReadDouble(item, "x");
                        double legacyY = ReadDouble(item, "y");
                        double legacyWidth = Math.Max(0, ReadDouble(item, "width"));
                        double legacyHeight = Math.Max(0, ReadDouble(item, "height"));
                        x1 = legacyX;
                        y1 = legacyY;
                        x2 = legacyX + legacyWidth;
                        y2 = legacyY + legacyHeight;
                    }

                    rawBoxes.Add(new RawOllamaBox(boxText, x1, y1, x2, y2));
                }

                OllamaCoordinateSpace coordinateSpace = SelectOllamaCoordinateSpace(
                    rawBoxes,
                    modelName,
                    sourceImageWidth,
                    sourceImageHeight,
                    originalImageWidth,
                    originalImageHeight);

                var boxes = new List<BoundingBoxViewModel>(rawBoxes.Count);
                foreach (var raw in rawBoxes)
                {
                    var scaledRect = ScaleFromOllamaSpace(
                        raw.X1,
                        raw.Y1,
                        raw.X2,
                        raw.Y2,
                        sourceImageWidth,
                        sourceImageHeight,
                        originalImageWidth,
                        originalImageHeight,
                        coordinateSpace);
                    if (scaledRect.Width <= 0 || scaledRect.Height <= 0) continue;

                    boxes.Add(new BoundingBoxViewModel(raw.Text, scaledRect, originalImageWidth, originalImageHeight));
                }

                string text = boxes.Count > 0
                    ? string.Join(Environment.NewLine, boxes.Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t)))
                    : string.Empty;

                return new OllamaOcrResponse
                {
                    Text = text,
                    Boxes = boxes
                };
            }
            catch
            {
                return new OllamaOcrResponse { Text = responseText.Trim() };
            }
        }

        private static double ReadDouble(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object) return 0;
            if (!element.TryGetProperty(propertyName, out var value)) return 0;

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetDouble(out var n) ? n : 0,
                JsonValueKind.String => double.TryParse(value.GetString(), out var s) ? s : 0,
                _ => 0,
            };
        }

        private static bool TryReadBbox2D(JsonElement item, out double x1, out double y1, out double x2, out double y2, bool yxOrder)
        {
            x1 = y1 = x2 = y2 = 0;
            if (item.ValueKind != JsonValueKind.Object) return false;
            if (!(item.TryGetProperty("bbox_2d", out var bboxElement) || item.TryGetProperty("box_2d", out bboxElement))
                || bboxElement.ValueKind != JsonValueKind.Array)
                return false;

            var vals = new List<double>(4);
            foreach (var v in bboxElement.EnumerateArray())
            {
                vals.Add(v.ValueKind switch
                {
                    JsonValueKind.Number => v.TryGetDouble(out var n) ? n : 0,
                    JsonValueKind.String => double.TryParse(v.GetString(), out var s) ? s : 0,
                    _ => 0,
                });
            }

            if (vals.Count != 4) return false;
            if (yxOrder)
            {
                y1 = vals[0];
                x1 = vals[1];
                y2 = vals[2];
                x2 = vals[3];
            }
            else
            {
                x1 = vals[0];
                y1 = vals[1];
                x2 = vals[2];
                y2 = vals[3];
            }
            return true;
        }

        private static OllamaCoordinateSpace SelectOllamaCoordinateSpace(
            IReadOnlyList<RawOllamaBox> boxes,
            string modelName,
            int sourceImageWidth,
            int sourceImageHeight,
            int originalImageWidth,
            int originalImageHeight)
        {
            if (boxes.Count == 0 || sourceImageWidth <= 0 || sourceImageHeight <= 0 || originalImageWidth <= 0 || originalImageHeight <= 0)
                return OllamaCoordinateSpace.SourcePixel;

            OllamaVisionModelFamily modelFamily = GetOllamaVisionModelFamily(modelName);
            if (modelFamily != OllamaVisionModelFamily.Qwen)
            {
                Debug.WriteLine($"[OcrService][Ollama] Coordinate space selected: {OllamaCoordinateSpace.Normalized1000}, model={modelName}, family={modelFamily}, boxCount={boxes.Count}");
                return OllamaCoordinateSpace.Normalized1000;
            }

            // Prefer assumed smart-resize coordinate space when original dimensions can be
            // transformed with the same constraints used by common VLM preprocessors.
            var selected = TryComputeSmartResizeDimensions(originalImageWidth, originalImageHeight, out _, out _)
                ? OllamaCoordinateSpace.AssumedSmartResize
                : OllamaCoordinateSpace.SourcePixel;

            Debug.WriteLine($"[OcrService][Ollama] Coordinate space selected: {selected}, original={originalImageWidth}x{originalImageHeight}, boxCount={boxes.Count}");
            return selected;
        }

        private static bool TryComputeSmartResizeDimensions(int originalWidth, int originalHeight, out int resizedWidth, out int resizedHeight)
        {
            resizedWidth = 0;
            resizedHeight = 0;

            // Heuristic mirrors factor-aligned smart resize used by VLM preprocessors.
            const int factor = 28;
            const int shortestEdge = 64 << 10;
            const int longestEdge = 2 << 20;

            if (originalWidth < factor || originalHeight < factor)
                return false;

            int minSide = Math.Min(originalWidth, originalHeight);
            int maxSide = Math.Max(originalWidth, originalHeight);
            if (minSide <= 0 || (double)maxSide / minSide > 200.0)
                return false;

            static int RoundToFactor(int value, int step)
                => Math.Max(step, (int)Math.Round((double)value / step) * step);

            static int FloorToFactor(int value, int step)
                => Math.Max(step, value / step * step);

            static int CeilToFactor(int value, int step)
                => Math.Max(step, (int)Math.Ceiling((double)value / step) * step);

            int hBar = RoundToFactor(originalHeight, factor);
            int wBar = RoundToFactor(originalWidth, factor);
            double pixels = (double)hBar * wBar;

            if (pixels > longestEdge)
            {
                double beta = Math.Sqrt(pixels / longestEdge);
                hBar = FloorToFactor((int)(originalHeight / beta), factor);
                wBar = FloorToFactor((int)(originalWidth / beta), factor);
            }
            else if (pixels < shortestEdge)
            {
                double beta = Math.Sqrt((double)shortestEdge / pixels);
                hBar = CeilToFactor((int)(originalHeight * beta), factor);
                wBar = CeilToFactor((int)(originalWidth * beta), factor);
            }

            if (wBar <= 0 || hBar <= 0)
                return false;

            resizedWidth = wBar;
            resizedHeight = hBar;
            return true;
        }

        private static Rect ScaleFromOllamaSpace(
            double x1,
            double y1,
            double x2,
            double y2,
            int sourceImageWidth,
            int sourceImageHeight,
            int originalImageWidth,
            int originalImageHeight,
            OllamaCoordinateSpace coordinateSpace)
        {
            // Normalize from model coordinate space into original image pixel space.
            if (sourceImageWidth <= 0 || sourceImageHeight <= 0 || originalImageWidth <= 0 || originalImageHeight <= 0) return new Rect();

            double minX = Math.Min(x1, x2);
            double minY = Math.Min(y1, y2);
            double maxX = Math.Max(x1, x2);
            double maxY = Math.Max(y1, y2);

            double sourceW;
            double sourceH;

            if (coordinateSpace == OllamaCoordinateSpace.Normalized1000)
            {
                sourceW = 1000.0;
                sourceH = 1000.0;
            }
            else if (coordinateSpace == OllamaCoordinateSpace.AssumedSmartResize)
            {
                if (!TryComputeSmartResizeDimensions(originalImageWidth, originalImageHeight, out int resizedW, out int resizedH))
                    return new Rect();

                sourceW = resizedW;
                sourceH = resizedH;
            }
            else
            {
                sourceW = sourceImageWidth;
                sourceH = sourceImageHeight;
            }

            double clampedX = Math.Clamp(minX, 0, sourceW);
            double clampedY = Math.Clamp(minY, 0, sourceH);
            double clampedMaxX = Math.Clamp(maxX, 0, sourceW);
            double clampedMaxY = Math.Clamp(maxY, 0, sourceH);
            double clampedW = clampedMaxX - clampedX;
            double clampedH = clampedMaxY - clampedY;
            if (clampedW <= 0 || clampedH <= 0) return new Rect();

            double sx = clampedX / sourceW * originalImageWidth;
            double sy = clampedY / sourceH * originalImageHeight;
            double sw = clampedW / sourceW * originalImageWidth;
            double sh = clampedH / sourceH * originalImageHeight;
            return new Rect(sx, sy, sw, sh);
        }

        private enum OllamaVisionModelFamily
        {
            Qwen,
            Gemma,
            Other
        }

        private static OllamaVisionModelFamily GetOllamaVisionModelFamily(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return OllamaVisionModelFamily.Other;

            string normalized = modelName.Trim().ToLowerInvariant();
            if (normalized.StartsWith("qwen3.5", StringComparison.Ordinal)
                || normalized.StartsWith("qwen3_5", StringComparison.Ordinal)
                || normalized.StartsWith("qwen-3.5", StringComparison.Ordinal)
                || normalized.StartsWith("qwen-3_5", StringComparison.Ordinal)
                || normalized.StartsWith("qwen3-vl", StringComparison.Ordinal)
                || normalized.StartsWith("qwen3_vl", StringComparison.Ordinal)
                || normalized.StartsWith("qwen-3-vl", StringComparison.Ordinal)
                || normalized.StartsWith("qwen-3_vl", StringComparison.Ordinal))
            {
                return OllamaVisionModelFamily.Qwen;
            }

            if (normalized.StartsWith("gemma3", StringComparison.Ordinal)
                || normalized.StartsWith("gemma3n", StringComparison.Ordinal)
                || normalized.StartsWith("gemma4", StringComparison.Ordinal))
            {
                return OllamaVisionModelFamily.Gemma;
            }

            return OllamaVisionModelFamily.Other;
        }

        private static bool IsGlmFamilyModel(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return false;

            string normalized = modelName.Trim().ToLowerInvariant();
            return normalized.StartsWith("glm", StringComparison.Ordinal)
                || normalized.Contains("/glm", StringComparison.Ordinal)
                || normalized.Contains("-glm", StringComparison.Ordinal);
        }
    }
}
