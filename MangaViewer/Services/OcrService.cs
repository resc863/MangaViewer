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
using MangaViewer.ViewModels;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using Windows.Foundation;
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
    /// OcrService - Provides OCR processing for images with configurable language, grouping, and paragraph detection.
    /// </summary>
    public partial class OcrService
    {
        private const double OllamaAssumedProcessingSquareSize = 1000.0;
        private const int OllamaOcrContextLength = 8192;
        private const int DocLayoutInputSize = 800;
        private const float DocLayoutScoreThreshold = 0.5f;
        private const string DocLayoutModelRelativePath = "onnx/PP-DocLayoutV3.onnx";
        private static readonly TimeSpan OllamaRequestTimeout = TimeSpan.FromSeconds(180);
        private static readonly TimeSpan OllamaThinkingRequestTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan HybridCropRequestTimeout = TimeSpan.FromSeconds(45);

        public sealed class OllamaOcrResponse
        {
            public string Text { get; init; } = string.Empty;
            public List<BoundingBoxViewModel> Boxes { get; init; } = new();
            public bool IsSuccessful { get; init; }
            public bool UsedFallback { get; init; }
            public string StatusMessage { get; init; } = string.Empty;
        }

        private sealed class OllamaModelCapabilities
        {
            public bool Vision { get; init; }
            public bool Tools { get; init; }
            public bool Thinking { get; init; }
        }

        public sealed class OnnxExecutionProviderInfo
        {
            public string Name { get; init; } = string.Empty;
            public string ReadyState { get; init; } = string.Empty;
        }

        private enum OllamaCoordinateSpace
        {
            SourcePixel,
            AssumedSmartResize,
            Normalized1000
        }

        public IReadOnlyList<OnnxExecutionProviderInfo> GetCompatibleOnnxExecutionProviders()
        {
            try
            {
                var catalog = ExecutionProviderCatalog.GetDefault();
                var providers = catalog.FindAllProviders();
                var result = providers
                    .Select(provider => new OnnxExecutionProviderInfo
                    {
                        Name = provider.Name,
                        ReadyState = provider.ReadyState.ToString()
                    })
                    .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                OnnxExecutionProviderStatus = result.Count == 0
                    ? "Compatible EP list is empty"
                    : $"Compatible EPs found: {result.Count}";

                return result;
            }
            catch (Exception ex)
            {
                OnnxExecutionProviderStatus = "Failed to enumerate compatible EPs: " + ex.Message;
                Log.Error(ex, "[OcrService] Compatible EP enumeration failed");
                return Array.Empty<OnnxExecutionProviderInfo>();
            }
        }

        private readonly record struct DocLayoutBox(float Score, Rect Rect, int ReadOrder);

        private readonly record struct RawOllamaBox(string Text, double X1, double Y1, double X2, double Y2);

        private static readonly Lazy<OcrService> _instance = new(() => new OcrService());
        public static OcrService Instance => _instance.Value;
        private static readonly HttpClient s_httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            return client;
        }

        public enum OcrGrouping { Word = 0, Line = 1, Paragraph = 2 }
        public enum WritingMode { Auto = 0, Horizontal = 1, Vertical = 2 }
        public enum OcrBackend { Hybrid = 0, Vlm = 1 }
        public enum OnnxEpRegistrationMode { Auto = 0, Manual = 1 }

        public event EventHandler? SettingsChanged;
        public string CurrentLanguage { get; private set; } = "auto";
        public OcrGrouping GroupingMode { get; private set; } = OcrGrouping.Word;
        public WritingMode TextWritingMode { get; private set; } = WritingMode.Auto;
        public double ParagraphGapFactorVertical { get; private set; } = 1.50;
        public double ParagraphGapFactorHorizontal { get; private set; } = 1.25;
        public OcrBackend Backend { get; private set; } = OcrBackend.Hybrid;
        public string OllamaEndpoint { get; private set; } = "http://localhost:11434";
        public string OllamaModel { get; private set; } = "glm-ocr:latest";
        public string OllamaThinkingLevel { get; private set; } = "Off";
        public bool OllamaStructuredOutputEnabled { get; private set; } = true;
        public double OllamaTemperature { get; private set; } = 1.0;
        public int LlamaServerMaxConcurrentRequests { get; private set; } = 1;
        public bool LlamaServerSlotEraseEnabled { get; private set; } = true;
        public int LlamaServerReportedTotalSlots { get; private set; }
        public int LlamaServerMaxConcurrentRequestsUpperBound => LlamaServerReportedTotalSlots > 0 ? Math.Clamp(LlamaServerReportedTotalSlots, 1, 32) : 32;
        public int EffectiveLlamaServerMaxConcurrentRequests => Math.Min(LlamaServerMaxConcurrentRequests, LlamaServerMaxConcurrentRequestsUpperBound);
        public int HybridTextExtractionParallelism { get; private set; } = 2;
        public bool HybridOnnxFallbackEnabled { get; private set; } = false;
        public bool PrefetchAdjacentPagesEnabled { get; private set; } = true;
        public int PrefetchAdjacentPageCount { get; private set; } = 1;
        public OnnxEpRegistrationMode OnnxExecutionProviderMode { get; private set; } = OnnxEpRegistrationMode.Auto;
        public string OnnxExecutionProviderManualList { get; private set; } = string.Empty;
        public string OnnxExecutionProviderStatus { get; private set; } = "Not initialized";
        public bool OnnxTrtRtxEnableCudaGraph { get; private set; } = true;
        public string OnnxTrtRtxRuntimeCachePath { get; private set; } = string.Empty;
        public bool OnnxUseEpContextModel { get; private set; } = true;
        public bool OnnxAutoCompileEpContextModel { get; private set; } = true;

        private readonly Dictionary<string, OcrEngine?> _engineCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<BoundingBoxViewModel>> _ocrCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, OllamaOcrResponse> _ollamaCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _hybridBoxTextCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<string>> _hybridBoxTextRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, OllamaModelCapabilities> _ollamaCapabilities = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _activeOcrRequestGate = new();
        private readonly Dictionary<Guid, ActiveOcrRequest> _activeOcrRequests = new();
        private readonly object _docLayoutSessionGate = new();
        private InferenceSession? _docLayoutSession;
        private string? _docLayoutModelPath;
        private bool _docLayoutRuntimeEpLogged;
        private readonly object _cacheGate = new();
        private readonly SemaphoreSlim _onnxEpRegisterGate = new(1, 1);
        private volatile bool _onnxEpInitialized;
        private CancellationTokenSource? _debounceCts;
        private readonly object _debounceGate = new();
        private readonly SynchronizationContext? _syncContext;
        private TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(250);

        private readonly IImageDecoder _decoder = new WinRtImageDecoder();
        private readonly OllamaVlmOcrBackend _ollamaVlmBackend;
        private readonly HybridOcrBackend _hybridOcrBackend;
        private readonly DocLayoutOnnxBackend _docLayoutOnnxBackend;

        private sealed class ActiveOcrRequest
        {
            public required Guid Id { get; init; }
            public required CancellationTokenSource CancellationSource { get; init; }
            public Func<Task>? SlotClearAsync { get; set; }
        }

        private OcrService()
        {
            _engineCache["auto"] = TryCreateEngineForLanguage("auto");
            _syncContext = SynchronizationContext.Current;
            _ollamaVlmBackend = new OllamaVlmOcrBackend(this);
            _hybridOcrBackend = new HybridOcrBackend(this);
            _docLayoutOnnxBackend = new DocLayoutOnnxBackend(this);

            Backend = (OcrBackend)SettingsProvider.Get("OcrBackend", 0);
            OllamaEndpoint = SettingsProvider.Get("OllamaEndpoint", "http://localhost:11434");
            OllamaModel = SettingsProvider.Get("OllamaModel", "glm-ocr:latest");
            OllamaThinkingLevel = ThinkingLevelHelper.NormalizeOllama(SettingsProvider.Get("OcrOllamaThinkingLevel", "Off"));
            OllamaStructuredOutputEnabled = SettingsProvider.Get("OcrOllamaStructuredOutputEnabled", true);
            OllamaTemperature = Math.Clamp(SettingsProvider.Get("OcrOllamaTemperature", 1.0), 0.0, 2.0);
            LlamaServerMaxConcurrentRequests = Math.Clamp(SettingsProvider.Get("LlamaServerMaxConcurrentRequests", OllamaRequestLoadCoordinator.MaxConcurrentRequests), 1, 32);
            LlamaServerSlotEraseEnabled = SettingsProvider.Get("LlamaServerSlotEraseEnabled", OllamaRequestLoadCoordinator.SlotEraseEnabled);
            HybridTextExtractionParallelism = Math.Clamp(SettingsProvider.Get("OcrHybridTextExtractionParallelism", 2), 1, 8);
            HybridOnnxFallbackEnabled = SettingsProvider.Get("OcrHybridOnnxFallbackEnabled", false);
            PrefetchAdjacentPagesEnabled = SettingsProvider.Get("OcrPrefetchAdjacentPagesEnabled", true);
            PrefetchAdjacentPageCount = Math.Clamp(SettingsProvider.Get("OcrPrefetchAdjacentPageCount", 1), 0, 10);
            OnnxExecutionProviderMode = (OnnxEpRegistrationMode)SettingsProvider.Get("OcrOnnxEpMode", (int)OnnxEpRegistrationMode.Auto);
            OnnxExecutionProviderManualList = SettingsProvider.Get("OcrOnnxEpManualList", string.Empty);
            OnnxTrtRtxEnableCudaGraph = SettingsProvider.Get("OcrOnnxTrtRtxEnableCudaGraph", true);
            OnnxTrtRtxRuntimeCachePath = SettingsProvider.Get("OcrOnnxTrtRtxRuntimeCachePath",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaViewer", "onnx", "trt_rtx_cache"));
            OnnxUseEpContextModel = SettingsProvider.Get("OcrOnnxUseEpContextModel", true);
            OnnxAutoCompileEpContextModel = SettingsProvider.Get("OcrOnnxAutoCompileEpContextModel", true);

            OllamaRequestLoadCoordinator.SetMaxConcurrentRequests(EffectiveLlamaServerMaxConcurrentRequests);
            OllamaRequestLoadCoordinator.SetSlotEraseEnabled(LlamaServerSlotEraseEnabled);
        }

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

        public void SetBackend(OcrBackend backend)
        {
            if (Backend != backend)
            {
                Backend = backend;
                SettingsProvider.Set("OcrBackend", (int)backend);
                OnSettingsChanged();
            }
        }

        public void SetOllamaEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return;
            endpoint = LlmEndpointCompatibility.NormalizeEndpoint(endpoint);
            if (!string.Equals(OllamaEndpoint, endpoint, StringComparison.OrdinalIgnoreCase))
            {
                OllamaEndpoint = endpoint;
                SettingsProvider.Set("OllamaEndpoint", endpoint);
                OnSettingsChanged();
            }
        }

        public void SetOllamaModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return;
            if (!string.Equals(OllamaModel, model, StringComparison.OrdinalIgnoreCase))
            {
                OllamaModel = model;
                SettingsProvider.Set("OllamaModel", model);
                OnSettingsChanged();
            }
        }

        public void SetOllamaThinkingLevel(string level)
        {
            level = ThinkingLevelHelper.NormalizeOllama(level);
            if (!string.Equals(OllamaThinkingLevel, level, StringComparison.OrdinalIgnoreCase))
            {
                OllamaThinkingLevel = level;
                SettingsProvider.Set("OcrOllamaThinkingLevel", level);
                OnSettingsChanged();
            }
        }
        public void SetOllamaStructuredOutputEnabled(bool enabled)
        {
            if (OllamaStructuredOutputEnabled == enabled) return;
            OllamaStructuredOutputEnabled = enabled;
            SettingsProvider.Set("OcrOllamaStructuredOutputEnabled", enabled);
            OnSettingsChanged();
        }

        public void SetOllamaTemperature(double value)
        {
            value = Math.Clamp(value, 0.0, 2.0);
            if (Math.Abs(OllamaTemperature - value) <= 0.0001) return;
            OllamaTemperature = value;
            SettingsProvider.Set("OcrOllamaTemperature", value);
            OnSettingsChanged();
        }

        public void SetLlamaServerMaxConcurrentRequests(int value)
        {
            int clamped = Math.Clamp(value, 1, 32);
            if (LlamaServerMaxConcurrentRequests == clamped) return;
            LlamaServerMaxConcurrentRequests = clamped;
            SettingsProvider.Set("LlamaServerMaxConcurrentRequests", clamped);
            OllamaRequestLoadCoordinator.SetMaxConcurrentRequests(EffectiveLlamaServerMaxConcurrentRequests);
        }

        public void SetLlamaServerSlotEraseEnabled(bool enabled)
        {
            if (LlamaServerSlotEraseEnabled == enabled) return;
            LlamaServerSlotEraseEnabled = enabled;
            SettingsProvider.Set("LlamaServerSlotEraseEnabled", enabled);
            OllamaRequestLoadCoordinator.SetSlotEraseEnabled(enabled);
        }

        public async Task RefreshLlamaServerSlotLimitAsync(CancellationToken cancellationToken = default)
        {
            string endpoint = OllamaEndpoint;
            string model = OllamaModel;

            int reportedTotalSlots = 0;
            try
            {
                int? totalSlots = await LlmEndpointCompatibility.GetOpenAiCompatibleTotalSlotsAsync(
                    s_httpClient,
                    endpoint,
                    model,
                    cancellationToken).ConfigureAwait(false);

                reportedTotalSlots = totalSlots.GetValueOrDefault();
            }
            catch
            {
                reportedTotalSlots = 0;
            }

            if (!string.Equals(endpoint, OllamaEndpoint, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(model, OllamaModel, StringComparison.OrdinalIgnoreCase))
                return;

            LlamaServerReportedTotalSlots = reportedTotalSlots > 0 ? Math.Clamp(reportedTotalSlots, 1, 32) : 0;
            OllamaRequestLoadCoordinator.SetMaxConcurrentRequests(EffectiveLlamaServerMaxConcurrentRequests);
        }

        public void SetHybridTextExtractionParallelism(int value)
        {
            int clamped = Math.Clamp(value, 1, 8);
            if (HybridTextExtractionParallelism == clamped) return;
            HybridTextExtractionParallelism = clamped;
            SettingsProvider.Set("OcrHybridTextExtractionParallelism", clamped);
        }

        public void SetHybridOnnxFallbackEnabled(bool enabled)
        {
            if (HybridOnnxFallbackEnabled == enabled) return;
            HybridOnnxFallbackEnabled = enabled;
            SettingsProvider.Set("OcrHybridOnnxFallbackEnabled", enabled);
            OnSettingsChanged();
        }

        public void SetPrefetchAdjacentPagesEnabled(bool enabled)
        {
            if (PrefetchAdjacentPagesEnabled == enabled) return;
            PrefetchAdjacentPagesEnabled = enabled;
            SettingsProvider.Set("OcrPrefetchAdjacentPagesEnabled", enabled);
        }

        public void SetPrefetchAdjacentPageCount(int count)
        {
            int clamped = Math.Clamp(count, 0, 10);
            if (PrefetchAdjacentPageCount == clamped) return;
            PrefetchAdjacentPageCount = clamped;
            SettingsProvider.Set("OcrPrefetchAdjacentPageCount", clamped);
        }

        public void SetOnnxExecutionProviderMode(OnnxEpRegistrationMode mode)
        {
            if (OnnxExecutionProviderMode == mode) return;
            OnnxExecutionProviderMode = mode;
            SettingsProvider.Set("OcrOnnxEpMode", (int)mode);
            OnnxExecutionProviderStatus = mode == OnnxEpRegistrationMode.Auto
                ? "Auto mode enabled"
                : "Manual mode enabled";
            _onnxEpInitialized = false;
            InvalidateDocLayoutSession();
        }

        public void SetOnnxExecutionProviderManualList(string? providers)
        {
            string normalized = providers?.Trim() ?? string.Empty;
            if (string.Equals(OnnxExecutionProviderManualList, normalized, StringComparison.Ordinal)) return;
            OnnxExecutionProviderManualList = normalized;
            SettingsProvider.Set("OcrOnnxEpManualList", normalized);
        }

        public void SetOnnxTrtRtxEnableCudaGraph(bool enabled)
        {
            if (OnnxTrtRtxEnableCudaGraph == enabled) return;
            OnnxTrtRtxEnableCudaGraph = enabled;
            SettingsProvider.Set("OcrOnnxTrtRtxEnableCudaGraph", enabled);
            InvalidateDocLayoutSession();
        }

        public void SetOnnxTrtRtxRuntimeCachePath(string? path)
        {
            string normalized = path?.Trim() ?? string.Empty;
            if (string.Equals(OnnxTrtRtxRuntimeCachePath, normalized, StringComparison.Ordinal)) return;
            OnnxTrtRtxRuntimeCachePath = normalized;
            SettingsProvider.Set("OcrOnnxTrtRtxRuntimeCachePath", normalized);
            InvalidateDocLayoutSession();
        }

        public void SetOnnxUseEpContextModel(bool enabled)
        {
            if (OnnxUseEpContextModel == enabled) return;
            OnnxUseEpContextModel = enabled;
            SettingsProvider.Set("OcrOnnxUseEpContextModel", enabled);
            InvalidateDocLayoutSession();
        }

        public void SetOnnxAutoCompileEpContextModel(bool enabled)
        {
            if (OnnxAutoCompileEpContextModel == enabled) return;
            OnnxAutoCompileEpContextModel = enabled;
            SettingsProvider.Set("OcrOnnxAutoCompileEpContextModel", enabled);
        }

        public void InitializeManualOnnxExecutionProvidersOnStartup()
        {
            if (OnnxExecutionProviderMode != OnnxEpRegistrationMode.Manual)
                return;
            if (string.IsNullOrWhiteSpace(OnnxExecutionProviderManualList))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsureOnnxExecutionProvidersReadyAsync(CancellationToken.None, force: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[OcrService] Manual EP startup initialization failed");
                }
            });
        }

        public async Task<bool> EnsureOnnxExecutionProvidersReadyAsync(CancellationToken cancellationToken, bool force)
        {
            if (!force)
            {
                if (_onnxEpInitialized)
                    return true;

                if (OnnxExecutionProviderMode == OnnxEpRegistrationMode.Manual
                    && string.IsNullOrWhiteSpace(OnnxExecutionProviderManualList))
                {
                    OnnxExecutionProviderStatus = "Manual mode enabled but EP list is empty";
                    return false;
                }
            }

            await _onnxEpRegisterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!force)
                {
                    if (_onnxEpInitialized)
                        return true;

                    if (OnnxExecutionProviderMode == OnnxEpRegistrationMode.Manual
                        && string.IsNullOrWhiteSpace(OnnxExecutionProviderManualList))
                    {
                        OnnxExecutionProviderStatus = "Manual mode enabled but EP list is empty";
                        return false;
                    }
                }

                var catalog = ExecutionProviderCatalog.GetDefault();

                if (OnnxExecutionProviderMode == OnnxEpRegistrationMode.Manual && !force)
                {
                    await EnsureManualExecutionProvidersReadyAndRegisteredAsync(catalog, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await catalog.EnsureAndRegisterCertifiedAsync().AsTask(cancellationToken).ConfigureAwait(false);
                }

                _onnxEpInitialized = true;

                string modePrefix = OnnxExecutionProviderMode == OnnxEpRegistrationMode.Manual
                    ? "Manual trigger"
                    : "Auto";
                string manualInfo = string.IsNullOrWhiteSpace(OnnxExecutionProviderManualList)
                    ? string.Empty
                    : $", manual list={OnnxExecutionProviderManualList}";
                bool hasManualFallbackStatus = OnnxExecutionProviderMode == OnnxEpRegistrationMode.Manual
                    && OnnxExecutionProviderStatus.Contains("fallback", StringComparison.OrdinalIgnoreCase);
                if (!hasManualFallbackStatus)
                    OnnxExecutionProviderStatus = $"{modePrefix}: EP registration succeeded{manualInfo}";
                Debug.WriteLine($"[OcrService][ONNX] {OnnxExecutionProviderStatus}");
                return true;
            }
            catch (OperationCanceledException)
            {
                OnnxExecutionProviderStatus = "EP registration canceled";
                throw;
            }
            catch (Exception ex)
            {
                OnnxExecutionProviderStatus = "EP registration failed: " + ex.Message;
                Log.Error(ex, "[OcrService] EP registration failed");
                return false;
            }
            finally
            {
                _onnxEpRegisterGate.Release();
            }
        }

        private async Task EnsureManualExecutionProvidersReadyAndRegisteredAsync(ExecutionProviderCatalog catalog, CancellationToken cancellationToken)
        {
            var manualEntries = ParseManualExecutionProviderEntries(OnnxExecutionProviderManualList);
            var providers = catalog.FindAllProviders();

            var matchedProviders = providers
                .Where(provider => manualEntries.Any(entry => IsMatchingExecutionProvider(entry, provider.Name)))
                .ToList();

            if (matchedProviders.Count == 0)
            {
                await catalog.EnsureAndRegisterCertifiedAsync().AsTask(cancellationToken).ConfigureAwait(false);
                OnnxExecutionProviderStatus =
                    $"Manual mode fallback: no compatible EP matched manual list ({OnnxExecutionProviderManualList}), using certified/default EP registration";
                Debug.WriteLine($"[OcrService][ONNX] {OnnxExecutionProviderStatus}");
                return;
            }

            foreach (var provider in matchedProviders)
            {
                if (!provider.ReadyState.ToString().Equals("Ready", StringComparison.OrdinalIgnoreCase))
                    await provider.EnsureReadyAsync().AsTask(cancellationToken).ConfigureAwait(false);
            }

            await catalog.RegisterCertifiedAsync().AsTask(cancellationToken).ConfigureAwait(false);

            var unresolvedEntries = manualEntries
                .Where(entry => !matchedProviders.Any(provider => IsMatchingExecutionProvider(entry, provider.Name)))
                .ToList();

            if (unresolvedEntries.Count > 0)
            {
                OnnxExecutionProviderStatus =
                    $"Manual mode: ready={matchedProviders.Count}, unresolved={string.Join(", ", unresolvedEntries)}";
            }
            else
            {
                OnnxExecutionProviderStatus = $"Manual mode: ready={matchedProviders.Count}";
            }
        }

        private static List<string> ParseManualExecutionProviderEntries(string manualList)
        {
            return manualList
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsMatchingExecutionProvider(string manualEntry, string providerName)
        {
            if (string.Equals(manualEntry, providerName, StringComparison.OrdinalIgnoreCase))
                return true;

            string normalizedEntry = NormalizeExecutionProviderName(manualEntry);
            string normalizedProvider = NormalizeExecutionProviderName(providerName);

            return string.Equals(normalizedEntry, normalizedProvider, StringComparison.Ordinal)
                || string.Equals(normalizedEntry + "ep", normalizedProvider, StringComparison.Ordinal)
                || string.Equals(normalizedEntry, normalizedProvider + "ep", StringComparison.Ordinal);
        }

        private static string NormalizeExecutionProviderName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var chars = value
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray();

            string normalized = new string(chars);
            normalized = normalized.Replace("executionprovider", string.Empty, StringComparison.Ordinal);
            normalized = normalized.Replace("provider", string.Empty, StringComparison.Ordinal);
            return normalized;
        }

        public void ClearCache()
        {
            lock (_cacheGate)
            {
                _ocrCache.Clear();
                _ollamaCache.Clear();
                _hybridBoxTextCache.Clear();
                _hybridBoxTextRequests.Clear();
                _ollamaCapabilities.Clear();
            }
        }

        public void CancelActiveOcrRequests()
        {
            List<ActiveOcrRequest> activeRequests;
            lock (_activeOcrRequestGate)
                activeRequests = _activeOcrRequests.Values.ToList();

            foreach (var request in activeRequests)
            {
                try
                {
                    request.CancellationSource.Cancel();
                }
                catch
                {
                }

                if (request.SlotClearAsync == null)
                    continue;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await request.SlotClearAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "CancelActiveOcrRequests slot clear failed");
                    }
                });
            }
        }

        private ActiveOcrRequest RegisterActiveOcrRequest(CancellationTokenSource cancellationSource)
        {
            var request = new ActiveOcrRequest
            {
                Id = Guid.NewGuid(),
                CancellationSource = cancellationSource
            };

            lock (_activeOcrRequestGate)
                _activeOcrRequests[request.Id] = request;

            return request;
        }

        private void SetActiveOcrRequestSlotClearCallback(ActiveOcrRequest request, Func<Task>? slotClearAsync)
        {
            if (request == null)
                return;

            lock (_activeOcrRequestGate)
            {
                if (_activeOcrRequests.ContainsKey(request.Id))
                    request.SlotClearAsync = slotClearAsync;
            }
        }

        private void UnregisterActiveOcrRequest(ActiveOcrRequest? request)
        {
            if (request == null)
                return;

            lock (_activeOcrRequestGate)
                _activeOcrRequests.Remove(request.Id);
        }

        private void OnSettingsChanged()
        {
            ClearCache();
            
            lock (_debounceGate)
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = new CancellationTokenSource();
                
                var localCts = _debounceCts;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(_debounceDelay, localCts.Token).ConfigureAwait(false);
                        
                        if (_syncContext != null)
                            _syncContext.Post(_ => 
                            { 
                                try { SettingsChanged?.Invoke(this, EventArgs.Empty); } 
                                catch { } 
                            }, null);
                        else
                        {
                            try { SettingsChanged?.Invoke(this, EventArgs.Empty); }
                            catch { }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Log.Error(ex, "SettingsChanged debounced dispatch failed"); }
                });
            }
        }

        public void SetSettingsChangedDebounce(TimeSpan delay) => _debounceDelay = delay;

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
        /// Legacy synchronous recognition for direct StorageFile usage.
        /// </summary>
        public async Task<List<OcrResult>> RecognizeAsync(StorageFile imageFile)
        {
            var engine = GetActiveEngine();
            if (engine == null) return new List<OcrResult>();

            try
            {
                using IRandomAccessStream stream = await imageFile.OpenAsync(FileAccessMode.Read);
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

                // Use WinRtImageDecoder's decode logic through path-based API
                var toOcr = await _decoder.DecodeForOcrAsync(imageFile.Path, CancellationToken.None);
                if (toOcr == null) return new List<OcrResult>();

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
            lock (_cacheGate)
            {
                if (_ocrCache.TryGetValue(cacheKey, out var cached))
                    return new List<BoundingBoxViewModel>(cached);
            }

            var engine = GetActiveEngine();
            IOcrEngineFacade facade = new WinRtOcrEngineFacade(engine);
            if (engine == null) return new List<BoundingBoxViewModel>();

            try
            {
                var toOcr = await _decoder.DecodeForOcrAsync(imagePath, cancellationToken).ConfigureAwait(false);
                if (toOcr == null) return new List<BoundingBoxViewModel>();
                int imgW = toOcr.PixelWidth;
                int imgH = toOcr.PixelHeight;
                var ocr = await facade.RecognizeAsync(toOcr, cancellationToken).ConfigureAwait(false);
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

                lock (_cacheGate)
                {
                    _ocrCache[cacheKey] = new List<BoundingBoxViewModel>(resultBoxes);
                }
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

        private static Rect ScaleRect(Rect rect, int imgW, int imgH)
        {
            if (IsEmpty(rect) || imgW <= 0 || imgH <= 0)
                return new Rect();

            return new Rect(
                rect.X / imgW,
                rect.Y / imgH,
                rect.Width / imgW,
                rect.Height / imgH);
        }

        private static bool OverlapsAfterScaling(Rect a, Rect b, int imgW, int imgH)
        {
            var sa = ScaleRect(a, imgW, imgH);
            var sb = ScaleRect(b, imgW, imgH);
            if (IsEmpty(sa) || IsEmpty(sb))
                return false;

            return sa.X < sb.X + sb.Width
                && sa.X + sa.Width > sb.X
                && sa.Y < sb.Y + sb.Height
                && sa.Y + sa.Height > sb.Y;
        }

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
                var current = new List<int> { indices[0] };
                for (int k = 1; k < indices.Count; k++)
                {
                    var prev = lineRects[indices[k - 1]];
                    var cur = lineRects[indices[k]];
                    if (OverlapsAfterScaling(prev, cur, imgW, imgH))
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
                var current = new List<int> { indices[0] };
                for (int k = 1; k < indices.Count; k++)
                {
                    var prev = lineRects[indices[k - 1]];
                    var cur = lineRects[indices[k]];
                    if (OverlapsAfterScaling(prev, cur, imgW, imgH))
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
    }
}
