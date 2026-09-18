using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ColumnView;

// ---- 余白からの範囲選択 (ラバーバンド) ----
//
// 列の余白 (最終行より下・行間・行の左右) を押してドラッグすると、枠に掛かった行を
// まとめて選択する。行は列幅いっぱいに並ぶので、選択は実質「縦方向の範囲」で決まる。
// 列は仮想化 + 項目単位スクロールで行のピクセル位置を全部は持てないため、
// 起点は「どの行の直後 (または行内) か」という行番号で覚え、スクロールしても崩れないようにする。
public partial class MainWindow
{
    private sealed class BandState
    {
        public required ListBox List { get; init; }
        public required ScrollViewer Viewer { get; init; }
        public required ColumnModel Column { get; init; }
        /// <summary>押した位置 (ScrollViewer 座標)。しきい値判定と枠の X に使う。</summary>
        public Point Start { get; init; }
        /// <summary>起点がどの行の後ろにあるか (-1 = 先頭行より上)。Inside なら行内。</summary>
        public int AnchorIndex { get; init; }
        public bool AnchorInside { get; init; }
        /// <summary>起点の Y を「基準行の上端からの距離」で持つ (スクロール後の描画位置の復元用)。</summary>
        public int RefIndex { get; init; }
        public double RefOffset { get; init; }
        /// <summary>Ctrl 押下で始めたときの元の選択 (これに範囲を足す)。</summary>
        public HashSet<object> Baseline { get; } = new();
        public bool Active { get; set; }
        public RubberBandAdorner? Adorner { get; set; }
        public DispatcherTimer? Timer { get; set; }
    }

    private readonly record struct RowBounds(int Index, double Top, double Bottom);

    private BandState? _band;

    /// <summary>余白を押したときに呼ぶ。範囲選択の候補として覚え、マウスを捕まえる
    /// (ドラッグ開始はしきい値を超えてから)。</summary>
    private void TryBeginBand(object sender, MouseButtonEventArgs e)
    {
        // ホーム列・グループ列はナビゲーション用の行なので対象外 (フォルダ列と検索列のみ)
        if (e.ClickCount != 1 || sender is not ListBox lb
            || lb.DataContext is not ColumnModel { } column || (column.Path is null && !column.IsSearch)
            || lb.Items.Count == 0
            || FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null
            || FindDescendant<ScrollViewer>(lb) is not { } sv)
            return;

        var p = e.GetPosition(sv);
        var rows = RealizedRows(lb, sv);
        if (rows.Count == 0)
            return;
        var (index, inside) = ResolveRow(rows, p.Y);
        var refRow = rows.FirstOrDefault(r => r.Index == index);
        if (refRow == default)
            refRow = rows[0];

        _band = new BandState
        {
            List = lb,
            Viewer = sv,
            Column = column,
            Start = p,
            AnchorIndex = index,
            AnchorInside = inside,
            RefIndex = refRow.Index,
            RefOffset = p.Y - refRow.Top,
        };
        lb.Focus();
        // ListBox 自身にキャプチャさせると、標準の「押したまま行をなぞって選択」と
        // 自動スクロールが動いて選択を上書きするため、内側の ScrollViewer で捕まえる
        sv.CaptureMouse();
        sv.LostMouseCapture += Band_LostMouseCapture;
        e.Handled = true;
    }

    /// <summary>範囲選択中なら true (呼び出し側はそれ以上処理しない)。</summary>
    private bool BandMouseMove(MouseEventArgs e)
    {
        if (_band is not { } band)
            return false;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndBand();
            return true;
        }

