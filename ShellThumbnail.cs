using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ColumnView;

/// <summary>
/// IShellItemImageFactory による実サムネイル取得。エクスプローラーのサムネイルと
/// 同じ仕組みで、画像・動画1コマ・PDF先頭・Office など「絵が出せる型」をほぼ網羅する。
/// パス＋更新日時＋サイズでキャッシュし、UI スレッド外から呼ぶ前提。
/// </summary>
public static class ShellThumbnail
{
    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private static readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>キャッシュに載せる上限サイズ。プレビュー用の大サムネイルはメモリを食うので
    /// 載せない (シェル側のサムネイルキャッシュが効くため再取得は速い)。</summary>
    private const int MaxCachedSize = 256;

    /// <summary>キャッシュの件数上限。超えたら丸ごと捨てる (48px 数千件で数十 MB 程度が目安)。</summary>
    private const int MaxCacheEntries = 4096;

    private static string Key(string path, long stamp, int size) => $"{path}|{stamp}|{size}";

    /// <summary>
    /// 指定パスのサムネイルを取得する。取得できなければ null。
    /// <paramref name="allowDownload"/>=false のときはキャッシュ済みのみ (クラウド実体を取りに行かない)。
    /// <paramref name="thumbnailOnly"/>=true のときは本物のサムネイルのみ (アイコンで代用しない)。
    /// </summary>
    public static ImageSource? Get(string path, int size, long stamp, bool allowDownload, bool thumbnailOnly = false)
    {
        if (size > MaxCachedSize || thumbnailOnly)
            return Load(path, size, allowDownload, thumbnailOnly);

        var key = Key(path, stamp, size);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var image = Load(path, size, allowDownload, thumbnailOnly);
        lock (_lock)
        {
            if (_cache.Count >= MaxCacheEntries)
                _cache.Clear();
            _cache[key] = image;
        }
        return image;
    }

    private static ImageSource? Load(string path, int size, bool allowDownload, bool thumbnailOnly)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out var factory);
            if (factory is null)
                return null;

            var flags = SIIGBF.BiggerSizeOk;
            if (!allowDownload)
                flags |= SIIGBF.InCacheOnly; // クラウド専用の実体ダウンロードを誘発しない
            if (thumbnailOnly)
                flags |= SIIGBF.ThumbnailOnly; // アイコンでの代用は失敗として返す

            var hr = factory.GetImage(new SIZE { cx = size, cy = size }, flags, out var hbitmap);
            Marshal.ReleaseComObject(factory);
            if (hr != 0 || hbitmap == IntPtr.Zero)
                return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hbitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// ホバー時のツールチップに添える寸法・長さなどのメタ情報を取得する。
/// 画像の寸法はヘッダーのみ読み取り、動画・音声の長さはシェルのプロパティストアから得る。
/// </summary>
public static class ShellMetadata
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico", ".heic", ".heif",
    };

    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".flv", ".3gp",
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma", ".opus",
    };

    public static bool IsImage(string name) => ImageExts.Contains(Path.GetExtension(name));
    public static bool IsMedia(string name) => MediaExts.Contains(Path.GetExtension(name));

    /// <summary>画像の寸法 (例: 1920 × 1080)。取れなければ null。ヘッダーのみ読むので軽い。</summary>
    public static string? ImageDimensions(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
                return null;
            var frame = decoder.Frames[0];
            return $"{frame.PixelWidth} × {frame.PixelHeight}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>動画・音声の長さ (例: 3:42)。取れなければ null。</summary>
    public static string? MediaDuration(string path)
    {
        try
        {
            var pkey = PKEY_Media_Duration;
            if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_DEFAULT,
                    IID_IPropertyStore, out var store) != 0 || store is null)
                return null;
            try
            {
                store.GetValue(ref pkey, out var pv);
                var value = pv.ReadUInt64AndClear(); // 100ns 単位
                if (value == 0)
                    return null;
                var span = TimeSpan.FromTicks((long)value);
                return span.TotalHours >= 1
                    ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                    : $"{span.Minutes}:{span.Seconds:00}";
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            return null;
        }
    }

    // ---- P/Invoke (プロパティストア) ----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string pszPath, IntPtr pbc, int flags, in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    private const int GPS_DEFAULT = 0;
    private static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    // PKEY_Media_Duration = {64440490-4C8B-11D1-8B70-080036B11A03}, 3
    private static readonly PROPERTYKEY PKEY_Media_Duration = new()
    {
        fmtid = new Guid("64440490-4C8B-11D1-8B70-080036B11A03"),
        pid = 3,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // x64 の PROPVARIANT は 24 バイト (ヘッダ8 + 共用体16)。out で書き戻される全域を
    // 受け切れるよう offset 16 にもフィールドを置いてサイズを確保する。
    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public ulong ulVal;
        [FieldOffset(16)] private IntPtr _reserved;

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        public ulong ReadUInt64AndClear()
        {
            var value = ulVal;
            var copy = this;
            PropVariantClear(ref copy);
            return value;
        }
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }
}
