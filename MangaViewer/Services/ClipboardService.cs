using System;
using Windows.ApplicationModel.DataTransfer;

namespace MangaViewer.Services
{
    /// <summary>
    /// ClipboardService
    /// High-level purpose: Provide a tiny, testable abstraction over the WinRT clipboard API so other
    /// parts of the app can copy text (e.g. OCR results) without depending directly on static WinRT
    /// calls. Centralizes error handling and makes future migration (Win32, cross?platform) easier.
    /// Thread-safety: The underlying Clipboard API is assumed UI-thread safe; we perform no extra locking.
    /// Failure handling: Returns false on any exception (silently swallowed). Callers typically ignore failures.
    /// Extension ideas:
    ///  - Add GetText() with defensive null/format checks.
    ///  - Support richer DataPackage content (images, HTML) if needed.
    ///  - Queue requests from background threads via Dispatcher if API requires UI thread in future.
    /// </summary>
    public sealed class ClipboardService
    {
        // Lazy singleton pattern keeps construction cost minimal and allows easy replacement in tests.
        private static readonly Lazy<ClipboardService> _instance = new(() => new ClipboardService());
        public static ClipboardService Instance => _instance.Value;
        private ClipboardService() { }

        /// <summary>
        /// Copies plain text to the system clipboard.
        /// Preconditions: Non-empty string.
        /// Postconditions: Clipboard contains the provided text (best effort); returns success flag.
        /// </summary>
        public bool SetText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false; // Fast reject avoids WinRT allocation.
            try
            {
                var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                dp.SetText(text);
                Clipboard.SetContent(dp);
                Clipboard.Flush(); // Ensure persistence after app exit.
                return true;
            }
            catch { return false; }
        }
    }
}
