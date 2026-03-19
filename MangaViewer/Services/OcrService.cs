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
    public class OcrService
    {
        private const double OllamaAssumedProcessingSquareSize = 1000.0;
        private const int OllamaOcrContextLength = 8192;
        private static readonly TimeSpan OllamaRequestTimeout = TimeSpan.FromSeconds(180);

        public sealed class OllamaOcrResponse
        {
            public string Text { get; init; } = string.Empty;
            public List<BoundingBoxViewModel> Boxes { get; init; } = new();
        }

        private sealed class OllamaModelCapabilities
        {
            public bool Vision { get; init; }
            public bool Tools { get; init; }
            public bool Thinking { get; init; }
        }

        private enum OllamaCoordinateSpace
        {
            SourcePixel,
            AssumedSquare1000
        }

        private readonly record struct RawOllamaBox(string Text, double X1, double Y1, double X2, double Y2);

        private static readonly Lazy<OcrService> _instance = new(() => new OcrService());
        public static OcrService Instance => _instance.Value;
        private static readonly HttpClient s_httpClient = new();

        public enum OcrGrouping { Word = 0, Line = 1, Paragraph = 2 }
        public enum WritingMode { Auto = 0, Horizontal = 1, Vertical = 2 }
        public enum OcrBackend { WindowsBuiltIn = 0, Ollama = 1 }

        public event EventHandler? SettingsChanged;
        public string CurrentLanguage { get; private set; } = "auto";
        public OcrGrouping GroupingMode { get; private set; } = OcrGrouping.Word;
        public WritingMode TextWritingMode { get; private set; } = WritingMode.Auto;
        public double ParagraphGapFactorVertical { get; private set; } = 1.50;
        public double ParagraphGapFactorHorizontal { get; private set; } = 1.25;
        public OcrBackend Backend { get; private set; } = OcrBackend.WindowsBuiltIn;
        public string OllamaEndpoint { get; private set; } = "http://localhost:11434";
        public string OllamaModel { get; private set; } = "glm-ocr:latest";
        public string OllamaThinkingLevel { get; private set; } = "Off";
        public bool OllamaStructuredOutputEnabled { get; private set; } = true;
        public double OllamaTemperature { get; private set; } = 1.0;
        public bool PrefetchAdjacentPagesEnabled { get; private set; } = true;
        public int PrefetchAdjacentPageCount { get; private set; } = 1;

        private readonly Dictionary<string, OcrEngine?> _engineCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<BoundingBoxViewModel>> _ocrCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, OllamaOcrResponse> _ollamaCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, OllamaModelCapabilities> _ollamaCapabilities = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheGate = new();
        private CancellationTokenSource? _debounceCts;
        private readonly object _debounceGate = new();
        private readonly SynchronizationContext? _syncContext;
        private TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(250);

        private readonly IImageDecoder _decoder = new WinRtImageDecoder();

        private OcrService()
        {
            _engineCache["auto"] = TryCreateEngineForLanguage("auto");
            _syncContext = SynchronizationContext.Current;

            Backend = (OcrBackend)SettingsProvider.Get("OcrBackend", 0);
            OllamaEndpoint = SettingsProvider.Get("OllamaEndpoint", "http://localhost:11434");
            OllamaModel = SettingsProvider.Get("OllamaModel", "glm-ocr:latest");
            OllamaThinkingLevel = NormalizeOllamaThinkingLevel(SettingsProvider.Get("OcrOllamaThinkingLevel", "Off"));
            OllamaStructuredOutputEnabled = SettingsProvider.Get("OcrOllamaStructuredOutputEnabled", true);
            OllamaTemperature = Math.Clamp(SettingsProvider.Get("OcrOllamaTemperature", 1.0), 0.0, 2.0);
            PrefetchAdjacentPagesEnabled = SettingsProvider.Get("OcrPrefetchAdjacentPagesEnabled", true);
            PrefetchAdjacentPageCount = Math.Clamp(SettingsProvider.Get("OcrPrefetchAdjacentPageCount", 1), 0, 10);
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
            endpoint = endpoint.TrimEnd('/');
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
            level = NormalizeOllamaThinkingLevel(level);
            if (!string.Equals(OllamaThinkingLevel, level, StringComparison.OrdinalIgnoreCase))
            {
                OllamaThinkingLevel = level;
                SettingsProvider.Set("OcrOllamaThinkingLevel", level);
                OnSettingsChanged();
            }
        }

        private static string NormalizeOllamaThinkingLevel(string? level)
        {
            if (string.IsNullOrWhiteSpace(level)) return "Off";
            if (level.Equals("Off", StringComparison.OrdinalIgnoreCase)
                || level.Equals("False", StringComparison.OrdinalIgnoreCase)
                || level.Equals("0", StringComparison.OrdinalIgnoreCase))
                return "Off";
            return "On";
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

        public async Task<string> GetOllamaTextAsync(string imagePath, CancellationToken cancellationToken)
        {
            var result = await GetOllamaOcrAsync(imagePath, cancellationToken).ConfigureAwait(false);
            return result.Text;
        }

        public async Task<OllamaOcrResponse> GetOllamaOcrAsync(string imagePath, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return new OllamaOcrResponse();
            string cacheKey = BuildOllamaCacheKey(imagePath);
            if (!forceRefresh)
            {
                lock (_cacheGate)
                {
                    if (_ollamaCache.TryGetValue(cacheKey, out var cached))
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
                    sourceImageWidth,
                    sourceImageHeight,
                    originalImageWidth,
                    originalImageHeight);

                lock (_cacheGate)
                {
                    _ollamaCache[cacheKey] = result;
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

        private string BuildOllamaCacheKey(string path)
            => $"{path}|endpoint={OllamaEndpoint}|model={OllamaModel}|thinking={OllamaThinkingLevel}|structured={OllamaStructuredOutputEnabled}|temp={OllamaTemperature:F3}";

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
            string groupingInstruction = BuildOllamaGroupingInstruction();

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
            string structuredInstruction = OllamaStructuredOutputEnabled
                ? @"Use this schema exactly:
{
  ""boxes"": [
    {
      ""bbox_2d"": [23, 82, 122, 143],
      ""text_content"": ""text snippet""
    }
  ]
}
Coordinates should be numbers and represent the detected box in reading order."
                : "Return valid JSON only. Include OCR result text and bounding boxes when available.";

            string prompt = $@"You are an OCR engine. Read all visible text from the image and return ONLY JSON.
{structuredInstruction}
{groupingInstruction}
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

            payload["format"] = "json";
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

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(OllamaRequestTimeout);

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

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(OllamaRequestTimeout);

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
                    ["boxes"] = new Dictionary<string, object?>
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
                                ["text_content"] = new Dictionary<string, object?> { ["type"] = "string" },
                            },
                            ["required"] = new[] { "bbox_2d", "text_content" }
                        }
                    }
                },
                ["required"] = new[] { "boxes" }
            };
        }

        private string BuildOllamaGroupingInstruction()
        {
            return GroupingMode switch
            {
                OcrGrouping.Word => @"Grouping mode is WORD.
Each output box MUST contain exactly one visible word.
If spacing is tight, still separate adjacent words into separate boxes.",
                OcrGrouping.Line => @"Grouping mode is LINE.
Each output box MUST contain exactly one full text line.
Do NOT merge different lines into one box.",
                OcrGrouping.Paragraph => @"Grouping mode is Paragraph.
Each output box MUST contain exactly one coherent text block (for manga, one speech bubble or narration block).
Merge all lines belonging to the same block into one box.",
                _ => "Group boxes naturally in reading order without changing granularity."
            };
        }

        private static OllamaOcrResponse ParseStructuredOllamaResponse(
            string responseText,
            int sourceImageWidth,
            int sourceImageHeight,
            int originalImageWidth,
            int originalImageHeight)
        {
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
                else
                {
                    return new OllamaOcrResponse { Text = responseText.Trim() };
                }

                var rawBoxes = new List<RawOllamaBox>();
                foreach (var item in boxesElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    string boxText = item.TryGetProperty("text_content", out var bt) && bt.ValueKind == JsonValueKind.String
                        ? (bt.GetString() ?? string.Empty)
                        : (item.TryGetProperty("text", out var legacyBt) && legacyBt.ValueKind == JsonValueKind.String
                            ? (legacyBt.GetString() ?? string.Empty)
                            : string.Empty);

                    if (!TryReadBbox2D(item, out var x1, out var y1, out var x2, out var y2))
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

        private static bool TryReadBbox2D(JsonElement item, out double x1, out double y1, out double x2, out double y2)
        {
            x1 = y1 = x2 = y2 = 0;
            if (item.ValueKind != JsonValueKind.Object) return false;
            if (!item.TryGetProperty("bbox_2d", out var bboxElement) || bboxElement.ValueKind != JsonValueKind.Array)
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
            x1 = vals[0];
            y1 = vals[1];
            x2 = vals[2];
            y2 = vals[3];
            return true;
        }

        private static OllamaCoordinateSpace SelectOllamaCoordinateSpace(
            IReadOnlyList<RawOllamaBox> boxes,
            int sourceImageWidth,
            int sourceImageHeight,
            int originalImageWidth,
            int originalImageHeight)
        {
            if (boxes.Count == 0 || sourceImageWidth <= 0 || sourceImageHeight <= 0 || originalImageWidth <= 0 || originalImageHeight <= 0)
                return OllamaCoordinateSpace.SourcePixel;

            bool exceedsSquareRange = originalImageWidth > OllamaAssumedProcessingSquareSize
                || originalImageHeight > OllamaAssumedProcessingSquareSize;

            var selected = exceedsSquareRange
                ? OllamaCoordinateSpace.AssumedSquare1000
                : OllamaCoordinateSpace.SourcePixel;

            Debug.WriteLine($"[OcrService][Ollama] Coordinate space selected: {selected}, exceeds1000={exceedsSquareRange}, boxCount={boxes.Count}");
            return selected;
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
            if (sourceImageWidth <= 0 || sourceImageHeight <= 0 || originalImageWidth <= 0 || originalImageHeight <= 0) return new Rect();

            double minX = Math.Min(x1, x2);
            double minY = Math.Min(y1, y2);
            double maxX = Math.Max(x1, x2);
            double maxY = Math.Max(y1, y2);

            double sourceW;
            double sourceH;

            if (coordinateSpace == OllamaCoordinateSpace.AssumedSquare1000)
            {
                sourceW = OllamaAssumedProcessingSquareSize;
                sourceH = OllamaAssumedProcessingSquareSize;
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

        public void ClearCache()
        {
            lock (_cacheGate)
            {
                _ocrCache.Clear();
                _ollamaCache.Clear();
                _ollamaCapabilities.Clear();
            }
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
