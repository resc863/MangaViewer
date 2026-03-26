# Copilot Instructions

## General Guidelines
- Use English for prompts sent to Ollama in this project.

## Project-Specific Rules
- For hybrid translation overlay font sizing, ignore line count and calculate using box size and translated text length.
- For hybrid OCR, if TensorRT engine build fails, retry ONNX with CPU before falling back to VLM. Default VLM fallback should be off.