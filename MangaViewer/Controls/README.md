# Controls

Components
- `AnimatedWrapPanel`: thumbnail layout panel with lightweight animations.
- `ParagraphGapSliderControl`: UI for OCR/paragraph spacing tweaks.
- `TagWrapPanel`: displays tag chips with wrapping.

Change notes
- Keep app state in view models; controls focus on visuals only.
- Performance: when laying out many children, minimize measure/arrange cost; consider virtualization when feasible.
