using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System.Threading;
using System.Threading.Tasks;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// 썸네일 생성 전략 인터페이스. 파일 경로 또는 메모리 바이트를 입력으로 받아 ImageSource를 반환.
    /// 구현은 필요 시 DispatcherQueue를 사용해 UI 스레드에서 안전하게 객체를 생성해야 한다.
    /// </summary>
    public interface IThumbnailProvider
    {
        Task<ImageSource?> GetForPathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim, CancellationToken ct);
        Task<ImageSource?> GetForBytesAsync(DispatcherQueue dispatcher, byte[] data, int maxDecodeDim, CancellationToken ct);
    }
}
