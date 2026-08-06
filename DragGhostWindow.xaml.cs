using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

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

    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_NOOWNERZORDER = 0x0200;

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    private const int WM_DPICHANGED = 0x02E0;

    private IntPtr _hwnd;

    /// <summary>行内のどこを掴んだか (カード左上からの DIP)。この点がカーソル直下に来続ける
    /// ことで「行をそのまま持ち上げた」ように見える。カード外にはみ出す値はクランプする。</summary>
    private Point _grabOffset = new(36, 14);

    /// <summary>直前に描画したドロップ効果。GiveFeedback はマウスが止まっていても連続発火するため、
    /// 変化したときだけ描き直さないとカードが毎フレーム再レイアウトされて微振動する。
    /// ドラッグ開始時は「まだ一度も描いていない」を表す null にリセットする。</summary>
    private DropIntent? _intent;

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
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE,
            ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED);

        // スケール率の違うディスプレイへ移ると WPF が WM_DPICHANGED を処理して
        // OS 推奨の矩形へ勝手に動かす。境界上でマウスを止めるとそのまま置き去りに
        // なるため、処理が終わった直後にカーソル位置へ戻す。
        HwndSource.FromHwnd(_hwnd)?.AddHook(OnWndProc);
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

        _intent = null; // 新しいドラッグ。次の SetEffect で必ず描き直す
        SetEffect(DragDropEffects.None);
    }

    /// <summary>ドロップ先が今返している効果をカードの見た目へ反映する
    /// (GiveFeedback のたびに呼ぶ。変化が無ければ何もしない)。</summary>
    /// <param name="noOp">効果としては受理されるが、離しても実際には何も起きない場合に true。
    /// OLE に返す効果はエクスプローラーと揃えたまま、表示だけ「何も起きない」側へ寄せる。</param>
    public void SetEffect(DragDropEffects effects, bool noOp = false)
    {
        if (noOp)
        {
            SetIntent(DropIntent.None);
            return;
        }

        // Scroll (最上位ビット) は「ターゲットが自動スクロール中」を表すフラグで操作ではない。
        // 操作ビットだけを取り出してから判定する。
        var ops = effects & (DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);

        // 操作が 1 つに定まるときだけ操作名を出す。複数立っている = ドロップ先が
        // 「どれでもよい」と返している状態で、実際どうなるかは離した瞬間に決まるため断定しない。
        var intent = ops switch
        {
            DragDropEffects.None => DropIntent.None,
            DragDropEffects.Copy => DropIntent.Copy,
            DragDropEffects.Move => DropIntent.Move,
            DragDropEffects.Link => DropIntent.Link,
            _ => DisambiguateByModifiers(ops),
        };
        SetIntent(intent);
    }

    private void SetIntent(DropIntent intent)
    {
        if (_intent == intent)
            return;
        _intent = intent;

        ApplyIntentVisual(intent);

        // バッジの文字数が変われば SizeToContent でカード幅が変わる。掴み位置のクランプは
        // ActualWidth を見ているので、実寸を確定させてから置き直さないと一瞬ずれる。
        UpdateLayout();
        MoveToCursor();
    }

    /// <summary>ドロップ先が複数の操作を返してきたとき、修飾キーで 1 つに絞り込む。
    /// 絞り込めなければ <see cref="DropIntent.Ambiguous"/> のまま返す。</summary>
    /// <remarks>
    /// Ctrl = コピー / Shift = 移動 / Alt もしくは Ctrl+Shift = リンク は OS 共通の取り決めで、
    /// ドロップ先はこれに従う義務がある。よってキーが押されている間は、ターゲットが
    /// マスクを返していても実際に起きる操作を確定できる。
    /// GiveFeedbackEventArgs はキー状態を持たないので OS へ直接問い合わせる
    /// (OLE のモーダルループ中は WPF の Keyboard.Modifiers が古い値のことがある)。
    /// </remarks>
    private static DropIntent DisambiguateByModifiers(DragDropEffects ops)
    {
        bool ctrl = IsKeyDown(VK_CONTROL);
        bool shift = IsKeyDown(VK_SHIFT);
        bool alt = IsKeyDown(VK_MENU);

        // リンクは Alt 単独でも Ctrl+Shift でも作れる (エクスプローラーと同じ)
        if ((alt || (ctrl && shift)) && ops.HasFlag(DragDropEffects.Link))
            return DropIntent.Link;
        if (ctrl && !shift && !alt && ops.HasFlag(DragDropEffects.Copy))
            return DropIntent.Copy;
        if (shift && !ctrl && !alt && ops.HasFlag(DragDropEffects.Move))
            return DropIntent.Move;
        return DropIntent.Ambiguous;
    }

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    private static bool IsKeyDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    /// <summary>ドロップ効果ごとの見た目 (枠色・バッジ・不透明度) を決める。</summary>
    private void ApplyIntentVisual(DropIntent intent)
    {
        if (intent == DropIntent.None)
        {
            // 既定の姿。「今離しても何も起きない」場所はすべてここに落ちる —
            // 掴んだ直後 (元のフォルダの上) も、受け付けないターゲットの上も同じ状態で、
            // 利用者から見た結果が同じである以上、見た目を分ける理由がない。
            // 置けないことの明示は OS の禁止カーソルに任せる。
            Card.BorderBrush = (Brush)FindResource("AccentDropBorderBrush");
            EffectBadge.Visibility = Visibility.Collapsed;
            return;
        }

        // ここから先は「何かが起きる」場所。バッジが出ていること自体が受理の合図で、
        // 色は結果の種類を表す。緑 = 元が残る (コピー / リンク)、青 = 元が消える (移動)、
        // オレンジ = 受け付けるが操作は離すまで確定しない。
        var (accentKey, tintKey) = intent switch
        {
            DropIntent.Move => ("DropMoveAccentBrush", "DropMoveTintBrush"),
            DropIntent.Ambiguous => ("AccentBrush", "AccentSelectHoverBrush"),
            _ => ("DropCopyAccentBrush", "DropCopyTintBrush"),
        };
        var accent = (Brush)FindResource(accentKey);

        Card.BorderBrush = accent;
        EffectBadge.Background = (Brush)FindResource(tintKey);
        EffectBadge.Visibility = Visibility.Visible;
        EffectGlyph.Foreground = accent;
        EffectText.Foreground = accent;
        (EffectGlyph.Text, EffectText.Text) = intent switch
        {
            DropIntent.Move => ("\uE72A", "移動"),   // Forward
            DropIntent.Link => ("\uE71B", "リンク"), // Link
            DropIntent.Ambiguous => ("\uE896", "ドロップ"), // Download
            _ => ("\uE710", "コピー"),               // Add
        };
    }

    /// <summary>現在のマウスカーソル位置へ追従させる (GiveFeedback のたびに呼ぶ)。</summary>
    /// <remarks>
    /// WPF の Left/Top は「そのウィンドウが今載っているモニターの DIP」で解釈され、
    /// 実際の配置時に WPF がそのモニターの DPI を掛けて物理ピクセルへ戻す。
    /// このため px → DIP の割り算に別ウィンドウ (Owner 等) の DPI を使うと、
    /// スケール率の異なるディスプレイへ跨いだ瞬間に「割った係数」と「掛け戻される係数」が
    /// 食い違い、位置が大きく飛ぶ (境界付近では往復して振動することもある)。
    /// そこで DIP を経由せず、カーソル座標 (元から物理 px) のまま SetWindowPos で直接配置する。
    /// DPI が要るのは掴み位置オフセットの px 換算だけで、そこはゴースト自身の
    /// 現在 DPI を使う = 実際の描画サイズと必ず整合する。
    /// </remarks>
    public void MoveToCursor()
    {
        if (!GetCursorPos(out var p))
            return;

        // ゴーストが今どのスケールで描かれているか。HWND 生成前 (初回 Show 前) は
        // まだ載るモニターが決まっていないので、カーソル直下のモニターで代用する。
        double scale = _hwnd != IntPtr.Zero ? GetDpiForWindow(_hwnd) / 96.0 : DpiScaleAt(p);
        if (scale <= 0)
            scale = 1.0;

        // 掴んだ点がカーソル直下に来るよう配置する。カードより右で掴んだ場合などは
        // カーソルがカードから離れないよう、カードの内側にクランプする
        double cardW = ActualWidth > 0 ? ActualWidth - PadLeft - PadRight : 140;
        double cardH = ActualHeight > 0 ? ActualHeight - PadTop - PadBottom : 30;
        double ox = PadLeft + Math.Clamp(_grabOffset.X, 8, Math.Max(8, cardW - 16));
        double oy = PadTop + Math.Clamp(_grabOffset.Y, 6, Math.Max(6, cardH - 6));

        if (_hwnd == IntPtr.Zero)
        {
            // HWND が無い間は Left/Top しか手段がない。ここは初回 Show 直前の暫定配置で、
            // Show 後に呼ばれる MoveToCursor が SetWindowPos で正確に置き直す。
            Left = p.X / scale - ox;
            Top = p.Y / scale - oy;
            return;
        }

        SetWindowPos(_hwnd, IntPtr.Zero,
            (int)Math.Round(p.X - ox * scale), (int)Math.Round(p.Y - oy * scale), 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private IntPtr OnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DPICHANGED && IsVisible)
        {
            // WPF 自身の WM_DPICHANGED 処理 (移動 + リサイズ) を待ってから置き直す。
            // OLE ドラッグ中のモーダルループもメッセージをポンプするので届く。
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(MoveToCursor));
        }
        return IntPtr.Zero;
    }

    /// <summary>指定スクリーン座標のモニターのスケール率 (96dpi = 1.0)。取得できなければ 0。</summary>
    private static double DpiScaleAt(POINT p)
    {
        var mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero || GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out var dx, out _) != 0)
            return 0;
        return dx / 96.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                            int X, int Y, int cx, int cy, int uFlags);

    /// <summary>ウィンドウが現在扱われている DPI (WM_DPICHANGED と同期して更新される)。</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
