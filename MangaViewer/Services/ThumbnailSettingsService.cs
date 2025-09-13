using System;
using Windows.Storage;

namespace MangaViewer.Services
{
    /// <summary>
    /// 썸네일 관련 사용자 설정 보관/알림 서비스. Decode 너비, 네이티브 사용 여부 등.
    /// </summary>
    public sealed class ThumbnailSettingsService
    {
        public static ThumbnailSettingsService Instance { get; } = new();
        private readonly ApplicationDataContainer _local = ApplicationData.Current.LocalSettings;
        private const string DecodeWidthKey = "ThumbDecodeWidth";
        private const string UseNativeKey = "ThumbUseNative";

        private int _decodeWidth = 150;
        private bool _useNative = false;

        private ThumbnailSettingsService()
        {
            try
            {
                if (_local.Values.TryGetValue(DecodeWidthKey, out var w)
                    && int.TryParse(w?.ToString(), out var parsed) && parsed > 0)
                {
                    _decodeWidth = parsed;
                }
                if (_local.Values.TryGetValue(UseNativeKey, out var n))
                {
                    if (n is bool b) _useNative = b;
                    else if (int.TryParse(n?.ToString(), out var i)) _useNative = i != 0;
                }
            }
            catch { _decodeWidth = 150; _useNative = false; }
        }

        public event EventHandler? SettingsChanged;

        public int DecodeWidth
        {
            get => _decodeWidth;
            set
            {
                int clamped = Math.Clamp(value, 64, 1024);
                if (clamped == _decodeWidth) return;
                _decodeWidth = clamped;
                try { _local.Values[DecodeWidthKey] = clamped; } catch { }
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool UseNative
        {
            get => _useNative;
            set
            {
                if (value == _useNative) return;
                _useNative = value;
                try { _local.Values[UseNativeKey] = value; } catch { }
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
