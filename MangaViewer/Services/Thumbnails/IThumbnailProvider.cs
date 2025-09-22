using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System.Threading;
using System.Threading.Tasks;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// 썸네일 생성 제공자 인터페이스.
    /// - 파일 경로 또는 메모리 바이트 입력을 받아 <see cref="ImageSource"/>를 반환합니다.
    /// - UI 접근이 필요한 경우 <see cref="DispatcherQueue"/>를 사용해 UI 스레드에서 안전하게 객체를 생성해야 합니다.
    /// </summary>
    public interface IThumbnailProvider
    {
        /// <summary>
        /// 파일 경로로부터 썸네일을 생성합니다.
        /// </summary>
        Task<ImageSource?> GetForPathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim, CancellationToken ct);
        /// <summary>
        /// 메모리 바이트로부터 썸네일을 생성합니다.
        /// </summary>
        Task<ImageSource?> GetForBytesAsync(DispatcherQueue dispatcher, byte[] data, int maxDecodeDim, CancellationToken ct);
    }
}
