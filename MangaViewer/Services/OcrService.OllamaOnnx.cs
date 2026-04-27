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
            return TryGetCachedOcr(imagePath, mode, out _, requireCompleteHybrid: true);
        }

        public bool TryGetCachedCurrentBackendOcr(string imagePath, out OllamaOcrResponse response)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                response = new OllamaOcrResponse();
                return false;
            }

            string mode = Backend == OcrBackend.Hybrid ? "hybrid" : "vlm";
            return TryGetCachedOcr(imagePath, mode, out response, requireCompleteHybrid: true);
        }

        private bool TryGetCachedOcr(string imagePath, string mode, out OllamaOcrResponse response, bool requireCompleteHybrid)
        {
            string cacheKey = BuildOllamaCacheKey(imagePath, mode);
            lock (_cacheGate)
            {
                if (!_ollamaCache.TryGetValue(cacheKey, out var cached)
                    || !ShouldReuseCachedOcrResult(cached)
                    || (requireCompleteHybrid && string.Equals(mode, "hybrid", StringComparison.OrdinalIgnoreCase) && HasIncompleteHybridTextBoxes(cached)))
                {
                    response = new OllamaOcrResponse();
                    return false;
                }

                response = CloneOllamaOcrResponse(cached);
                return true;
            }
        }

        private static bool ShouldReuseCachedOcrResult(OllamaOcrResponse? response)
            => response != null && response.IsSuccessful;

        private static bool HasIncompleteHybridTextBoxes(OllamaOcrResponse? response)
            => response != null && response.Boxes.Any(IsIncompleteHybridTextBox);

        private static bool IsIncompleteHybridTextBox(BoundingBoxViewModel box)
            => box != null && string.IsNullOrWhiteSpace(box.Text);

        private static List<BoundingBoxViewModel> GetIncompleteHybridTextBoxes(IReadOnlyList<BoundingBoxViewModel> boxes)
            => boxes.Where(IsIncompleteHybridTextBox).ToList();

        private static OllamaOcrResponse CloneOllamaOcrResponse(OllamaOcrResponse response)
            => new()
            {
                Text = response.Text,
                Boxes = response.Boxes.Select(CloneBoundingBox).ToList(),
                IsSuccessful = response.IsSuccessful,
                UsedFallback = response.UsedFallback,
                StatusMessage = response.StatusMessage
            };

        private static BoundingBoxViewModel CloneBoundingBox(BoundingBoxViewModel box)
            => new(box.Text, box.OriginalBoundingBox, box.ImagePixelWidth, box.ImagePixelHeight);

        public Task<string> GetHybridBoxTextAsync(string imagePath, BoundingBoxViewModel box, CancellationToken cancellationToken)
            => GetOrRequestHybridBoxTextAsync(imagePath, box, null, cancellationToken);

        private async Task<string> GetOrRequestHybridBoxTextAsync(
            string imagePath,
            BoundingBoxViewModel box,
            byte[]? originalBytes,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || box == null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(box.Text))
                return box.Text.Trim();

            string cacheKey = BuildHybridBoxTextCacheKey(imagePath, box);
            Task<string>? pendingTask;

            lock (_cacheGate)
            {
                if (_hybridBoxTextCache.TryGetValue(cacheKey, out var cachedText) && !string.IsNullOrWhiteSpace(cachedText))
                    return cachedText;

                if (!_hybridBoxTextRequests.TryGetValue(cacheKey, out pendingTask))
                {
                    pendingTask = RecognizeHybridBoxTextCoreAsync(imagePath, box, originalBytes, cancellationToken);
                    _hybridBoxTextRequests[cacheKey] = pendingTask;
                }
            }

            try
            {
                return await pendingTask.ConfigureAwait(false);
            }
            finally
            {
                lock (_cacheGate)
                {
                    if (_hybridBoxTextRequests.TryGetValue(cacheKey, out var currentTask)
                        && ReferenceEquals(currentTask, pendingTask))
                    {
                        _hybridBoxTextRequests.Remove(cacheKey);
                    }
                }
            }
        }

        private async Task<string> RecognizeHybridBoxTextCoreAsync(
            string imagePath,
            BoundingBoxViewModel box,
            byte[]? originalBytes,
            CancellationToken cancellationToken)
        {
            byte[]? sourceBytes = originalBytes;
            if (sourceBytes == null || sourceBytes.Length == 0)
                sourceBytes = await TryGetOriginalImageBytesAndSizeAsync(imagePath, cancellationToken).ConfigureAwait(false);

            if (sourceBytes == null || sourceBytes.Length == 0)
                return string.Empty;

            byte[]? cropBytes = await EncodeCropToLosslessAsync(sourceBytes, box.OriginalBoundingBox, cancellationToken).ConfigureAwait(false);
            if (cropBytes == null || cropBytes.Length == 0)
                return string.Empty;

            string cropText = await SendOllamaCropOcrRequestAsync(cropBytes, cancellationToken).ConfigureAwait(false);
            string normalizedText = string.IsNullOrWhiteSpace(cropText) ? string.Empty : cropText.Trim();
            string cacheKey = BuildHybridBoxTextCacheKey(imagePath, box);

            lock (_cacheGate)
            {
                if (string.IsNullOrWhiteSpace(normalizedText))
                    _hybridBoxTextCache.Remove(cacheKey);
                else
                    _hybridBoxTextCache[cacheKey] = normalizedText;
            }

            return normalizedText;
        }

        private sealed class OllamaVlmOcrBackend
        {
            private readonly OcrService _owner;

            public OllamaVlmOcrBackend(OcrService owner)
            {
                _owner = owner;
            }

            public async Task<OllamaOcrResponse> GetOcrAsync(string imagePath, CancellationToken cancellationToken, bool forceRefresh)
            {
                if (string.IsNullOrWhiteSpace(imagePath)) return new OllamaOcrResponse();

                string cacheKey = _owner.BuildOllamaCacheKey(imagePath, "vlm");
                if (!forceRefresh && _owner.TryGetCachedOcr(imagePath, "vlm", out var cached, requireCompleteHybrid: false))
                    return cached;

                try
                {
                    int sourceImageWidth = 0;
                    int sourceImageHeight = 0;
                    int originalImageWidth = 0;
                    int originalImageHeight = 0;

                    var sourceImage = await TryLoadSourceImageAsync(imagePath, cancellationToken).ConfigureAwait(false);
                    byte[]? imageBytes = sourceImage.Bytes;
                    sourceImageWidth = sourceImage.Width;
                    sourceImageHeight = sourceImage.Height;
                    originalImageWidth = sourceImage.Width;
                    originalImageHeight = sourceImage.Height;
                    if (imageBytes == null || imageBytes.Length == 0 || sourceImageWidth <= 0 || sourceImageHeight <= 0)
                    {
                        var bitmap = await _owner._decoder.DecodeForOcrAsync(imagePath, cancellationToken).ConfigureAwait(false);
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

                    string responseText = await _owner.SendOllamaVisionOcrRequestAsync(imageBytes, sourceImageWidth, sourceImageHeight, cancellationToken).ConfigureAwait(false);

                    OllamaOcrResponse result = ParseStructuredOllamaResponse(
                        responseText,
                        _owner.OllamaModel,
                        sourceImageWidth,
                        sourceImageHeight,
                        originalImageWidth,
                        originalImageHeight);

                    var cacheableResult = CloneOllamaOcrResponse(result);
                    lock (_owner._cacheGate)
                    {
                        if (ShouldReuseCachedOcrResult(cacheableResult))
                            _owner._ollamaCache[cacheKey] = cacheableResult;
                        else
                            _owner._ollamaCache.Remove(cacheKey);
                    }

                    return result;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error(ex, "[OcrService] Ollama OCR failed");
                    return new OllamaOcrResponse
                    {
                        IsSuccessful = false,
                        StatusMessage = ex.Message
                    };
                }
            }

            private static async Task<(byte[]? Bytes, int Width, int Height)> TryLoadSourceImageAsync(string imagePath, CancellationToken cancellationToken)
            {
                byte[]? imageBytes = await TryGetOriginalImageBytesAndSizeAsync(imagePath, cancellationToken).ConfigureAwait(false);
                if (imageBytes != null && imageBytes.Length > 0)
                {
                    var originalSize = await TryGetImageSizeAsync(imageBytes, cancellationToken).ConfigureAwait(false);
                    if (originalSize.Width > 0 && originalSize.Height > 0)
                        return (imageBytes, originalSize.Width, originalSize.Height);
                }

                return (imageBytes, 0, 0);
            }
        }

        private sealed class HybridOcrBackend
        {
            private readonly OcrService _owner;

            public HybridOcrBackend(OcrService owner)
            {
                _owner = owner;
            }

            public async Task<OllamaOcrResponse> GetOcrAsync(
                string imagePath,
                CancellationToken cancellationToken,
                bool forceRefresh,
                Action<IReadOnlyList<BoundingBoxViewModel>>? onLayoutBoxesReady)
            {
                if (string.IsNullOrWhiteSpace(imagePath)) return new OllamaOcrResponse();

                string cacheKey = _owner.BuildOllamaCacheKey(imagePath, "hybrid");
                OllamaOcrResponse? cachedHybrid = null;
                if (!forceRefresh)
                {
                    if (_owner.TryGetCachedOcr(imagePath, "hybrid", out var cached, requireCompleteHybrid: false))
                        cachedHybrid = cached;

                    if (cachedHybrid != null)
                    {
                        onLayoutBoxesReady?.Invoke(cachedHybrid.Boxes);

                        var pendingBoxes = GetIncompleteHybridTextBoxes(cachedHybrid.Boxes);
                        if (pendingBoxes.Count == 0)
                            return cachedHybrid;

                        try
                        {
                            var sourceImage = await _owner.TryGetHybridSourceImageAsync(imagePath, cancellationToken).ConfigureAwait(false);
                            if (sourceImage.Bytes != null && sourceImage.Width > 0 && sourceImage.Height > 0)
                            {
                                await _owner.RecognizeLayoutBoxesWithGlmOcrAsync(
                                    imagePath,
                                    sourceImage.Bytes,
                                    pendingBoxes,
                                    cancellationToken).ConfigureAwait(false);
                            }

                            var resumed = BuildOllamaOcrResponse(cachedHybrid.Boxes);
                            var cacheableResumed = CloneOllamaOcrResponse(resumed);
                            lock (_owner._cacheGate)
                            {
                                if (ShouldReuseCachedOcrResult(cacheableResumed))
                                    _owner._ollamaCache[cacheKey] = cacheableResumed;
                                else
                                    _owner._ollamaCache.Remove(cacheKey);
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
                    var sourceImage = await _owner.TryGetHybridSourceImageAsync(imagePath, cancellationToken).ConfigureAwait(false);
                    if (sourceImage.Bytes == null || sourceImage.Width <= 0 || sourceImage.Height <= 0)
                    {
                        return await _owner.BuildHybridFallbackResponseAsync(
                            imagePath,
                            "Hybrid OCR source image unavailable",
                            cancellationToken,
                            forceRefresh,
                            null).ConfigureAwait(false);
                    }

                    var layoutBoxes = await _owner.DetectLayoutWithOnnxAsync(
                        sourceImage.Bytes,
                        sourceImage.Width,
                        sourceImage.Height,
                        cancellationToken).ConfigureAwait(false);
                    if (layoutBoxes.Count == 0)
                    {
                        return await _owner.BuildHybridFallbackResponseAsync(
                            imagePath,
                            "Hybrid OCR ONNX layout detection returned no boxes",
                            cancellationToken,
                            forceRefresh,
                            null).ConfigureAwait(false);
                    }

                    var resultBoxes = BuildLayoutBoundingBoxes(layoutBoxes, sourceImage.Width, sourceImage.Height);
                    onLayoutBoxesReady?.Invoke(resultBoxes);

                    await _owner.RecognizeLayoutBoxesWithGlmOcrAsync(
                        imagePath,
                        sourceImage.Bytes,
                        resultBoxes,
                        cancellationToken).ConfigureAwait(false);

                    var result = BuildOllamaOcrResponse(resultBoxes);
                    var cacheableResult = CloneOllamaOcrResponse(result);

                    lock (_owner._cacheGate)
                    {
                        if (ShouldReuseCachedOcrResult(cacheableResult))
                            _owner._ollamaCache[cacheKey] = cacheableResult;
                        else
                            _owner._ollamaCache.Remove(cacheKey);
                    }

                    return result;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error(ex, "[OcrService] Hybrid OCR failed");
                    return await _owner.BuildHybridFallbackResponseAsync(
                        imagePath,
                        "Hybrid OCR failed while executing ONNX layout detection",
                        cancellationToken,
                        forceRefresh,
                        ex).ConfigureAwait(false);
                }
            }
        }

        private sealed class DocLayoutOnnxBackend
        {
            private readonly OcrService _owner;

            public DocLayoutOnnxBackend(OcrService owner)
            {
                _owner = owner;
            }

            public async Task<List<DocLayoutBox>> DetectLayoutAsync(byte[] imageBytes, int imageWidth, int imageHeight, CancellationToken cancellationToken)
            {
                try
                {
                    return await RunPrimaryDetectionAsync(imageBytes, imageWidth, imageHeight, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OcrService][ONNX] Primary EP layout detection failed. Retrying with CPU EP. Error={ex.Message}");
                    return await RunCpuFallbackDetectionAsync(imageBytes, imageWidth, imageHeight, cancellationToken).ConfigureAwait(false);
                }
            }

            public string ResolveLoadModelPath()
            {
                if (_owner.OnnxUseEpContextModel)
                {
                    string epContextPath = _owner.GetDocLayoutEpContextModelPath();
                    if (File.Exists(epContextPath))
                        return epContextPath;
                }

                return _owner.GetDocLayoutModelPath();
            }

            public InferenceSession? GetOrCreateSession()
            {
                string modelPath = ResolveLoadModelPath();
                if (!File.Exists(modelPath))
                {
                    Debug.WriteLine($"[OcrService][ONNX] Model file not found. Path={modelPath}");
                    return null;
                }

                lock (_owner._docLayoutSessionGate)
                {
                    if (_owner._docLayoutSession != null
                        && string.Equals(_owner._docLayoutModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[OcrService][ONNX] Model already loaded. Success=True, Path={modelPath}");
                        return _owner._docLayoutSession;
                    }

                    Debug.WriteLine($"[OcrService][ONNX] Loading model... Path={modelPath}");
                    try
                    {
                        _owner._docLayoutSession?.Dispose();
                        var sessionOptions = CreateSessionOptions();
                        _owner._docLayoutSession = new InferenceSession(modelPath, sessionOptions);
                        _owner._docLayoutModelPath = modelPath;
                        _owner._docLayoutRuntimeEpLogged = false;
                        Debug.WriteLine($"[OcrService][ONNX] Model loaded. Success=True, Inputs={_owner._docLayoutSession.InputMetadata.Count}, Outputs={_owner._docLayoutSession.OutputMetadata.Count}");
                        return _owner._docLayoutSession;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OcrService][ONNX] Model loaded. Success=False, Error={ex.Message}");
                        throw;
                    }
                }
            }

            public void InvalidateSession()
            {
                lock (_owner._docLayoutSessionGate)
                {
                    _owner._docLayoutSession?.Dispose();
                    _owner._docLayoutSession = null;
                    _owner._docLayoutModelPath = null;
                    _owner._docLayoutRuntimeEpLogged = false;
                }
            }

            public SessionOptions CreateSessionOptions()
            {
                var sessionOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    EnableMemoryPattern = true,
                    EnableCpuMemArena = true
                };

                var requestedProviders = ResolveRequestedExecutionProviders();
                if (requestedProviders.Count > 0)
                    Debug.WriteLine($"[OcrService][ONNX] Requested EP chain: {string.Join(" -> ", requestedProviders)}");
                else
                    Debug.WriteLine("[OcrService][ONNX] Requested EP chain: <none> (runtime default registration will be used)");

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

            public async Task<bool> CompileEpContextModelAsync(CancellationToken cancellationToken)
            {
                string inputModelPath = _owner.GetDocLayoutModelPath();
                if (!File.Exists(inputModelPath))
                {
                    _owner.OnnxExecutionProviderStatus = "EP context compile skipped: model file not found";
                    return false;
                }

                string outputModelPath = _owner.GetDocLayoutEpContextModelPath();
                string? outputDirectory = Path.GetDirectoryName(outputModelPath);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                    Directory.CreateDirectory(outputDirectory);

                await _owner.EnsureOnnxExecutionProvidersReadyAsync(cancellationToken, force: false).ConfigureAwait(false);

                bool compiledByCompileApi = false;
                try
                {
                    compiledByCompileApi = TryCompileEpContextWithCompileApi(inputModelPath, outputModelPath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OcrService][ONNX] Compile API EP context generation failed: {ex.Message}");
                }

                if (!compiledByCompileApi)
                {
                    try
                    {
                        using var options = CreateSessionOptions();
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
                    _owner.OnnxExecutionProviderStatus = "EP context compile failed (Compile API unavailable and optimized-model fallback failed)";
                    return false;
                }

                InvalidateSession();
                _owner.OnnxExecutionProviderStatus = $"EP context model ready: {Path.GetFileName(outputModelPath)}";
                return true;
            }

            public void TryLogRuntimeExecutionProviders(InferenceSession session)
            {
                lock (_owner._docLayoutSessionGate)
                {
                    if (_owner._docLayoutRuntimeEpLogged)
                        return;

                    _owner._docLayoutRuntimeEpLogged = true;
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

            private async Task<List<DocLayoutBox>> RunPrimaryDetectionAsync(byte[] imageBytes, int imageWidth, int imageHeight, CancellationToken cancellationToken)
            {
                await _owner.EnsureOnnxExecutionProvidersReadyAsync(cancellationToken, force: false).ConfigureAwait(false);

                if (_owner.OnnxUseEpContextModel
                    && _owner.OnnxAutoCompileEpContextModel
                    && _owner.IsDocLayoutModelInstalled()
                    && !_owner.IsDocLayoutEpContextModelInstalled())
                {
                    await CompileEpContextModelAsync(cancellationToken).ConfigureAwait(false);
                }

                Debug.WriteLine($"[OcrService][ONNX] Bounding-box detection request started. InputImage={imageWidth}x{imageHeight}, Bytes={imageBytes?.Length ?? 0}");

                var session = GetOrCreateSession();
                if (session == null)
                {
                    Debug.WriteLine("[OcrService][ONNX] Bounding-box detection completed. Returned=False, BoxCount=0 (session unavailable)");
                    return new List<DocLayoutBox>();
                }

                return await _owner.RunDocLayoutDetectionCoreAsync(session, imageBytes, imageWidth, imageHeight, cancellationToken).ConfigureAwait(false);
            }

            private async Task<List<DocLayoutBox>> RunCpuFallbackDetectionAsync(byte[] imageBytes, int imageWidth, int imageHeight, CancellationToken cancellationToken)
            {
                string modelPath = _owner.GetDocLayoutModelPath();
                if (!File.Exists(modelPath))
                    modelPath = ResolveLoadModelPath();

                if (!File.Exists(modelPath))
                {
                    Debug.WriteLine($"[OcrService][ONNX] CPU fallback model file not found. Path={modelPath}");
                    return new List<DocLayoutBox>();
                }

                using var options = CreateCpuOnlyDocLayoutSessionOptions();
                using var session = new InferenceSession(modelPath, options);
                Debug.WriteLine($"[OcrService][ONNX] CPU fallback session loaded. Path={modelPath}");
                return await _owner.RunDocLayoutDetectionCoreAsync(session, imageBytes, imageWidth, imageHeight, cancellationToken).ConfigureAwait(false);
            }

            private Dictionary<string, string>? BuildExecutionProviderOptions(string providerName)
            {
                if (string.IsNullOrWhiteSpace(providerName))
                    return null;

                if (!providerName.Contains("tensorrtrtx", StringComparison.OrdinalIgnoreCase))
                    return null;

                var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["enable_cuda_graph"] = _owner.OnnxTrtRtxEnableCudaGraph ? "1" : "0"
                };

                string runtimeCachePath = _owner.OnnxTrtRtxRuntimeCachePath?.Trim() ?? string.Empty;
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

                if (_owner.OnnxExecutionProviderMode == OnnxEpRegistrationMode.Manual && !string.IsNullOrWhiteSpace(_owner.OnnxExecutionProviderManualList))
                {
                    providers.AddRange(_owner.OnnxExecutionProviderManualList
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                    return providers;
                }

                var compatible = _owner.GetCompatibleOnnxExecutionProviders();
                foreach (var provider in compatible)
                {
                    if (provider.ReadyState.Contains("Ready", StringComparison.OrdinalIgnoreCase))
                        providers.Add(provider.Name);
                }

                return providers;
            }

            private bool TryCompileEpContextWithCompileApi(string inputModelPath, string outputModelPath)
            {
                var ortAssembly = typeof(InferenceSession).Assembly;
                Type? modelCompilerType = ortAssembly.GetType("Microsoft.ML.OnnxRuntime.ModelCompiler");
                if (modelCompilerType == null)
                    return false;

                using var options = CreateSessionOptions();
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
        }

        public async Task<OllamaOcrResponse> GetOllamaOcrAsync(string imagePath, CancellationToken cancellationToken, bool forceRefresh = false)
            => await _ollamaVlmBackend.GetOcrAsync(imagePath, cancellationToken, forceRefresh).ConfigureAwait(false);

        public async Task<OllamaOcrResponse> GetHybridOcrAsync(
            string imagePath,
            CancellationToken cancellationToken,
            bool forceRefresh = false,
            Action<IReadOnlyList<BoundingBoxViewModel>>? onLayoutBoxesReady = null)
            => await _hybridOcrBackend.GetOcrAsync(imagePath, cancellationToken, forceRefresh, onLayoutBoxesReady).ConfigureAwait(false);

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
                    IsSuccessful = false,
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
                    IsSuccessful = fallback.IsSuccessful,
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
            => await _docLayoutOnnxBackend.DetectLayoutAsync(imageBytes, imageWidth, imageHeight, cancellationToken).ConfigureAwait(false);

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
            string imagePath,
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
                using var boxCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                boxCts.CancelAfter(HybridCropRequestTimeout);

                await throttler.WaitAsync(boxCts.Token).ConfigureAwait(false);
                try
                {
                    boxCts.Token.ThrowIfCancellationRequested();
                    string cropText = await GetOrRequestHybridBoxTextAsync(imagePath, box, originalBytes, boxCts.Token).ConfigureAwait(false);
                    box.Text = string.IsNullOrWhiteSpace(cropText) ? string.Empty : cropText.Trim();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    box.Text = string.Empty;
                    Debug.WriteLine("[OcrService][HybridCrop] Box OCR canceled due to per-box timeout.");
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    box.Text = string.Empty;
                    Debug.WriteLine($"[OcrService][HybridCrop] Box OCR failed and was skipped. Error={ex.Message}");
                }
                finally
                {
                    throttler.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private string BuildHybridBoxTextCacheKey(string imagePath, BoundingBoxViewModel box)
            => $"{BuildOllamaCacheKey(imagePath, "hybrid")}|crop={box.OriginalX:F3},{box.OriginalY:F3},{box.OriginalW:F3},{box.OriginalH:F3}|img={box.ImagePixelWidth}x{box.ImagePixelHeight}";

        private static OllamaOcrResponse BuildOllamaOcrResponse(List<BoundingBoxViewModel> resultBoxes)
        {
            return new OllamaOcrResponse
            {
                Text = string.Join(Environment.NewLine, resultBoxes.Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t))),
                Boxes = resultBoxes,
                IsSuccessful = true
            };
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

        private void InvalidateDocLayoutSession()
            => _docLayoutOnnxBackend.InvalidateSession();

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

        public async Task<bool> CompileDocLayoutEpContextModelAsync(CancellationToken cancellationToken)
            => await _docLayoutOnnxBackend.CompileEpContextModelAsync(cancellationToken).ConfigureAwait(false);

        private void TryLogDocLayoutRuntimeExecutionProviders(InferenceSession session)
            => _docLayoutOnnxBackend.TryLogRuntimeExecutionProviders(session);

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
            return await ExecuteWithActiveOcrRequestAsync(cancellationToken, async (requestToken, activeRequest) =>
            {
                var prepared = await PrepareOllamaChatRequestAsync(requestToken).ConfigureAwait(false);
                string endpoint = prepared.Endpoint;
                LlmApiFlavor flavor = prepared.Flavor;
                bool thinkEnabled = prepared.ThinkEnabled;
                object? think = prepared.Think;

                string cropPrompt = OllamaOcrProtocol.BuildHybridCropPrompt(OllamaModel);

                if (flavor == LlmApiFlavor.OpenAiCompatible)
                {
                    Debug.WriteLine($"[OcrService][HybridCrop] Using llama-server OpenAI-compatible route. Model={OllamaModel}");
                    return await SendOpenAiCompatibleCropOcrRequestAsync(
                        endpoint,
                        cropPrompt,
                        imageBytes,
                        thinkEnabled,
                        activeRequest,
                        requestToken).ConfigureAwait(false);
                }

                var message = BuildOllamaVisionMessage(cropPrompt, imageBytes);

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

                return await ExecuteNativeOllamaChatRequestAsync(
                    endpoint,
                    payload,
                    thinkEnabled,
                    HybridCropRequestTimeout,
                    "[OcrService][HybridCrop]",
                    NormalizeHybridCropOcrText,
                    requestToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
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

        private static string NormalizeHybridCropOcrText(string? content)
        {
            string text = StripThinkingContent(content);
            text = TrimMarkdownCodeFence(text);

            if (string.Equals(text, "markdown", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "text", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "plaintext", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "plain text", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            return text;
        }

        private static string TruncateForDebug(string text, int maxLength = 8000)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;

            return text[..maxLength] + " ... (truncated)";
        }

        private async Task<T> ExecuteWithActiveOcrRequestAsync<T>(
            CancellationToken cancellationToken,
            Func<CancellationToken, ActiveOcrRequest, Task<T>> action)
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var activeRequest = RegisterActiveOcrRequest(requestCts);

            try
            {
                return await action(requestCts.Token, activeRequest).ConfigureAwait(false);
            }
            finally
            {
                UnregisterActiveOcrRequest(activeRequest);
            }
        }

        private async Task<(string Endpoint, LlmApiFlavor Flavor, bool ThinkEnabled, object? Think)> PrepareOllamaChatRequestAsync(CancellationToken cancellationToken)
        {
            string endpoint = LlmEndpointCompatibility.NormalizeEndpoint(OllamaEndpoint);
            LlmApiFlavor flavor = await LlmEndpointCompatibility.DetectApiFlavorAsync(s_httpClient, endpoint, cancellationToken).ConfigureAwait(false);
            await LlmEndpointCompatibility.EnsureModelLoadedAsync(s_httpClient, endpoint, OllamaModel, cancellationToken).ConfigureAwait(false);

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

            return (endpoint, flavor, thinkEnabled, think);
        }

        private static Dictionary<string, object?> BuildOllamaVisionMessage(string prompt, byte[] imageBytes)
        {
            return new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = prompt,
                ["images"] = new[] { Convert.ToBase64String(imageBytes) }
            };
        }

        private static object[] BuildOpenAiCompatibleImageContent(string prompt, byte[] imageBytes, string mimeType)
        {
            return
            [
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = prompt
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object?>
                    {
                        ["url"] = $"data:{mimeType};base64,{Convert.ToBase64String(imageBytes)}"
                    }
                }
            ];
        }

        private async Task<string> ExecuteOpenAiCompatibleChatRequestAsync(
            string endpoint,
            Dictionary<string, object?> payload,
            bool thinkEnabled,
            TimeSpan maxTimeout,
            string requestLogPrefix,
            Func<string?, string> normalizeContent,
            ActiveOcrRequest activeRequest,
            CancellationToken cancellationToken)
        {
            LlmEndpointCompatibility.ApplyOpenAiThinkingOptions(payload, thinkEnabled);

            using var requestLease = await OllamaRequestLoadCoordinator.AcquireAsync(
                thinkEnabled ? OllamaThinkingRequestTimeout : OllamaRequestTimeout,
                thinkEnabled,
                cancellationToken).ConfigureAwait(false);
            payload["cache_prompt"] = false;
            bool useManagedSlots = requestLease.SlotId >= 0
                && LlmEndpointCompatibility.SupportsManagedSlots(endpoint, OllamaModel);
            if (useManagedSlots && requestLease.SlotEraseEnabled)
                useManagedSlots = await LlmEndpointCompatibility.TryEraseSlotAsync(s_httpClient, endpoint, requestLease.SlotId, OllamaModel).ConfigureAwait(false);

            if (useManagedSlots)
            {
                ConfigureActiveOcrRequestSlotClear(activeRequest, endpoint, requestLease);
                payload["id_slot"] = requestLease.SlotId;
            }

            string payloadJson = JsonSerializer.Serialize(payload);
            Debug.WriteLine($"{requestLogPrefix} Request JSON: {TruncateForDebug(payloadJson)}");

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/v1/chat/completions")
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(requestLease.EffectiveTimeout < maxTimeout ? requestLease.EffectiveTimeout : maxTimeout);

            try
            {
                using var response = await s_httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                string finalContent = normalizeContent(LlmEndpointCompatibility.ExtractAssistantText(doc.RootElement));
                Debug.WriteLine($"{requestLogPrefix} Final content: {TruncateForDebug(finalContent)}");
                return finalContent;
            }
            catch
            {
                if (requestLease.SlotEraseEnabled
                    && requestLease.SlotId >= 0
                    && LlmEndpointCompatibility.SupportsManagedSlots(endpoint, OllamaModel))
                {
                    await LlmEndpointCompatibility.TryEraseSlotAsync(s_httpClient, endpoint, requestLease.SlotId, OllamaModel).ConfigureAwait(false);
                }

                throw;
            }
        }

        private async Task<string> ExecuteNativeOllamaChatRequestAsync(
            string endpoint,
            Dictionary<string, object?> payload,
            bool thinkEnabled,
            TimeSpan maxTimeout,
            string requestLogPrefix,
            Func<string?, string> normalizeContent,
            CancellationToken cancellationToken,
            bool logResponseReceived = false)
        {
            string payloadJson = JsonSerializer.Serialize(payload);
            Debug.WriteLine($"{requestLogPrefix} Request JSON: {TruncateForDebug(payloadJson)}");

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/api/chat")
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };

            using var requestLease = await OllamaRequestLoadCoordinator.AcquireAsync(
                thinkEnabled ? OllamaThinkingRequestTimeout : OllamaRequestTimeout,
                thinkEnabled,
                cancellationToken).ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(requestLease.EffectiveTimeout < maxTimeout ? requestLease.EffectiveTimeout : maxTimeout);

            using var response = await s_httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            if (logResponseReceived)
                Debug.WriteLine($"{requestLogPrefix} Response JSON received.");

            using var doc = JsonDocument.Parse(json);
            string finalContent = normalizeContent(LlmEndpointCompatibility.ExtractAssistantText(doc.RootElement));
            Debug.WriteLine($"{requestLogPrefix} Final content: {TruncateForDebug(finalContent)}");
            return finalContent;
        }

        private async Task<string> SendOllamaVisionOcrRequestAsync(byte[] imageBytes, int imageWidth, int imageHeight, CancellationToken cancellationToken)
        {
            return await ExecuteWithActiveOcrRequestAsync(cancellationToken, async (requestToken, activeRequest) =>
            {
                var prepared = await PrepareOllamaChatRequestAsync(requestToken).ConfigureAwait(false);
                string endpoint = prepared.Endpoint;
                LlmApiFlavor flavor = prepared.Flavor;
                object? think = prepared.Think;
                bool thinkEnabled = prepared.ThinkEnabled;
                var promptDefinition = OllamaOcrProtocol.BuildVisionPrompt(OllamaModel, thinkEnabled);
                string prompt = promptDefinition.Prompt;

                if (flavor == LlmApiFlavor.OpenAiCompatible)
                {
                    return await SendOpenAiCompatibleVisionOcrRequestAsync(
                        endpoint,
                        prompt,
                        imageBytes,
                        thinkEnabled,
                        promptDefinition.UseSchemaResponse,
                        activeRequest,
                        requestToken).ConfigureAwait(false);
                }

                var message = BuildOllamaVisionMessage(prompt, imageBytes);

                var payload = new Dictionary<string, object?>
                {
                    ["model"] = OllamaModel,
                    ["stream"] = false,
                    ["messages"] = new[] { message }
                };

                payload["format"] = promptDefinition.UseSchemaResponse
                    ? OllamaOcrProtocol.BuildOcrJsonSchema()
                    : "json";
                payload["options"] = new Dictionary<string, object?>
                {
                    ["temperature"] = OllamaTemperature,
                    ["num_ctx"] = OllamaOcrContextLength
                };

                if (think != null)
                    payload["think"] = think;

                return await ExecuteNativeOllamaChatRequestAsync(
                    endpoint,
                    payload,
                    thinkEnabled,
                    OllamaThinkingRequestTimeout,
                    "[OcrService][Ollama]",
                    OllamaOcrProtocol.StripThinkingContent,
                    requestToken,
                    logResponseReceived: true).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        private async Task<OllamaModelCapabilities> GetOllamaModelCapabilitiesAsync(string model, CancellationToken cancellationToken)
        {
            lock (_cacheGate)
            {
                if (_ollamaCapabilities.TryGetValue(model, out var cached))
                    return cached;
            }

            string endpoint = LlmEndpointCompatibility.NormalizeEndpoint(OllamaEndpoint);
            LlmApiFlavor flavor = await LlmEndpointCompatibility.DetectApiFlavorAsync(s_httpClient, endpoint, cancellationToken).ConfigureAwait(false);

            if (flavor == LlmApiFlavor.OpenAiCompatible)
            {
                var openAiCapabilities = await LlmEndpointCompatibility.GetOpenAiCompatibleModelCapabilitiesAsync(
                    s_httpClient,
                    endpoint,
                    model,
                    cancellationToken).ConfigureAwait(false);

                var endpointCapabilities = new OllamaModelCapabilities
                {
                    Vision = openAiCapabilities.Vision,
                    Tools = true,
                    Thinking = openAiCapabilities.Thinking
                };

                lock (_cacheGate)
                {
                    _ollamaCapabilities[model] = endpointCapabilities;
                }

                return endpointCapabilities;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/api/show")
            {
                Content = new StringContent(LlmEndpointCompatibility.BuildModelRequestJson(model), Encoding.UTF8, "application/json")
            };

            using var requestLease = await OllamaRequestLoadCoordinator.AcquireAsync(
                OllamaRequestTimeout,
                thinkingEnabled: false,
                cancellationToken).ConfigureAwait(false);
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
            return !ThinkingLevelHelper.IsOff(ThinkingLevelHelper.NormalizeOllama(thinkingLevel));
        }

        private void ConfigureActiveOcrRequestSlotClear(ActiveOcrRequest activeRequest, string endpoint, OllamaRequestLoadCoordinator.RequestLease requestLease)
        {
            if (activeRequest == null || !requestLease.SlotEraseEnabled || requestLease.SlotId < 0)
                return;

            SetActiveOcrRequestSlotClearCallback(activeRequest, async () =>
            {
                if (LlmEndpointCompatibility.SupportsManagedSlots(endpoint, OllamaModel))
                    await LlmEndpointCompatibility.TryEraseSlotAsync(s_httpClient, endpoint, requestLease.SlotId, OllamaModel).ConfigureAwait(false);
            });
        }

        private async Task<string> SendOpenAiCompatibleCropOcrRequestAsync(
            string endpoint,
            string prompt,
            byte[] imageBytes,
            bool thinkEnabled,
            ActiveOcrRequest activeRequest,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = OllamaModel,
                ["stream"] = false,
                ["temperature"] = 0.0,
                ["messages"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = BuildOpenAiCompatibleImageContent(prompt, imageBytes, "image/png")
                    }
                }
            };

            Debug.WriteLine($"[OcrService][HybridCrop] OpenAI-compatible crop request. Model={OllamaModel}, Prompt={prompt}");
            return await ExecuteOpenAiCompatibleChatRequestAsync(
                endpoint,
                payload,
                thinkEnabled,
                HybridCropRequestTimeout,
                "[OcrService][HybridCrop]",
                OllamaOcrProtocol.NormalizeHybridCropText,
                activeRequest,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> SendOpenAiCompatibleVisionOcrRequestAsync(
            string endpoint,
            string prompt,
            byte[] imageBytes,
            bool thinkEnabled,
            bool useSchemaResponse,
            ActiveOcrRequest activeRequest,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = OllamaModel,
                ["stream"] = false,
                ["temperature"] = OllamaTemperature,
                ["messages"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = BuildOpenAiCompatibleImageContent(prompt, imageBytes, "image/jpeg")
                    }
                }
            };

            payload["response_format"] = useSchemaResponse
                ? new Dictionary<string, object?>
                {
                    ["type"] = "json_schema",
                    ["schema"] = OllamaOcrProtocol.BuildOcrJsonSchema()
                }
                : new Dictionary<string, object?>
                {
                    ["type"] = "json_object"
                };

            return await ExecuteOpenAiCompatibleChatRequestAsync(
                endpoint,
                payload,
                thinkEnabled,
                OllamaThinkingRequestTimeout,
                "[OcrService][llama-server]",
                OllamaOcrProtocol.StripThinkingContent,
                activeRequest,
                cancellationToken).ConfigureAwait(false);
        }

        private static OllamaOcrResponse ParseStructuredOllamaResponse(
            string responseText,
            string modelName,
            int sourceImageWidth,
            int sourceImageHeight,
            int originalImageWidth,
            int originalImageHeight)
            => OllamaOcrProtocol.ParseStructuredResponse(
                responseText,
                modelName,
                sourceImageWidth,
                sourceImageHeight,
                originalImageWidth,
                originalImageHeight);

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
