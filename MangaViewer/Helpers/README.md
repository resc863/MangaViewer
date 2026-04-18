# Helpers

Components
- `BaseViewModel`
  - Base `INotifyPropertyChanged` implementation with `SetProperty` and `OnPropertyChanged`.
- `RelayCommand`
  - Simple synchronous `ICommand` with optional `canExecute` predicate and `RaiseCanExecuteChanged()`.
- `AsyncRelayCommand`
  - Async `ICommand` wrapper that prevents re-entrancy while the task is running and raises `CanExecuteChanged` automatically.
- `DispatcherHelper`
  - Centralized UI-thread marshalling helpers: `RunOnUiAsync<T>`, `RunOnUiAsync`, `RunOnUi`, and `HasThreadAccess`.
- `LocalizationHelper`
  - Resource lookup helper with fallback text support via `ResourceLoader.GetForViewIndependentUse()`.

Usage notes
- Use `SetProperty` for backing fields so redundant notifications are suppressed.
- `AsyncRelayCommand` currently guards against duplicate execution but does not expose built-in cancellation.
- Prefer `DispatcherHelper` over repeating `DispatcherQueue.TryEnqueue` boilerplate across services and view models.
- For localized UI text, always provide a meaningful fallback when calling `LocalizationHelper.GetString(...)`.
