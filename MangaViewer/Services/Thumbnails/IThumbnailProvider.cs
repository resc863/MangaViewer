using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System.Threading;
using System.Threading.Tasks;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// ����� ���� ���� �������̽�. ���� ��� �Ǵ� �޸� ����Ʈ�� �Է����� �޾� ImageSource�� ��ȯ.
    /// ������ �ʿ� �� DispatcherQueue�� ����� UI �����忡�� �����ϰ� ��ü�� �����ؾ� �Ѵ�.
    /// </summary>
    public interface IThumbnailProvider
    {
        Task<ImageSource?> GetForPathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim, CancellationToken ct);
        Task<ImageSource?> GetForBytesAsync(DispatcherQueue dispatcher, byte[] data, int maxDecodeDim, CancellationToken ct);
    }
}
