# Copilot Instructions

## General Guidelines
- Use English for prompts sent to Ollama in this project.
- Expect complete localization coverage when asking for multilingual support, not partial translation of only a few screens.

## Project-Specific Rules
- For hybrid translation overlay font sizing, ignore line count and calculate using box size and translated text length.
- For hybrid OCR, if TensorRT engine build fails, retry ONNX with CPU before falling back to VLM. Default VLM fallback should be off.
- In OCR prompt tuning, avoid including recommended output field structure/schema wording because it can conflict with model prior behavior. For Gemma-family OCR prompts, explicitly request recognizing text and returning JSON with bounding boxes, while also avoiding output-structure and coordinate-format guidance; accept model-native output and adapt parsing to fields like box_2d/label. 
- Apply direct OCR prompt instructions, including concise JSON structure examples for Qwen and Gemma, even when they conflict with current repo prompt-tuning guidance.
- Adjust thinking control settings for models that support Thinking, including in Hybrid OCR mode (not only VLM).