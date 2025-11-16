using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System.Threading;
using System.Threading.Tasks;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// Abstraction for creating decoded thumbnail ImageSource objects.
    /// Implementations must:
    ///  - Decode efficiently (potentially off-UI thread) then marshal creation to UI thread if needed.
    ///  - Respect CancellationToken for scroll cancellation.
    ///  - Optionally leverage caching before decoding.
    /// </summary>
    public interface IThumbnailProvider
    {
        /// <summary>Decode thumbnail from file path up to maxDecodeDim (largest of width/height).</summary>
        Task<ImageSource?> GetForPathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim, CancellationToken ct);
        /// <summary>Decode thumbnail from raw bytes (memory gallery) up to maxDecodeDim.</summary>
        Task<ImageSource?> GetForBytesAsync(DispatcherQueue dispatcher, byte[] data, int maxDecodeDim, CancellationToken ct);
    }
}
