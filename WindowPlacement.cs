using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ColumnView;

/// <summary>保存しておくウィンドウの位置・大きさ。
/// 通常表示時の矩形 (物理ピクセル・ワークスペース座標) と最大化状態を持つ。</summary>
public class SavedWindowPlacement
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// 前回終了時のウィンドウ位置・大きさの保存と復元。
/// PerMonitorV2 ではモニターごとに DPI が違い WPF の Left/Top (DIP) だと位置が曖昧になるため、
/// 一般的なアプリと同じく Win32 の GetWindowPlacement / SetWindowPlacement で物理ピクセルのまま扱う。
/// 最大化中・最小化中に閉じても「元に戻したときの矩形」が残るので、次回はそこから始められる。
/// </summary>
public static class WindowPlacementStore
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref RECT lprc, int dwFlags);

    private const int SW_HIDE = 0;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int WPF_RESTORETOMAXIMIZED = 0x2;
    private const int MONITOR_DEFAULTTONULL = 0;

    /// <summary>ウィンドウの現在の位置・大きさを控える (閉じる直前に呼ぶ)。</summary>
    public static SavedWindowPlacement? Capture(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0)
            return null;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp))
            return null;
        var r = wp.rcNormalPosition;
        if (r.Right <= r.Left || r.Bottom <= r.Top)
            return null;
        return new SavedWindowPlacement
        {
            Left = r.Left,
            Top = r.Top,
            Right = r.Right,
            Bottom = r.Bottom,
            // 最小化中に閉じた場合は「元に戻すと最大化」だったかで判断する
            Maximized = wp.showCmd == SW_SHOWMAXIMIZED
                || (wp.showCmd == SW_SHOWMINIMIZED && (wp.flags & WPF_RESTORETOMAXIMIZED) != 0),
        };
    }

    /// <summary>控えた位置・大きさを表示前のウィンドウに当てる (OnSourceInitialized から呼ぶ)。
    /// モニター構成が変わってどの画面にも掛からなくなった位置は捨て、既定位置で開く。</summary>
    public static void Apply(Window window, SavedWindowPlacement saved)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || saved.Right <= saved.Left || saved.Bottom <= saved.Top)
            return;
        var rect = new RECT { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom };
        if (MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL) == 0)
            return;

        var wp = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            showCmd = SW_HIDE, // まだ表示しない (表示は WPF の Show に任せる)
            rcNormalPosition = rect,
        };
        // DPI の違うモニターへ移ると WM_DPICHANGED で大きさが拡縮されるため、
        // 移った後にもう一度当てて物理ピクセルの大きさを保存時どおりにする
        SetWindowPlacement(hwnd, ref wp);
        SetWindowPlacement(hwnd, ref wp);

        if (saved.Maximized)
            window.WindowState = WindowState.Maximized; // 通常矩形のあるモニターで最大化される
    }
}
