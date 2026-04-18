using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using MangaViewer.ViewModels;
using Windows.Foundation;

namespace MangaViewer.Services
{
    internal static class OllamaOcrProtocol
    {
        private const double AssumedProcessingSquareSize = 1000.0;

        internal readonly record struct VisionPromptDefinition(string Prompt, bool UseSchemaResponse);

        private readonly record struct RawOllamaBox(string Text, double X1, double Y1, double X2, double Y2);

        private enum CoordinateSpace
        {
            SourcePixel,
            AssumedSmartResize,
            Normalized1000
        }

        private enum VisionModelFamily
        {
            Qwen,
            Gemma,
            Other
        }

        public static string BuildHybridCropPrompt(string? modelName)
        {
            if (IsGlmFamilyModel(modelName))
                return "Text Recognition:";

            return @"Recognize all readable text in this cropped manga region.
Consider typical Japanese manga speech-bubble text writing/reading order.
Return only the recognized text as plain text.
Do not return JSON, markdown, labels, or explanations.
If no readable text exists, return an empty string.";
        }

        public static VisionPromptDefinition BuildVisionPrompt(string? modelName, bool thinkingEnabled)
        {
            VisionModelFamily modelFamily = GetVisionModelFamily(modelName);
            bool isQwenFamilyModel = modelFamily == VisionModelFamily.Qwen;
            bool isGemmaFamilyModel = modelFamily == VisionModelFamily.Gemma;

            string thinkInstruction = thinkingEnabled
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

            return new VisionPromptDefinition(prompt, isQwenFamilyModel || isGemmaFamilyModel);
        }

        public static object BuildOcrJsonSchema()
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

        public static string StripThinkingContent(string? content)
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

        public static string NormalizeHybridCropText(string? content)
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

        public static OcrService.OllamaOcrResponse ParseStructuredResponse(
            string responseText,
            string modelName,
            int sourceImageWidth,
            int sourceImageHeight,
            int originalImageWidth,
            int originalImageHeight)
        {
            string jsonText = TrimMarkdownCodeFence(responseText);
            if (string.IsNullOrWhiteSpace(jsonText))
                return new OcrService.OllamaOcrResponse();

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
                    return new OcrService.OllamaOcrResponse { Text = responseText.Trim(), IsSuccessful = true };
                }

                var rawBoxes = new List<RawOllamaBox>();
                bool useNormalizedYxOrder = true;
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

                CoordinateSpace coordinateSpace = SelectCoordinateSpace(
                    rawBoxes,
                    modelName,
                    sourceImageWidth,
                    sourceImageHeight,
                    originalImageWidth,
                    originalImageHeight);

                var boxes = new List<BoundingBoxViewModel>(rawBoxes.Count);
                foreach (var raw in rawBoxes)
                {
                    var scaledRect = ScaleFromModelSpace(
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

                return new OcrService.OllamaOcrResponse
                {
                    Text = text,
                    Boxes = boxes,
                    IsSuccessful = true
                };
            }
            catch
            {
                return new OcrService.OllamaOcrResponse { Text = responseText.Trim(), IsSuccessful = true };
            }
        }

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

        private static CoordinateSpace SelectCoordinateSpace(
            IReadOnlyList<RawOllamaBox> boxes,
            string modelName,
            int sourceImageWidth,
            int sourceImageHeight,
            int originalImageWidth,
            int originalImageHeight)
        {
            if (boxes.Count == 0 || sourceImageWidth <= 0 || sourceImageHeight <= 0 || originalImageWidth <= 0 || originalImageHeight <= 0)
                return CoordinateSpace.SourcePixel;

            VisionModelFamily modelFamily = GetVisionModelFamily(modelName);
            if (modelFamily != VisionModelFamily.Qwen)
            {
                Debug.WriteLine($"[OcrService][Ollama] Coordinate space selected: {CoordinateSpace.Normalized1000}, model={modelName}, family={modelFamily}, boxCount={boxes.Count}");
                return CoordinateSpace.Normalized1000;
            }

            var selected = TryComputeSmartResizeDimensions(originalImageWidth, originalImageHeight, out _, out _)
                ? CoordinateSpace.AssumedSmartResize
                : CoordinateSpace.SourcePixel;

            Debug.WriteLine($"[OcrService][Ollama] Coordinate space selected: {selected}, original={originalImageWidth}x{originalImageHeight}, boxCount={boxes.Count}");
            return selected;
        }

        private static bool TryComputeSmartResizeDimensions(int originalWidth, int originalHeight, out int resizedWidth, out int resizedHeight)
        {
            resizedWidth = 0;
            resizedHeight = 0;

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

        private static Rect ScaleFromModelSpace(
            double x1,
            double y1,
            double x2,
            double y2,
            int sourceImageWidth,
            int sourceImageHeight,
            int originalImageWidth,
            int originalImageHeight,
            CoordinateSpace coordinateSpace)
        {
            if (sourceImageWidth <= 0 || sourceImageHeight <= 0 || originalImageWidth <= 0 || originalImageHeight <= 0) return new Rect();

            double minX = Math.Min(x1, x2);
            double minY = Math.Min(y1, y2);
            double maxX = Math.Max(x1, x2);
            double maxY = Math.Max(y1, y2);

            double sourceW;
            double sourceH;

            if (coordinateSpace == CoordinateSpace.Normalized1000)
            {
                sourceW = AssumedProcessingSquareSize;
                sourceH = AssumedProcessingSquareSize;
            }
            else if (coordinateSpace == CoordinateSpace.AssumedSmartResize)
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

        private static VisionModelFamily GetVisionModelFamily(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return VisionModelFamily.Other;

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
                return VisionModelFamily.Qwen;
            }

            if (normalized.StartsWith("gemma3", StringComparison.Ordinal)
                || normalized.StartsWith("gemma3n", StringComparison.Ordinal)
                || normalized.StartsWith("gemma4", StringComparison.Ordinal))
            {
                return VisionModelFamily.Gemma;
            }

            return VisionModelFamily.Other;
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
