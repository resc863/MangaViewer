# Controls

Components
- `AnimatedWrapPanel`
  - Custom `Panel` that arranges children in wrapping rows.
  - Applies implicit composition offset animations so items slide smoothly into their new positions when the layout changes.
- `ParagraphGapSliderControl`
  - Small settings control bound directly to `OcrService.Instance`.
  - Lets the user switch between horizontal and vertical paragraph-gap targets and update the active gap factor with a slider.
- `TagWrapPanel`
  - Lightweight non-virtualized wrap panel for tag chips.
  - Supports spacing, min/max item sizing, `ForceWidth`, and `FallbackWrapWidth` so wrapping stays stable inside `ScrollViewer` or `StackPanel` layouts.

Change notes
- `ParagraphGapSliderControl` is stateful because it mirrors current OCR settings; if OCR setting names change, update this control and `OcrService` together.
- `TagWrapPanel` intentionally favors predictable wrapping over virtualization. Use it for moderate item counts, not unbounded feeds.
- Keep layout work inexpensive in custom panels because these controls are used in scrollable, image-heavy UI.
