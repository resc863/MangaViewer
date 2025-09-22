using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System.Threading;
using System.Threading.Tasks;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// ����� ���� ������ �������̽�.
    /// - ���� ��� �Ǵ� �޸� ����Ʈ �Է��� �޾� <see cref="ImageSource"/>�� ��ȯ�մϴ�.
    /// - UI ������ �ʿ��� ��� <see cref="DispatcherQueue"/>�� ����� UI �����忡�� �����ϰ� ��ü�� �����ؾ� �մϴ�.
    /// </summary>
    public interface IThumbnailProvider
    {
        /// <summary>
        /// ���� ��ηκ��� ������� �����մϴ�.
        /// </summary>
        Task<ImageSource?> GetForPathAsync(DispatcherQueue dispatcher, string path, int maxDecodeDim, CancellationToken ct);
        /// <summary>
        /// �޸� ����Ʈ�κ��� ������� �����մϴ�.
        /// </summary>
        Task<ImageSource?> GetForBytesAsync(DispatcherQueue dispatcher, byte[] data, int maxDecodeDim, CancellationToken ct);
    }
}
