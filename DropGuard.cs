using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace ColumnView;

/// <summary>
/// クリックのつもりの小さなずれで、ファイルが隣の行 (フォルダ) へ移動してしまうのを防ぐ。
/// このアプリから始めたファイルのドラッグについて、ドロップ先を次のどちらかを満たしたときだけ「確定」とする:
///  - ドラッグを始めた場所から十分に離れたことがある (遠くへ運んだ = 意図した操作)
///  - そのドロップ先の上に一定時間とどまっている
/// 押してすぐ離した誤操作は、どちらも満たさないので何も起きない。
/// 判定は画面上の距離 (カーソルの実移動量) で行うので、列の入れ替えで中身がずれても惑わされない。
/// 状態は全ウィンドウ共通 (別ウィンドウへのドロップでも同じ判定を使う)。
/// </summary>
internal static class DropGuard
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT pt);

    private static bool _active;
    private static Point _startPx;
    private static double _farPx;
    private static double _maxTravelPx;
    private static string? _target;
    private static long _targetSince;

    /// <summary>ドラッグの開始時に呼ぶ。<paramref name="pressedAt"/> はボタンを押した位置 (source 基準の DIP)。</summary>
    public static void Begin(Visual source, Point pressedAt)
    {
        var settings = AppSettings.Current;
        _active = settings.DropGuard;
        if (!_active)
            return;
        try
        {
            _startPx = source.PointToScreen(pressedAt);
            _farPx = Math.Max(0, settings.DropFarDistance) * VisualTreeHelper.GetDpi(source).DpiScaleX;
        }
        catch (InvalidOperationException)
        {
            _active = false; // 画面に出ていない等。見張りなしで通常どおり
            return;
        }
        _maxTravelPx = 0;
        _target = null;
        _targetSince = 0;
    }

    public static void End() => _active = false;

    /// <summary>今カーソルの下にあるドロップ先 (フォルダのパス) で離したら実行してよいか。
    /// このアプリ以外から来たドラッグや、見張りを切っている場合は常に true。</summary>
    public static bool IsArmed(string? target)
    {
        if (!_active)
            return true;

        if (GetCursorPos(out var p))
        {
            var dx = p.X - _startPx.X;
            var dy = p.Y - _startPx.Y;
            _maxTravelPx = Math.Max(_maxTravelPx, Math.Sqrt(dx * dx + dy * dy));
        }
        if (_maxTravelPx >= _farPx)
            return true;

        var now = Environment.TickCount64;
        if (!string.Equals(_target, target, StringComparison.OrdinalIgnoreCase) || _targetSince == 0)
        {
            _target = target;
            _targetSince = now;
        }
        return now - _targetSince >= Math.Max(0, AppSettings.Current.DropDwellMs);
    }
}