        if (!band.Active)
        {
            var p = e.GetPosition(band.Viewer);
            if (Math.Abs(p.X - band.Start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - band.Start.Y) < SystemParameters.MinimumVerticalDragDistance)
                return true;
            ActivateBand(band);
        }
        UpdateBand(band);
        return true;
    }

    private void ActivateBand(BandState band)
    {
        band.Active = true;
        var lb = band.List;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            foreach (var sel in lb.SelectedItems)
                band.Baseline.Add(sel);

        if (AdornerLayer.GetAdornerLayer(lb) is { } layer)
        {
            var accent = (lb.TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.SteelBlue;
            band.Adorner = new RubberBandAdorner(lb, accent);
            layer.Add(band.Adorner);
        }

        // カーソルが列の上下にはみ出したまま止まっていてもスクロールを続けるためのタイマー
        band.Timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        band.Timer.Tick += (_, _) => BandAutoScroll(band);
        band.Timer.Start();
    }

    private void BandAutoScroll(BandState band)
    {
        if (_band != band)
            return;
        var sv = band.Viewer;
        var y = Mouse.GetPosition(sv).Y;
        const double speedPerPx = 1 / 30.0; // はみ出し量 30px ごとに 1 行ずつ速く
        double step = 0;
        if (y < 0)
            step = -(1 + Math.Floor(-y * speedPerPx));
        else if (y > sv.ActualHeight)
            step = 1 + Math.Floor((y - sv.ActualHeight) * speedPerPx);
        if (step == 0)
            return;

        var before = sv.VerticalOffset;
        // 列は項目単位スクロール (CanContentScroll) なので 1 = 1 行
        sv.ScrollToVerticalOffset(before + (sv.CanContentScroll ? step : step * 24));
        sv.UpdateLayout();
        if (sv.VerticalOffset != before)
            UpdateBand(band);
    }

    private void UpdateBand(BandState band)
    {
        var lb = band.List;
        var sv = band.Viewer;
        var rows = RealizedRows(lb, sv);
        if (rows.Count == 0)
            return;

        var p = Mouse.GetPosition(sv);
        double height = sv.ActualHeight;
        double curY = Math.Clamp(p.Y, 0, Math.Max(0, height - 1));
        var (curIndex, curInside) = ResolveRow(rows, curY);

        // 起点の現在の表示位置。基準行が画面外なら上下の端に張り付ける
        double anchorY;
        var refRow = rows.FirstOrDefault(r => r.Index == band.RefIndex);
        if (refRow != default)
            anchorY = refRow.Top + band.RefOffset;
        else
            anchorY = band.RefIndex < rows[0].Index ? double.NegativeInfinity : double.PositiveInfinity;

        // 上側の点は「行内なら その行から / 行の後ろなら 次の行から」、下側の点は「上端を越えた最後の行まで」
        bool anchorUpper = band.AnchorIndex < curIndex || (band.AnchorIndex == curIndex && anchorY <= curY);
        var (upIndex, upInside) = anchorUpper ? (band.AnchorIndex, band.AnchorInside) : (curIndex, curInside);
        int downIndex = anchorUpper ? curIndex : band.AnchorIndex;
        int from = Math.Max(0, upInside ? upIndex : upIndex + 1);
        int to = Math.Min(lb.Items.Count - 1, downIndex);

        var target = new HashSet<object>(band.Baseline);
        for (int i = from; i <= to; i++)
            target.Add(lb.Items[i]);
        ApplyBandSelection(lb, target, from, to);

        if (band.Adorner is { } adorner)
        {
            double x1 = Math.Clamp(band.Start.X, 0, sv.ActualWidth);
            double x2 = Math.Clamp(p.X, 0, sv.ActualWidth);
            double y1 = Math.Clamp(anchorY, 0, height);
            double y2 = Math.Clamp(p.Y, 0, height);
            var a = sv.TranslatePoint(new Point(Math.Min(x1, x2), Math.Min(y1, y2)), lb);
            adorner.SetRect(new Rect(a, new Size(Math.Abs(x2 - x1), Math.Abs(y2 - y1))));
        }
    }

    /// <summary>選択を target に揃える。差分だけ足し引きするので、ドラッグ中の毎回の更新は軽い。</summary>
    private static void ApplyBandSelection(ListBox lb, HashSet<object> target, int from, int to)
    {
        var current = new HashSet<object>(lb.SelectedItems.Cast<object>());
        foreach (var sel in current)
            if (!target.Contains(sel))
                lb.SelectedItems.Remove(sel);
        // 範囲は上から順に足す (SelectedItem = 最初に選ばれた項目 になるため)
        for (int i = from; i <= to; i++)
            if (lb.Items[i] is { } item && !current.Contains(item))
                lb.SelectedItems.Add(item);
        foreach (var item in target)
            if (!current.Contains(item) && !lb.SelectedItems.Contains(item))
                lb.SelectedItems.Add(item);
    }

    private void Band_LostMouseCapture(object sender, MouseEventArgs e) => EndBand();

    private void EndBand()
    {
        if (_band is not { } band)
            return;
        _band = null; // 先に下ろす (ReleaseMouseCapture → LostMouseCapture で再入するため)
        band.Timer?.Stop();
        band.Viewer.LostMouseCapture -= Band_LostMouseCapture;
        if (band.Viewer.IsMouseCaptured)
            band.Viewer.ReleaseMouseCapture();
        if (band.Adorner is { } adorner)
            AdornerLayer.GetAdornerLayer(band.List)?.Remove(adorner);
        if (!band.Active)
            return;

        // ドラッグ中は子の列の展開を止めていたので、確定した選択でまとめて反映する
        NotifyBandSelection(band.List, band.Column, final: true);
        var lb = band.List;
        if (lb.SelectedItems.Count > 0
            && lb.ItemContainerGenerator.ContainerFromItem(lb.SelectedItems[^1]) is ListBoxItem container)
            container.Focus();
    }

    /// <summary>範囲選択中の SelectionChanged の代わり。1 件選択でもフォルダを開かず、
    /// 子の列を畳んで件数だけ出す (確定時 final=true のときだけ 1 件を通常選択として扱う)。</summary>
    private async void NotifyBandSelection(ListBox lb, ColumnModel column, bool final)
    {
        int count = lb.SelectedItems.Count;
        if (final && count == 1 && lb.SelectedItem is FileSystemItem item)
        {
            await _vm.OnItemSelectedAsync(column, item);
            return;
        }
        _vm.OnMultiSelect(column, count);
        if (count == 0)
            _vm.StatusText = "";
    }

    /// <summary>実体化済みで表示中の行 (ScrollViewer 座標、行番号順)。</summary>
    private static List<RowBounds> RealizedRows(ListBox lb, ScrollViewer sv)
    {
        var rows = new List<RowBounds>();
        if (FindDescendant<ItemsPresenter>(lb) is not { } presenter
            || VisualTreeHelper.GetChildrenCount(presenter) == 0
            || VisualTreeHelper.GetChild(presenter, 0) is not Panel panel)
            return rows;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
        {
            if (VisualTreeHelper.GetChild(panel, i) is not ListBoxItem { IsVisible: true } c)
                continue;
            int index = lb.ItemContainerGenerator.IndexFromContainer(c);
            if (index < 0)
                continue;
            var top = c.TranslatePoint(new Point(0, 0), sv).Y;
            rows.Add(new RowBounds(index, top, top + c.ActualHeight));
        }
        rows.Sort((a, b) => a.Index.CompareTo(b.Index));
        return rows;
    }

    /// <summary>Y 位置が「どの行の後ろ (または行内) か」。上端が y 以下の最後の行を返す。</summary>
    private static (int Index, bool Inside) ResolveRow(List<RowBounds> rows, double y)
    {
        int index = rows[0].Index - 1;
        bool inside = false;
        foreach (var r in rows)
        {
            if (r.Top > y)
                break;
            index = r.Index;
            inside = y < r.Bottom;
        }
        return (index, inside);
    }

    /// <summary>範囲選択の枠。ヒットテストには関与しない。</summary>
    private sealed class RubberBandAdorner : Adorner
    {
        private readonly Brush _fill;
        private readonly Pen _pen;
        private Rect _rect = Rect.Empty;

        public RubberBandAdorner(UIElement adorned, Color accent) : base(adorned)
        {
            IsHitTestVisible = false;
            _fill = new SolidColorBrush(Color.FromArgb(0x26, accent.R, accent.G, accent.B));
            _fill.Freeze();
            _pen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, accent.R, accent.G, accent.B)), 1);
            _pen.Freeze();
        }

        public void SetRect(Rect rect)
        {
            _rect = rect;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_rect.IsEmpty || _rect.Width < 1 && _rect.Height < 1)
                return;
            // 1px 線をピクセル境界に合わせてにじませない
            var r = new Rect(Math.Round(_rect.X) + 0.5, Math.Round(_rect.Y) + 0.5,
                             Math.Round(_rect.Width), Math.Round(_rect.Height));
            dc.DrawRoundedRectangle(_fill, _pen, r, 3, 3);
        }
    }
}
