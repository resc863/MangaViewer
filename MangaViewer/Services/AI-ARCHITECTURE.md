# AI service architecture

This note summarizes the current AI-related structure after the OCR/translation refactor.

## Translation

Main pieces:
- `TranslationService`
  - owns translation request orchestration
  - caches plain-text and box-aware translation responses
  - normalizes JSON responses for box translation
- `TranslationProviderDescriptor`
  - central registry entry for each provider
  - defines provider name, capability flags, settings accessors, thinking normalization, and client construction
- `TranslationProviders`
  - descriptor registry + provider name parsing helpers
- `TranslationClientFactory`
  - builds `TranslationClientConfiguration` from the active descriptor
- `DelegatingChatClientBase`
  - common wrapper base for provider clients that forward to an inner `IChatClient`

Provider adapters:
- `GoogleGenAIChatClient`
- `OpenAIChatClient`
- `AnthropicChatClient`
- `OllamaChatClient`

Result:
- provider metadata is defined once
- `MangaViewModel` no longer owns translation prompt/caching details
- provider wrappers share more common logic

## OCR

`OcrService` is still the public OCR entry point, but the main responsibilities are now split into helper backends.

### Public entry points
- `GetOcrAsync(...)` for WinRT OCR
- `GetOllamaOcrAsync(...)` for VLM OCR
- `GetHybridOcrAsync(...)` for Hybrid OCR
- `CompileDocLayoutEpContextModelAsync(...)` for EP-context model generation

### Extracted OCR helpers
- `OllamaVlmOcrBackend`
  - full-image VLM OCR orchestration
  - cache lookup/store for `mode=vlm`
- `HybridOcrBackend`
  - ONNX layout + crop OCR orchestration
  - cache lookup/store for `mode=hybrid`
  - partial-box retry for cached hybrid results
- `DocLayoutOnnxBackend`
  - DocLayout ONNX session lifecycle
  - execution-provider selection
  - EP-context compile flow
  - CPU fallback session creation
  - runtime EP logging
- `OllamaOcrProtocol`
  - OCR prompt construction
  - OCR JSON schema generation
  - thinking-strip / crop-text normalization
  - structured response parsing
  - model-family-specific coordinate interpretation

## Request lifecycle split

`OcrService.OllamaOnnx.cs` now separates:
- request lifecycle helpers
  - active request registration
  - timeout handling
  - slot erase handling
  - native Ollama vs OpenAI-compatible request execution
- prompt / schema / parsing helpers
  - all protocol-specific rules in `OllamaOcrProtocol`
- backend orchestration helpers
  - VLM / Hybrid / DocLayout ONNX flow separation

## Maintenance guidance

When adding a new translation provider:
1. add a new `TranslationProviderKind`
2. register a new `TranslationProviderDescriptor`
3. implement or wire the provider `IChatClient`

When changing OCR prompting/parsing:
1. update `OllamaOcrProtocol`
2. keep request transport changes in `OcrService.OllamaOnnx.cs`
3. keep ONNX session/EP changes in `DocLayoutOnnxBackend`
