using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ColumnView;

/// <summary>
/// ドラッグ中にマウスカーソルへ追従する小さなプレビュー (アイコン + 名前 + 件数バッジ)。
/// OLE ドラッグの既定カーソルだけでは「何を運んでいるか」が分かりにくいため、
/// 自前の浮動ウィンドウで補う。クリックスルー (WS_EX_TRANSPARENT) にすることで、
/// 自分の下にある本来のドロップ対象 (自アプリの列 / エクスプローラー等) へ
/// マウスイベント・OLE ドラッグイベントの両方をそのまま通す。
/// </summary>
public partial class DragGhostWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    /// <summary>行内のどこを掴んだか (カード左上からの DIP)。この点がカーソル直下に来続ける
    /// ことで「行をそのまま持ち上げた」ように見える。カード外にはみ出す値はクランプする。</summary>
    private Point _grabOffset = new(36, 14);

    /// <summary>XAML の外周 Grid Margin (左上)。カードの左上はウィンドウ原点からこの分ずれている。</summary>
    private const double PadLeft = 10, PadTop = 10;
    /// <summary>同 (右下)。カード実寸 = ウィンドウ実寸 − これら。</summary>
    private const double PadRight = 14, PadBottom = 14;

    public DragGhostWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED);
    }

    /// <summary>表示内容を設定する。count が 2 以上なら重なりカード + 件数バッジを出す。
    /// grabOffset は掴んだ位置 (行の左上からの DIP)。</summary>
    public void SetContent(ImageSource? icon, string name, int count, Point grabOffset)
    {
        IconImage.Source = icon;
        IconImage.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        NameText.Text = name;
        _grabOffset = grabOffset;

        var multi = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Stack1.Visibility = multi;
        Stack2.Visibility = multi;
        CountBadge.Visibility = multi;
        if (count > 1)
            CountText.Text = count.ToString();
    }

    /// <summary>現在のマウスカーソル位置へ追従させる (GiveFeedback のたびに呼ぶ)。</summary>
    public void MoveToCursor()
    {
        if (!GetCursorPos(out var p))
            return;
        // このウィンドウ自身はまだ Show 前の可能性があるため、ドラッグ元の
        // Owner ウィンドウの DPI を基準にスクリーン座標(px) → DIP へ変換する
        var dpi = VisualTreeHelper.GetDpi(Owner ?? Application.Current.MainWindow ?? (Window)this);

        // 掴んだ点がカーソル直下に来るよう配置する。カードより右で掴んだ場合などは
        // カーソルがカードから離れないよう、カードの内側にクランプする
        double cardW = ActualWidth > 0 ? ActualWidth - PadLeft - PadRight : 140;
        double cardH = ActualHeight > 0 ? ActualHeight - PadTop - PadBottom : 30;
        double ox = PadLeft + Math.Clamp(_grabOffset.X, 8, Math.Max(8, cardW - 16));
        double oy = PadTop + Math.Clamp(_grabOffset.Y, 6, Math.Max(6, cardH - 6));

        Left = p.X / dpi.DpiScaleX - ox;
        Top = p.Y / dpi.DpiScaleY - oy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
