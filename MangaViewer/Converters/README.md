# Converters

Components
- `BooleanToVisibilityConverter`: `bool` ¡ê `Visibility` conversion.
- `BooleanToBrushConverter`: `bool` ¡ê `Brush` mapping.

Change notes
- Converters should be pure and stateless; ensure thread safety.
- Register as `StaticResource` in XAML. When renaming keys, update XAML resource keys accordingly.
