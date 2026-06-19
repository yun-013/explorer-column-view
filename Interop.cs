using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ColumnView;

/// <summary>
/// Seer (https://1218.io) 連携。
/// 公式SDK (ccseer/Seer-sdk seer/ipc.h) の WM_COPYDATA プロトコルを使用する。
/// </summary>
public static class SeerInterop
{
    private const string SeerClassName = "SeerWindowClass";
    private const int SEER_INVOKE_W32 = 5000;
    private const uint WM_COPYDATA = 0x004A;

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public nint dwData;
        public int cbData;
        public nint lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, ref COPYDATASTRUCT lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    private static DateTime _lastInvoke = DateTime.MinValue;

    public static bool IsRunning => FindWindow(SeerClassName, null) != 0;

    public static bool IsPreviewVisible
    {
        get
        {
            var hwnd = FindWindow(SeerClassName, null);
            return hwnd != 0 && IsWindowVisible(hwnd);
        }
    }

    /// <summary>プレビューのトグル。同じパスなら閉じ、別のパスなら切り替わる。</summary>
    public static bool Toggle(string path)
    {
        var hwnd = FindWindow(SeerClassName, null);
        if (hwnd == 0)
            return false;

        // ipc.h: SEER_INVOKE_* の最小間隔は 200ms
        if ((DateTime.UtcNow - _lastInvoke).TotalMilliseconds < 200)
            return true;
        _lastInvoke = DateTime.UtcNow;

        var ptr = Marshal.StringToHGlobalUni(path);
        try
        {
            var cds = new COPYDATASTRUCT
            {
                dwData = SEER_INVOKE_W32,
                cbData = (path.Length + 1) * 2,
                lpData = ptr,
            };
            SendMessage(hwnd, WM_COPYDATA, 0, ref cds);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return true;
    }
}

/// <summary>
/// SHGetFileInfo によるファイルアイコン取得。拡張子単位でキャッシュし、
/// SHGFI_USEFILEATTRIBUTES を使うことでクラウドのオンライン専用ファイルに
/// 触れない（ダウンロードを誘発しない)。
/// </summary>
public static class IconCache
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private static readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>拡張子ベースの汎用アイコン。ディスクに触れない。</summary>
    public static ImageSource? GetByExtension(string? extension, bool isDirectory)
    {
        var key = isDirectory ? "<dir>" : (string.IsNullOrEmpty(extension) ? "<none>" : extension);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var icon = Load(
                isDirectory ? "folder" : "file" + (string.IsNullOrEmpty(extension) ? "" : extension),
                isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                SHGFI_USEFILEATTRIBUTES);
            _cache[key] = icon;
            return icon;
        }
    }

    /// <summary>実パスからのアイコン (ドライブ・特殊フォルダ用)。パス単位でキャッシュ。</summary>
    public static ImageSource? GetByPath(string path)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var cached))
                return cached;

            var icon = Load(path, 0, 0);
            _cache[path] = icon;
            return icon;
        }
    }

    private static ImageSource? Load(string path, uint attributes, uint extraFlags)
    {
        var shfi = new SHFILEINFO();
        var result = SHGetFileInfo(path, attributes, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | extraFlags);
        if (result == 0 || shfi.hIcon == 0)
            return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }
}

public static class NaturalSort
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public static readonly Comparison<string> Compare = (x, y) => StrCmpLogicalW(x, y);
}
