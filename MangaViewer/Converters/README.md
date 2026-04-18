# Converters

Components
- `BooleanToVisibilityConverter`
  - Converts `bool` to `Visibility.Visible` / `Visibility.Collapsed`.
  - Supports `parameter="Reversed"` to invert the result.
  - `ConvertBack` is not implemented.
- `BooleanToBrushConverter`
  - Maps `true` to a shared green brush, `false` to a shared red brush, and non-boolean values to a shared gray brush.
  - `ConvertBack` is not implemented.

Change notes
- Keep converters stateless apart from safe shared brush instances.
- If converter resource keys change, update all XAML `StaticResource` references together.
- These converters are currently one-way helpers; add `ConvertBack` only if a real two-way binding scenario is introduced.
