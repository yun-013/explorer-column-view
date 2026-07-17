using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ColumnView;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>最後にアクティブだったウィンドウ。別プロセスから転送された
    /// 「フォルダーを開く」要求 (単一インスタンス) の届け先になる。</summary>
    public static MainWindow? LastActivated { get; private set; }

    public MainWindow() : this(new MainViewModel()) { }

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
        _vm.TabsEmptied += OnTabsEmptied;

        Activated += (_, _) => LastActivated = this;
        Closed += (_, _) => { if (LastActivated == this) LastActivated = null; };

        // パンくずが長いときは先頭ではなく末尾 (現在地) を見せる
        _vm.Breadcrumbs.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => CrumbScroll.ScrollToRightEnd()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        CrumbScroll.SizeChanged += (_, _) => CrumbScroll.ScrollToRightEnd();

        // タブの増減・並べ替えで区切り線の表示状態を追従させる
        _vm.Tabs.CollectionChanged += (_, _) => ScheduleTabSeparatorUpdate();

        // Quick Look が開いている間、選択を変えるとプレビューを追従させる
        _vm.PreviewFollow = FollowQuickLook;
    }

    /// <summary>別プロセス (フォルダーのダブルクリックや Win+E) から渡されたフォルダーを
    /// 新しいタブで開き、ウィンドウを前面に出す。null はホームタブ。</summary>
    public async Task OpenFolderTabAsync(string? path)
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        await _vm.NewTabAsync(path);
    }

    // ---- Quick Look プレビュー (スペースキー) ----

    private QuickLookWindow? _quickLook;

    /// <summary>スペースキー: プレビューを開く / 同じ項目なら閉じる。</summary>
    private void ToggleQuickLook(FileSystemItem? item)
    {
        if (item is null || item.IsGroupEntry)
            return;
        if (_quickLook is { IsVisible: true } ql && ql.CurrentPath == item.Path)
        {
            ql.CloseQuickLook();
            return;
        }
        _quickLook ??= new QuickLookWindow { Owner = this };
        _quickLook.ShowFor(item, this);
    }

    /// <summary>選択変更に追従してプレビューの中身だけ差し替える (開いているときのみ)。</summary>
    private void FollowQuickLook(FileSystemItem item)
    {
        if (item.IsGroupEntry)
            return;
        if (_quickLook is { IsVisible: true })
            _quickLook.ShowFor(item, this);
    }

    private bool CloseQuickLookIfOpen()
    {
        if (_quickLook is { IsVisible: true } ql)
        {
            ql.CloseQuickLook();
            return true;
        }
        return false;
    }

    // ---- タブ列の見た目 (区切り線) と横スクロール ----

    /// <summary>タブ区切り線の更新をレイアウト確定後に 1 回だけ実行する。</summary>
    private void ScheduleTabSeparatorUpdate()
        => Dispatcher.BeginInvoke(new Action(UpdateTabSeparators),
            System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>
    /// タブ間の区切り線: 隣り合う2つが両方とも非アクティブのときだけ表示する (Files/Chrome 風)。
    /// テンプレートのトリガーだけでは「アクティブタブの左隣」を消せないためここで補正する。
    /// タブは高々十数個なのでループのコストは無視できる。
    /// </summary>
    private void UpdateTabSeparators()
    {
        var gen = TabStrip.ItemContainerGenerator;
        int count = TabStrip.Items.Count;
        int sel = TabStrip.SelectedIndex;
        for (int i = 0; i < count; i++)
        {
            if (gen.ContainerFromIndex(i) is not ListBoxItem item)
                continue;
            item.ApplyTemplate();
            if (item.Template.FindName("Sep", item) is not Border sep)
                continue;
            // 最後のタブと、アクティブタブの左隣はローカル値で消す。
            // それ以外はローカル値を外してテンプレートのトリガー (自身が選択/ホバー中は消す) に任せる
            if (i == count - 1 || i == sel - 1)
                sep.Visibility = Visibility.Collapsed;
            else
                sep.ClearValue(VisibilityProperty);
        }
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabStrip.SelectedItem is { } sel)
            TabStrip.ScrollIntoView(sel);
        ScheduleTabSeparatorUpdate();
    }

    /// <summary>タブが増えてもキャプションボタンを押し出さないよう、タブ列の幅を「+ ボタンを除いた残り」に収める。</summary>
    private void CaptionBar_SizeChanged(object sender, SizeChangedEventArgs e)
        => TabStrip.MaxWidth = Math.Max(0, TabArea.ActualWidth - NewTabButton.Width - 16);

    /// <summary>タブ列の上では通常ホイールでも横スクロール (スクロールバーは出さない)。1ノッチ=40px。</summary>
    private void TabStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindDescendant<ScrollViewer>(TabStrip) is { } sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
                return hit;
            if (FindDescendant<T>(child) is { } deep)
                return deep;
        }
        return null;
    }

    /// <summary>アクティブタブを前後に切り替える (Ctrl+Tab / Ctrl+Shift+Tab)。</summary>
    private void CycleTab(int dir)
    {
        var tabs = _vm.Tabs;
        if (tabs.Count < 2 || _vm.ActiveTab is not { } current)
            return;
        int idx = tabs.IndexOf(current);
        if (idx < 0)
            return;
        _vm.ActiveTab = tabs[(idx + dir + tabs.Count) % tabs.Count];
    }

    /// <summary>カラム表示エリアのホイール: Shift+ホイール、または列リスト外 (余白) では横スクロール。</summary>
    private void Columns_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var overList = FindAncestor<ListBox>(e.OriginalSource as DependencyObject) != null;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || !overList)
        {
            ColumnsScroll.ScrollToHorizontalOffset(ColumnsScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    /// <summary>タブが 0 個になったとき: 他にウィンドウがあれば閉じ、無ければ新規タブを開く。</summary>
    private void OnTabsEmptied()
    {
        if (Application.Current.Windows.OfType<MainWindow>().Count() > 1)
            Close();
        else
            _ = _vm.NewTabAsync(null);
    }

    // ---- コピー / 切り取りの視覚マーク & クリップボード監視 ----

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WM_SETTINGCHANGE = 0x001A;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    /// <summary>全ウィンドウの表示にコピー / 切り取りマークを反映する。</summary>
    private static void RefreshClipboardMarksAllWindows()
    {
        foreach (var w in Application.Current.Windows.OfType<MainWindow>())
            w._vm.ApplyClipboardMarks();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        AddClipboardFormatListener(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook(ClipboardWndProc);

        // Win11 の DWM にウィンドウ自体を角丸にしてもらう
        // (標準枠を消しているため四隅が黒く残らないように)。Win10 では失敗しても無害
        try
        {
            var pref = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch
        {
            // dwmapi が無い環境では従来どおり
        }
    }

    private nint ClipboardWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // システムのライト/ダーク切替に追従
        if (msg == WM_SETTINGCHANGE)
        {
            try
            {
                if (lParam != 0 &&
                    System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                    Dispatcher.BeginInvoke(new Action(App.ApplySystemTheme));
            }
            catch
            {
                // lParam が文字列でないブロードキャストは無視
            }
            return 0;
        }

        // チルトホイール (WPF は WM_MOUSEHWHEEL を標準ではルーティングしない)
        if (msg == WM_MOUSEHWHEEL)
        {
            int delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF)); // 正 = 右チルト
            if (TabStrip.IsMouseOver)
            {
                if (FindDescendant<ScrollViewer>(TabStrip) is { } sv)
                    sv.ScrollToHorizontalOffset(sv.HorizontalOffset + delta / 3.0);
                handled = true;
            }
            else if (ColumnsScroll.IsMouseOver)
            {
                ColumnsScroll.ScrollToHorizontalOffset(ColumnsScroll.HorizontalOffset + delta);
                handled = true;
            }
            return 0;
        }

        if (msg == WM_CLIPBOARDUPDATE && !ClipboardMarks.IsEmpty)
        {
            // 自アプリの書き込み直後にも飛んでくるので、中身がマークと一致していれば維持し、
            // 他アプリに書き換えられていたらマークを解除する
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                if (ClipboardMarks.IsEmpty)
                    return;
                var files = ClipboardOps.GetFiles(out var cut);
                if (ClipboardMarks.Matches(files, cut))
                    return;
                ClipboardMarks.Clear();
                RefreshClipboardMarksAllWindows();
            });
        }
        return 0;
    }

    // ---- ウィンドウ操作 (カスタムタイトルバー) ----

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>カラム表示カードの角丸に合わせて中身をクリップ (列の背景や区切り線が角からはみ出さないように)。
    /// カード自身ではなく中身の ScrollViewer に掛ける (カードに掛けると影ごと切られる)。リサイズ時のみ。</summary>
    private void ColumnsCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        fe.Clip = new RectangleGeometry(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), 11, 11);
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // 最大化時はリサイズ枠ぶんはみ出すのでパディングで内側に収める
        RootDock.Margin = maximized ? new Thickness(7) : new Thickness(0);
        RootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        // 最大化時は画面いっぱいになるため角丸を解除 (透明な欠けを防ぐ)
        RootBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(10);
        MaxButton.Content = char.ConvertFromUtf32(maximized ? 0xE923 : 0xE922); // 元に戻す / 最大化
    }

    // ---- タブ操作 ----

    private async void NewTab_Click(object sender, RoutedEventArgs e)
        => await _vm.NewTabAsync(null);

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TabModel tab)
            _vm.CloseTab(tab);
    }

    // ---- タブの引き剝がし & 再結合 (Chrome 風) ----

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint p);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    private const int VK_LBUTTON = 0x01;

    private Point _tabDragStart;
    private TabModel? _tabDragModel;
    private bool _reordering;
    private double _grabOffX = 70;
    private double _grabOffY = 18;
    private double _grabInTab;        // タブ内の掴んだ X 位置
    private double _dragTranslateX;   // 並べ替え中タブの現在の移動量

    // フロート中 (引き剝がし後) の状態
    private MainWindow? _floating;
    private TabModel? _floatingTab;
    private MainWindow? _mergeTarget;
    private System.Windows.Threading.DispatcherTimer? _dragWatchdog;

    private void TabStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = e.GetPosition(this);
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _tabDragModel = container?.DataContext as TabModel;
        _dragTranslateX = 0;
        if (container is not null)
        {
            // タブ上の掴んだ位置をフロートウィンドウの先頭タブに合わせる
            _grabInTab = e.GetPosition(container).X;
            _grabOffX = _grabInTab + container.Margin.Left;
            _grabOffY = 18;
        }
    }

    // タブ列の外へ出てもイベントを拾えるようウィンドウ全体で監視する
    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragModel is null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _tabDragModel = null;
            return;
        }

        var pos = e.GetPosition(this);
        var dx = pos.X - _tabDragStart.X;
        var dy = pos.Y - _tabDragStart.Y;

        if (_vm.Tabs.Count > 1)
        {
            if (dy > 40)
            {
                // 下へ引き出す → 別ウィンドウへ切り離し
                EndReorderVisual();
                _reordering = false;
                var tab = _tabDragModel;
                _tabDragModel = null;
                StartFloatingDrag(tab);
                return;
            }
            // 横方向にドラッグ → タブ列内で並べ替え (Chrome と同じ)
            if (_reordering || Math.Abs(dx) > SystemParameters.MinimumHorizontalDragDistance)
            {
                if (!_reordering)
                {
                    _reordering = true;
                    BeginReorderVisual();
                }
                UpdateReorder(e.GetPosition(TabStrip).X);
            }
            return;
        }

        // 単一タブ: 動かすとウィンドウごと移動
        if (Math.Abs(dx) > 6 || Math.Abs(dy) > 6)
        {
            var tab = _tabDragModel;
            _tabDragModel = null;
            StartFloatingDrag(tab);
        }
    }

    private void TabStrip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndReorderVisual();
        _tabDragModel = null;
        _reordering = false;
    }

    private ListBoxItem? TabContainer(TabModel? t)
        => t is null ? null : TabStrip.ItemContainerGenerator.ContainerFromItem(t) as ListBoxItem;

    private ListBoxItem? TabContainerAt(int i)
        => TabStrip.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;

    private void BeginReorderVisual()
    {
        _dragTranslateX = 0;
        if (TabContainer(_tabDragModel) is { } c)
            Panel.SetZIndex(c, 1000); // ドラッグ中タブを最前面に
    }

    private void EndReorderVisual()
    {
        if (TabContainer(_tabDragModel) is { } c)
        {
            c.RenderTransform = Transform.Identity;
            Panel.SetZIndex(c, 0);
        }
        _dragTranslateX = 0;
    }

    /// <summary>並べ替え: ドラッグ中タブをカーソルに張り付かせ、隣の中心を越えたら入れ替える。</summary>
    private void UpdateReorder(double cursorX)
    {
        if (_tabDragModel is null || TabContainer(_tabDragModel) is not { } c)
            return;

        Panel.SetZIndex(c, 1000);

        // 1) ドラッグ中タブをカーソルに追従させる (レイアウトを動かさず描画だけ平行移動)
        var naturalLeft = c.TranslatePoint(new Point(0, 0), TabStrip).X - _dragTranslateX;
        _dragTranslateX = (cursorX - _grabInTab) - naturalLeft;
        c.RenderTransform = new TranslateTransform(_dragTranslateX, 0);

        // 2) 隣のタブの中心を越えたらデータ順を入れ替え (周りのタブが寄る)
        var dragIndex = _vm.Tabs.IndexOf(_tabDragModel);
        if (dragIndex < 0)
            return;

        if (dragIndex < _vm.Tabs.Count - 1 && TabContainerAt(dragIndex + 1) is { } rc)
        {
            var center = rc.TranslatePoint(new Point(rc.ActualWidth / 2, 0), TabStrip).X;
            if (cursorX > center)
            {
                _vm.Tabs.Move(dragIndex, dragIndex + 1);
                return;
            }
        }
        if (dragIndex > 0 && TabContainerAt(dragIndex - 1) is { } lc)
        {
            var center = lc.TranslatePoint(new Point(lc.ActualWidth / 2, 0), TabStrip).X;
            if (cursorX < center)
                _vm.Tabs.Move(dragIndex, dragIndex - 1);
        }
    }

    /// <summary>タブのドラッグを開始する。複数タブなら新ウィンドウへ切り離し、単一タブならこのウィンドウごと動かす。</summary>
    private void StartFloatingDrag(TabModel tab)
    {
        MainWindow floating;
        if (_vm.Tabs.Count > 1)
        {
            // 複数タブ: 1 枚を新ウィンドウへ切り離す
            var wasActive = _vm.ActiveTab == tab;
            var index = _vm.Tabs.IndexOf(tab);
            _vm.Tabs.Remove(tab);
            if (wasActive && _vm.Tabs.Count > 0)
                _vm.ActiveTab = _vm.Tabs[Math.Min(index, _vm.Tabs.Count - 1)];

            var newVm = new MainViewModel(createInitialTab: false);
            newVm.Tabs.Add(tab);
            newVm.ActiveTab = tab;

            floating = new MainWindow(newVm)
            {
                Width = Width,
                Height = Height,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            floating.Show();
        }
        else
        {
            // 単一タブ: このウィンドウごとドラッグ (Chrome と同じ)
            floating = this;
        }

        // ドラッグ中のウィンドウを最前面に & 半透明にして結合先を透かす
        floating.Topmost = true;
        floating.Opacity = 0.85;
        floating.Activate();

        _floating = floating;
        _floatingTab = tab;
        _mergeTarget = null;

        // 追従はフレーム同期で滑らかに (マウスキャプチャはしない = キャプション移動等を阻害しない)。
        CompositionTarget.Rendering += DragTick;

        // 解放検出は描画に依存しない監視タイマーで確実に行う。
        // CompositionTarget.Rendering はカーソル静止時に発火が止まり、その間に離すと
        // 取りこぼして固まる → 描画に依存しない DispatcherTimer で必ず拾う (フリーズ防止の要)。
        _dragWatchdog = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(30),
        };
        _dragWatchdog.Tick += DragWatchdogTick;
        _dragWatchdog.Start();
    }

    /// <summary>フレームごとにフロートウィンドウをカーソルへ追従させる (滑らかな移動)。</summary>
    private void DragTick(object? sender, EventArgs e)
    {
        if (_floating is null)
        {
            EndFloatingDrag();
            return;
        }

        GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(_floating);
        _floating.Left = p.X / dpi.DpiScaleX - _grabOffX;
        _floating.Top = p.Y / dpi.DpiScaleY - _grabOffY;

        UpdateMergeTarget(p);
        // 結合先の上にいるときはより透かして、相手のタブ列ハイライトを見せる
        _floating.Opacity = _mergeTarget is not null ? 0.55 : 0.85;

        if (!IsLeftButtonDown())
            EndFloatingDrag();
    }

    /// <summary>描画が止まっても確実にボタン解放を検出する (フリーズ防止)。</summary>
    private void DragWatchdogTick(object? sender, EventArgs e)
    {
        if (_floating is null || !IsLeftButtonDown())
            EndFloatingDrag();
    }

    private static bool IsLeftButtonDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    private void UpdateMergeTarget(NativePoint cursor)
    {
        MainWindow? found = null;
        foreach (var w in Application.Current.Windows.OfType<MainWindow>())
        {
            if (ReferenceEquals(w, _floating) || w.WindowState == WindowState.Minimized || !w.IsVisible)
                continue;
            try
            {
                var tl = w.PointToScreen(new Point(0, 0));
                var br = w.PointToScreen(new Point(w.ActualWidth, 40)); // 上端のタブ列
                if (cursor.X >= tl.X && cursor.X <= br.X && cursor.Y >= tl.Y && cursor.Y <= br.Y)
                {
                    found = w;
                    break;
                }
            }
            catch { /* レイアウト未確定のウィンドウは無視 */ }
        }

        if (!ReferenceEquals(found, _mergeTarget))
        {
            _mergeTarget?.SetMergeHighlight(false);
            _mergeTarget = found;
            _mergeTarget?.SetMergeHighlight(true);
        }
    }

    /// <summary>ドラッグを確定する。追従と監視を止め、結合先があればタブを移して自分を閉じる。二重呼び出しは無視。</summary>
    private void EndFloatingDrag()
    {
        CompositionTarget.Rendering -= DragTick;
        if (_dragWatchdog is not null)
        {
            _dragWatchdog.Stop();
            _dragWatchdog.Tick -= DragWatchdogTick;
            _dragWatchdog = null;
        }

        var floating = _floating;
        if (floating is null)
            return;

        floating.Topmost = false;
        floating.Opacity = 1.0;

        if (_mergeTarget is not null && _floatingTab is not null)
        {
            // 別ウィンドウのタブ列にドロップ → 結合 (運んできたタブを相手へ移し、自分は閉じる)
            _mergeTarget.SetMergeHighlight(false);
            _mergeTarget.AcceptMergedTab(_floatingTab);
            floating.Close();
        }

        _floating = null;
        _floatingTab = null;
        _mergeTarget = null;
    }

    /// <summary>ドラッグ中に閉じられても、死んだウィンドウを掴み続けないよう追従/監視を後始末する。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _quickLook?.Close();
        _quickLook = null;
        _dragGhost?.Close();
        _dragGhost = null;
        StopDragWheelHook();
        if (new WindowInteropHelper(this).Handle is var hwnd && hwnd != 0)
            RemoveClipboardFormatListener(hwnd);
        CompositionTarget.Rendering -= DragTick;
        if (_dragWatchdog is not null)
        {
            _dragWatchdog.Stop();
            _dragWatchdog.Tick -= DragWatchdogTick;
            _dragWatchdog = null;
        }
        base.OnClosed(e);
    }

    /// <summary>別ウィンドウから運ばれてきたタブを受け取る (再結合)。</summary>
    public void AcceptMergedTab(TabModel tab)
    {
        _vm.Tabs.Add(tab);
        _vm.ActiveTab = tab;
        Activate();
    }

    /// <summary>ドッキング先候補としてタブ列を強調する。</summary>
    public void SetMergeHighlight(bool on)
        => CaptionBar.Background = on
            ? new SolidColorBrush(Color.FromRgb(0xBF, 0xD9, 0xF2))
            : new SolidColorBrush(Color.FromRgb(0xDD, 0xE1, 0xE6));

    // ---- 並べ替え・お気に入り ----

    private void SortDirection_Click(object sender, RoutedEventArgs e)
        => _vm.SortDescending = !_vm.SortDescending;

    private async void Favorite_Click(object sender, RoutedEventArgs e)
        => await _vm.ToggleFavoriteAsync(null);

    // ---- タブグループ ----

    /// <summary>ツールバーのグループボタン: 新規作成の入口 (管理は各グループの右クリック)。</summary>
    private void GroupsButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = GroupsButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        var createEmpty = new MenuItem { Header = "新しい空のグループ…" };
        createEmpty.Click += async (_, _) => await CreateGroupFlow(null, null);
        menu.Items.Add(createEmpty);

        var current = _vm.FavoriteTarget;
        if (current is not null)
        {
            var fromCurrent = new MenuItem { Header = "現在のフォルダで新規グループ…" };
            fromCurrent.Click += async (_, _) => await CreateGroupFlow(new[] { current }, null);
            menu.Items.Add(fromCurrent);
        }

        var roots = _vm.OpenTabRoots();
        if (roots.Count > 0)
        {
            var fromTabs = new MenuItem { Header = $"開いているタブから新規グループ… ({roots.Count})" };
            fromTabs.Click += async (_, _) => await CreateGroupFlow(roots, null);
            menu.Items.Add(fromTabs);
        }

        // 現在のフォルダを既存グループへ追加 (深い階層でホーム列が隠れていても使える)
        if (current is not null && _vm.Groups.Count > 0)
        {
            menu.Items.Add(new Separator());
            var addTo = new MenuItem { Header = "現在のフォルダをグループに追加" };
            foreach (var (group, depth) in _vm.GroupsExcept(""))
            {
                var id = group.Id;
                var mi = new MenuItem { Header = new string('　', depth) + group.Name };
                mi.Click += async (_, _) => await _vm.AddCurrentFolderToGroupAsync(id);
                addTo.Items.Add(mi);
            }
            menu.Items.Add(addTo);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "グループは左のホーム列に表示。クリックで中身を開き、右クリックで管理",
            IsEnabled = false,
        });

        menu.IsOpen = true;
    }

    /// <summary>グループ見出しを右クリック: そのグループの管理メニュー。</summary>
    private void ShowGroupContextMenu(FileSystemItem header, ListBox owner)
    {
        if (header.GroupId is not { } id)
            return;
        var menu = new ContextMenu { PlacementTarget = owner };

        var open = new MenuItem { Header = "すべてタブで開く (サブグループ含む)" };
        open.Click += async (_, _) => await _vm.OpenGroupAsync(id);
        menu.Items.Add(open);

        var openDirect = new MenuItem { Header = "直下のフォルダのみ開く" };
        openDirect.Click += async (_, _) => await _vm.OpenGroupDirectAsync(id);
        menu.Items.Add(openDirect);

        menu.Items.Add(new Separator());

        var add = new MenuItem { Header = "現在のフォルダを追加", IsEnabled = _vm.FavoriteTarget is not null };
        add.Click += async (_, _) => await _vm.AddCurrentFolderToGroupAsync(id);
        menu.Items.Add(add);

        var newSub = new MenuItem { Header = "新しいサブグループ…" };
        newSub.Click += async (_, _) => await CreateGroupFlow(null, id);
        menu.Items.Add(newSub);

        menu.Items.Add(BuildMoveIntoMenu(id));

        menu.Items.Add(new Separator());

        var rename = new MenuItem { Header = "名前を変更…" };
        rename.Click += async (_, _) => await RenameGroupFlow(id, header.Name);
        menu.Items.Add(rename);

        var delete = new MenuItem { Header = "グループを削除" };
        delete.Click += async (_, _) => await _vm.DeleteGroupAsync(id);
        menu.Items.Add(delete);

        menu.IsOpen = true;
    }

    /// <summary>「別のグループへ移動」サブメニュー (自分とその子孫は除外)。</summary>
    private MenuItem BuildMoveIntoMenu(string id)
    {
        var move = new MenuItem { Header = "別のグループへ移動" };

        var toTop = new MenuItem { Header = "(トップレベルへ)" };
        toTop.Click += async (_, _) => await _vm.MoveGroupToTopAsync(id);
        move.Items.Add(toTop);

        var any = false;
        foreach (var (group, depth) in _vm.GroupsExcept(id))
        {
            any = true;
            var targetId = group.Id;
            var mi = new MenuItem { Header = new string('　', depth) + group.Name };
            mi.Click += async (_, _) => await _vm.MoveGroupIntoAsync(id, targetId);
            move.Items.Add(mi);
        }
        if (!any)
            toTop.Header = "(移動先のグループがありません — トップレベルへ)";
        return move;
    }

    /// <summary>グループメンバー (フォルダ) を右クリック: グループから外す。</summary>
    private void ShowGroupMemberContextMenu(FileSystemItem member, ListBox owner)
    {
        if (member.GroupId is not { } id)
            return;
        var menu = new ContextMenu { PlacementTarget = owner };
        var remove = new MenuItem { Header = "グループから削除" };
        remove.Click += async (_, _) => await _vm.RemoveFromGroupAsync(id, member.Path);
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    private async Task CreateGroupFlow(IEnumerable<string>? seed, string? parentId)
    {
        var title = parentId is null ? "新しいグループ" : "新しいサブグループ";
        if (PromptText(title, "グループ名:", "") is { } name && !string.IsNullOrWhiteSpace(name))
            await _vm.CreateGroupAsync(name, seed, parentId);
    }

    private async Task RenameGroupFlow(string id, string currentName)
    {
        if (PromptText("グループ名の変更", "新しい名前:", currentName) is { } name && name != currentName)
            await _vm.RenameGroupAsync(id, name);
    }

    // ---- ナビゲーション (戻る / 進む / 上へ) ----

    private async void Back_Click(object sender, RoutedEventArgs e) => await _vm.GoBackAsync();
    private async void Forward_Click(object sender, RoutedEventArgs e) => await _vm.GoForwardAsync();
    private async void Up_Click(object sender, RoutedEventArgs e) => await _vm.GoUpAsync();

    // ---- 右クリック: ネイティブのシェルコンテキストメニュー ----

    private async void Column_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox lb || lb.DataContext is not ColumnModel column)
            return;

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as FileSystemItem;

        // タブグループの見出し・メンバーは独自の管理メニューを出す (シェルメニューではなく)
        if (item is { IsGroupEntry: true })
        {
            e.Handled = true;
            ShowGroupContextMenu(item, lb);
            return;
        }
        if (item is { IsGroupMember: true })
        {
            e.Handled = true;
            ShowGroupMemberContextMenu(item, lb);
            return;
        }

        if (item is not null)
            lb.SelectedItem = item;

        var targetPath = item?.Path ?? column.Path;
        if (targetPath is null)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        var screen = lb.PointToScreen(e.GetPosition(lb));
        e.Handled = true;

        // ピン属性での切替が効くのは cfapi 登録のクラウドのみ。Google ドライブ (DriveFS) は
        // 独自メニュー (オフライン アクセス) がシェルメニュー側に出るので独自項目は足さない
        var cloudCapable = CloudSync.IsCfapiPath(targetPath);
        var result = ShellContextMenu.Show(hwnd, targetPath, (int)screen.X, (int)screen.Y, cloudCapable);
        var refreshDir = item is not null ? System.IO.Path.GetDirectoryName(item.Path) : column.Path;

        switch (result)
        {
            case ShellMenuResult.MakeOffline:
            case ShellMenuResult.MakeOnline:
            {
                var pinned = result == ShellMenuResult.MakeOffline;
                var paths = SelectedFilePaths(lb);
                if (paths.Count == 0)
                    paths = new List<string> { targetPath };
                _vm.StatusText = pinned ? "オフラインで保持しています…" : "オンラインのみに切り替えています…";
                try
                {
                    await Task.Run(() =>
                    {
                        foreach (var p in paths)
                            CloudSync.SetOffline(p, pinned);
                    });
                    _vm.StatusText = pinned ? "常にこのデバイスに保持しました" : "オンラインのみにしました (空き容量を確保)";
                }
                catch
                {
                    _vm.StatusText = "オフライン状態を切り替えられませんでした";
                }
                if (refreshDir is not null)
                    await _vm.RefreshColumnsAsync(new[] { refreshDir });
                break;
            }
            case ShellMenuResult.ShellInvoked when refreshDir is not null:
                await _vm.RefreshColumnsAsync(new[] { refreshDir });
                break;
            case ShellMenuResult.Rename when item is not null:
                if (PromptText("名前の変更", "新しい名前:", item.Name, selectStem: true) is { } newName)
                    await _vm.RenameAsync(item, newName);
                break;
            case ShellMenuResult.AddFavorite:
                await _vm.ToggleFavoriteAsync(targetPath);
                break;
            case ShellMenuResult.CopyPath:
                try
                {
                    Clipboard.SetText(targetPath);
                    _vm.StatusText = $"パスをコピーしました: {targetPath}";
                }
                catch { /* クリップボードが他アプリに占有されている場合は無視 */ }
                break;
        }
    }

    /// <summary>1 行のテキスト入力ダイアログ。selectStem=true なら拡張子を除いて選択する (名前変更用)。</summary>
    private string? PromptText(string title, string label, string initial, bool selectStem = false)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = Brushes.White,
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        var box = new TextBox { Text = initial, Padding = new Thickness(4), FontSize = 13 };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button { Content = "OK", Width = 84, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 84, Height = 28, IsCancel = true };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(box);
        panel.Children.Add(row);
        dialog.Content = panel;

        string? result = null;
        ok.Click += (_, _) => { result = box.Text; dialog.DialogResult = true; };
        dialog.Loaded += (_, _) =>
        {
            box.Focus();
            if (selectStem)
            {
                var dot = initial.LastIndexOf('.');
                box.Select(0, dot > 0 ? dot : initial.Length); // 拡張子を除いて選択
            }
            else
            {
                box.SelectAll();
            }
        };
        return dialog.ShowDialog() == true ? result : null;
    }

    // ---- カラム操作 ----

    private async void Column_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.DataContext is not ColumnModel column)
            return;

        // 複数選択時は子の列を開かず件数表示のみ
        if (listBox.SelectedItems.Count > 1)
        {
            _vm.OnMultiSelect(column, listBox.SelectedItems.Count);
            return;
        }
        if (listBox.SelectedItem is FileSystemItem item)
            await _vm.OnItemSelectedAsync(column, item);
    }

    /// <summary>選択中の項目のうち実ファイル / フォルダだけのパス
    /// (ホームのお気に入り・ドライブ・グループ行はファイル操作の対象外)。</summary>
    private static List<string> SelectedFilePaths(ListBox listBox)
        => listBox.SelectedItems.OfType<FileSystemItem>()
            .Where(i => i is { UseRealIcon: false, IsGroupEntry: false, IsGroupMember: false })
            .Select(i => i.Path)
            .ToList();

    private async void Column_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;
        var selected = listBox.SelectedItem as FileSystemItem;
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;

        switch (e.Key)
        {
            case Key.C when ctrl:
                _vm.CopyToClipboard(SelectedFilePaths(listBox), cut: false);
                RefreshClipboardMarksAllWindows();
                e.Handled = true;
                break;

            case Key.X when ctrl:
                _vm.CopyToClipboard(SelectedFilePaths(listBox), cut: true);
                RefreshClipboardMarksAllWindows();
                e.Handled = true;
                break;

            case Key.Escape when CloseQuickLookIfOpen():
                // プレビューが開いていれば、まず閉じる
                e.Handled = true;
                break;

            case Key.Escape when !ClipboardMarks.IsEmpty:
                // コピー / 切り取りを取り消す (エクスプローラーの Esc と同じ)
                ClipboardOps.ClearAfterMove();
                ClipboardMarks.Clear();
                RefreshClipboardMarksAllWindows();
                _vm.StatusText = "コピー / 切り取りを取り消しました";
                e.Handled = true;
                break;

            case Key.Escape when _vm.ActiveTab?.Columns.Any(c => c.IsSearch) == true:
                // 検索結果の列を閉じて一覧へ戻る
                _vm.CloseSearch();
                FocusColumn((_vm.ActiveTab?.Columns.Count ?? 1) - 1, selectFirst: false);
                e.Handled = true;
                break;

            case Key.F2 when selected is { UseRealIcon: false, IsGroupEntry: false }:
                e.Handled = true;
                if (PromptText("名前の変更", "新しい名前:", selected.Name, selectStem: !selected.IsDirectory) is { } newName)
                    await _vm.RenameAsync(selected, newName);
                break;

            case Key.Delete:
            {
                var paths = SelectedFilePaths(listBox);
                if (paths.Count == 0)
                    break;
                e.Handled = true;
                var permanent = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                await _vm.DeleteAsync(paths, permanent, new WindowInteropHelper(this).Handle);
                break;
            }

            case Key.Space:
                ToggleQuickLook(selected);
                e.Handled = true;
                break;

            case Key.Enter:
                _vm.OpenItem(selected);
                e.Handled = true;
                break;

            case Key.Right:
                if (selected?.IsDirectory == true)
                {
                    FocusColumn(GetColumnIndex(listBox) + 1, selectFirst: true);
                    e.Handled = true;
                }
                break;

            case Key.Left:
            case Key.Back:
                FocusColumn(GetColumnIndex(listBox) - 1, selectFirst: false);
                e.Handled = true;
                break;
        }
    }

    private void Column_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: FileSystemItem item } && !item.IsDirectory)
            _vm.OpenItem(item);
    }

    private void Column_Loaded(object sender, RoutedEventArgs e)
        => (sender as FrameworkElement)?.BringIntoView();

    /// <summary>最終列の自動幅は項目読み込み後に確定するため、広がった分が画面外へ
    /// はみ出さないよう追従スクロールする。</summary>
    private void Column_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && sender is FrameworkElement { DataContext: ColumnModel { IsLast: true } } fe)
            fe.BringIntoView();
    }

    // ---- ホバー時のツールチップ (フォルダはサイズを遅延計算) ----

    private CancellationTokenSource? _tooltipCts;

    private async void Item_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileSystemItem item)
            return;
        _tooltipCts?.Cancel();
        _tooltipCts = new CancellationTokenSource();
        var token = _tooltipCts.Token;
        if (item.IsDirectory)
            await item.EnsureFolderSizeAsync(token);
        else
            await item.EnsureMetadataAsync(token);
    }

    private void Item_ToolTipClosing(object sender, ToolTipEventArgs e)
        => _tooltipCts?.Cancel();

    // ---- ドラッグ&ドロップ ----

    private Point _dragStart;
    private FileSystemItem? _dragCandidate;
    private FileSystemItem? _favDragCandidate;
    private FileSystemItem? _groupDragCandidate;
    private FileSystemItem? _memberDragCandidate;
    private FileSystemItem? _reclickItem;
    private bool _isDragging;
    private ListBoxItem? _dropHighlight;
    private ListBoxItem? _insertIndicator;
    private DragGhostWindow? _dragGhost;

    private enum GroupDropMode { None, Before, After, Onto }

    /// <summary>ホーム列・グループ列の統一並べ替え / 入れ子化。データ = キー (パス or "group:"+Id)。
    /// ドロップ先の列がコンテナ (ホーム or どのグループ) を決める。</summary>
    private const string HomeFormat = "ColumnView.HomeReorder";

    private void Column_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _reclickItem = null;
        _groupDragCandidate = null;
        _memberDragCandidate = null;
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as FileSystemItem;

        // グループ見出し: クリックは通常選択 (= 中身を次の列に展開)、ドラッグは並べ替え / 入れ子化
        _groupDragCandidate = item is { IsGroupEntry: true } ? item : null;
        // グループメンバー (フォルダ): クリックはナビゲーション、ドラッグはグループ内の並べ替え
        _memberDragCandidate = item is { IsGroupMember: true } ? item : null;

        // ドライブ・特殊フォルダ・お気に入り・グループはナビゲーション用なのでファイル D&D の対象外
        _dragCandidate = item is { UseRealIcon: false, IsGroupEntry: false } ? item : null;
        // お気に入りはホーム列内での並べ替え専用ドラッグ対象
        _favDragCandidate = item is { IsFavoriteEntry: true } ? item : null;

        // 既に複数選択された項目を掴んだ場合、選択を 1 つに潰さずにまとめてドラッグできるようにする
        if (sender is ListBox lb && item is not null && lb.SelectedItems.Count > 1
            && lb.SelectedItems.Contains(item)
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            _reclickItem = item;
            e.Handled = true; // ListBox による選択の単一化を抑止
        }
    }

    private void Column_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 複数選択を掴んだがドラッグしなかった = 通常クリック → その項目だけ選択
        if (_reclickItem is not null && !_isDragging && sender is ListBox lb)
        {
            lb.SelectedItems.Clear();
            lb.SelectedItem = _reclickItem;
        }
        _reclickItem = null;
        _favDragCandidate = null;
        _groupDragCandidate = null;
        _memberDragCandidate = null;
    }

    private void Column_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging || e.LeftButton != MouseButtonState.Pressed)
            return;
        if (_dragCandidate is null && _favDragCandidate is null
            && _groupDragCandidate is null && _memberDragCandidate is null)
            return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // グループ見出し / メンバー / お気に入り: すべて統一並べ替え (HomeFormat、キーで運ぶ)。
        // ドロップ先の列がコンテナ (ホーム列 or グループ列) を決める。
        if (_groupDragCandidate is { GroupId: { } gid } group)
        {
            RunReorderDrag(sender, new DataObject(HomeFormat, "group:" + gid), group, GrabOffsetFor(sender, group, e));
            return;
        }
        if (_memberDragCandidate is { } member)
        {
            RunReorderDrag(sender, new DataObject(HomeFormat, member.Path), member, GrabOffsetFor(sender, member, e));
            return;
        }
        if (_favDragCandidate is { } fav)
        {
            RunReorderDrag(sender, new DataObject(HomeFormat, fav.Path), fav, GrabOffsetFor(sender, fav, e));
            return;
        }

        if (_dragCandidate is null)
            return;

        // 掴んだ項目が複数選択に含まれていれば、選択中の全ファイルをまとめて運ぶ
        string[] paths;
        if (sender is ListBox lb && lb.SelectedItems.Count > 1 && lb.SelectedItems.Contains(_dragCandidate))
            paths = lb.SelectedItems.OfType<FileSystemItem>()
                .Where(i => !i.UseRealIcon).Select(i => i.Path).ToArray();
        else
            paths = new[] { _dragCandidate.Path };

        if (paths.Length == 0)
            return;

        var data = new DataObject(DataFormats.FileDrop, paths);
        var src = (DependencyObject)sender;
        _isDragging = true;
        DragDrop.AddGiveFeedbackHandler(src, DragSource_GiveFeedback);
        ShowDragGhost(_dragCandidate.Icon, _dragCandidate.Name, paths.Length,
                      GrabOffsetFor(sender, _dragCandidate, e));
        StartDragWheelHook();
        try
        {
            DragDrop.DoDragDrop(src, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            StopDragWheelHook();
            DragDrop.RemoveGiveFeedbackHandler(src, DragSource_GiveFeedback);
            HideDragGhost();
            _isDragging = false;
            _dragCandidate = null;
            _reclickItem = null;
        }
    }

    /// <summary>並べ替え系ドラッグ (お気に入り / グループ / メンバー) の共通処理。</summary>
    private void RunReorderDrag(object sender, DataObject data, FileSystemItem item, Point grabOffset)
    {
        var src = (DependencyObject)sender;
        _isDragging = true;
        DragDrop.AddGiveFeedbackHandler(src, DragSource_GiveFeedback);
        ShowDragGhost(item.Icon, item.Name, 1, grabOffset);
        StartDragWheelHook();
        try
        {
            DragDrop.DoDragDrop(src, data, DragDropEffects.Move);
        }
        finally
        {
            StopDragWheelHook();
            DragDrop.RemoveGiveFeedbackHandler(src, DragSource_GiveFeedback);
            HideDragGhost();
            _isDragging = false;
            _favDragCandidate = null;
            _groupDragCandidate = null;
            _memberDragCandidate = null;
            _dragCandidate = null;
            ClearInsertIndicator();
            ClearDropHighlight();
        }
    }

    // ---- ドラッグゴースト (カーソル追従のプレビュー) ----

    /// <summary>行内のどこを掴んだか (行の左上からの位置)。ゴーストをこの点がカーソル直下に
    /// 来るよう配置すると「行をそのまま持ち上げた」ように見える。</summary>
    private static Point GrabOffsetFor(object sender, FileSystemItem item, MouseEventArgs e)
    {
        if (sender is ListBox lb
            && lb.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
        {
            var p = e.GetPosition(container);
            if (p.X >= 0 && p.Y >= 0 && p.X <= container.ActualWidth && p.Y <= container.ActualHeight)
                return p;
        }
        return new Point(36, 14); // 行が特定できないときはカード左上寄りを掴んだ扱い
    }

    private void ShowDragGhost(ImageSource? icon, string name, int count, Point grabOffset)
    {
        _dragGhost ??= new DragGhostWindow { Owner = this };
        _dragGhost.SetContent(icon, name, count, grabOffset);
        _dragGhost.MoveToCursor();
        _dragGhost.Show();
        _dragGhost.MoveToCursor(); // Show 後は実寸が確定するので正確な位置に置き直す
    }

    private void HideDragGhost() => _dragGhost?.Hide();

    /// <summary>OLE ドラッグ中はマウスイベントが来ないため、GiveFeedback (連続発火する)
    /// に乗ってゴーストを追従させる。カーソル形状は既定 (移動/コピー/禁止) のまま。</summary>
    private void DragSource_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        => _dragGhost?.MoveToCursor();

    // ---- ドラッグ中のスクロール ----

    /// <summary>ドラッグ中のホイール / チルトスクロール。縦はカーソル下の列、
    /// 横 (チルト / Shift+ホイール) は列ストリップ全体を動かす。</summary>
    private void DragWheelScroll(int delta, bool tilt, Point screenPx)
    {
        // 自ウィンドウの上でだけ反応する (LL フックは画面全体のホイールを拾うため)
        Point pos;
        try { pos = PointFromScreen(screenPx); } catch { return; }
        if (pos.X < 0 || pos.Y < 0 || pos.X > ActualWidth || pos.Y > ActualHeight)
            return;

        bool shift = (GetKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
        if (tilt || shift)
        {
            // チルトは右チルト = 右へ、Shift+ホイールは上回転 = 左へ (通常時の操作感と揃える)
            double d = tilt ? delta : -delta;
            ColumnsScroll.ScrollToHorizontalOffset(ColumnsScroll.HorizontalOffset + d);
            return;
        }

        if (FindAncestor<ListBox>(InputHitTest(pos) as DependencyObject) is { } lb
            && FindDescendant<ScrollViewer>(lb) is { } sv)
        {
            double lines = -delta / 120.0 * SystemParameters.WheelScrollLines;
            // 列は項目単位スクロール (CanContentScroll) なので行数、そうでなければピクセル換算
            sv.ScrollToVerticalOffset(sv.VerticalOffset + (sv.CanContentScroll ? lines : lines * 24));
        }
        else
        {
            // 列の外 (余白など) は通常時と同じく横スクロール
            ColumnsScroll.ScrollToHorizontalOffset(ColumnsScroll.HorizontalOffset - delta);
        }
    }

    // OLE ドラッグ中はマウスが OLE 側にキャプチャされ、WM_MOUSEWHEEL が自ウィンドウに
    // 届かない。そこでドラッグの間だけ低レベルマウスフックを張ってホイールを拾う。
    // フックはドラッグ終了 (finally) で必ず外すので、通常時のコストはゼロ。

    private const int WH_MOUSE_LL = 14;
    private nint _dragMouseHook;
    private LowLevelMouseProc? _dragMouseProc; // GC に回収されないよう保持必須

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int X, Y;
        public uint mouseData, flags, time;
        public nuint dwExtraInfo;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    private void StartDragWheelHook()
    {
        if (_dragMouseHook != 0)
            return;
        _dragMouseProc = DragMouseHookProc;
        _dragMouseHook = SetWindowsHookExW(WH_MOUSE_LL, _dragMouseProc, 0, 0);
    }

    private void StopDragWheelHook()
    {
        if (_dragMouseHook != 0)
            UnhookWindowsHookEx(_dragMouseHook);
        _dragMouseHook = 0;
        _dragMouseProc = null;
    }

    private nint DragMouseHookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == WM_MOUSEWHEEL || wParam == WM_MOUSEHWHEEL))
        {
            try
            {
                var info = System.Runtime.InteropServices.Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int delta = unchecked((short)((info.mouseData >> 16) & 0xFFFF));
                DragWheelScroll(delta, wParam == WM_MOUSEHWHEEL, new Point(info.X, info.Y));
            }
            catch { /* フック内から例外を漏らさない */ }
        }
        return CallNextHookEx(_dragMouseHook, nCode, wParam, lParam);
    }

    private int _edgeScrollTick;

    /// <summary>カーソルが端に寄ったまま運んでいるとき、見切れている先へ自動スクロールする。
    /// DragOver はドラッグ中に高頻度で発火するため、時間で間引いて速度を一定に保つ。</summary>
    private void DragEdgeAutoScroll(object sender, DragEventArgs e)
    {
        const double zone = 28;    // 端からこの距離 (DIP) で発動
        const double hStep = 14;   // 横スクロールの 1 回分 (px)
        const int intervalMs = 120; // この間隔ごとに 1 ステップ (小さいほど速い)

        if (Environment.TickCount - _edgeScrollTick < intervalMs)
            return;

        bool scrolled = false;
        var p = e.GetPosition(ColumnsScroll);
        if (p.X < zone)
        {
            ColumnsScroll.ScrollToHorizontalOffset(ColumnsScroll.HorizontalOffset - hStep);
            scrolled = true;
        }
        else if (p.X > ColumnsScroll.ViewportWidth - zone)
        {
            ColumnsScroll.ScrollToHorizontalOffset(ColumnsScroll.HorizontalOffset + hStep);
            scrolled = true;
        }

        if (sender is ListBox lb && FindDescendant<ScrollViewer>(lb) is { } sv)
        {
            var q = e.GetPosition(sv);
            if (q.Y < zone)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - 1); // 項目単位: 1 = 1 行
                scrolled = true;
            }
            else if (q.Y > sv.ActualHeight - zone)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset + 1);
                scrolled = true;
            }
        }

        if (scrolled)
            _edgeScrollTick = Environment.TickCount;
    }

    private void Column_DragOver(object sender, DragEventArgs e)
    {
        StartDragWheelHook(); // 他アプリ発のドラッグでもホイールスクロールを効かせる
        DragEdgeAutoScroll(sender, e);

        if (e.Data.GetDataPresent(HomeFormat))
        {
            var key = e.Data.GetData(HomeFormat) as string ?? "";
            var container = ResolveChildDrop(sender, e, key, out var mode);
            e.Effects = mode == GroupDropMode.None ? DragDropEffects.None : DragDropEffects.Move;
            ShowGroupDropFeedback(container, mode);
            e.Handled = true;
            return;
        }

        // フォルダをグループ見出しの上にドラッグ → そのグループへ追加
        if (GroupAddTarget(e) is { } addTarget)
        {
            e.Effects = DragDropEffects.Move;
            ClearInsertIndicator();
            SetOntoHighlight(addTarget);
            e.Handled = true;
            return;
        }

        e.Effects = ResolveDropEffect(sender, e);
        UpdateDropHighlight(e);
        e.Handled = true;
    }

    /// <summary>FileDrop のフォルダがグループ見出しの上にあるなら、その見出しコンテナを返す。</summary>
    private static ListBoxItem? GroupAddTarget(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return null;
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not FileSystemItem { IsGroupEntry: true })
            return null;
        var sources = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        return sources.Any(System.IO.Directory.Exists) ? container : null;
    }

    private void Column_DragLeave(object sender, DragEventArgs e)
    {
        // 自分発のドラッグ中はフックを張ったままにする (列間の移動で DragLeave が
        // 一瞬入るたびに外すとチラつくため、終了は DoDragDrop の finally に任せる)
        if (!_isDragging)
            StopDragWheelHook();
        ClearDropHighlight();
        ClearInsertIndicator();
    }

    private async void Column_Drop(object sender, DragEventArgs e)
    {
        if (!_isDragging)
            StopDragWheelHook();
        ClearDropHighlight();
        ClearInsertIndicator();

        if (e.Data.GetDataPresent(HomeFormat))
        {
            var key = e.Data.GetData(HomeFormat) as string;
            if (key is null)
                return;
            var container = ResolveChildDrop(sender, e, key, out var mode);
            if (container?.DataContext is FileSystemItem target)
            {
                var targetKey = target.IsGroupEntry ? "group:" + target.GroupId : target.Path;
                // ドロップ先の列がコンテナ: ホーム列=null、グループ列=そのグループ Id
                var containerGroupId = (sender as ListBox)?.DataContext is ColumnModel cm ? cm.GroupId : null;
                e.Handled = true;
                if (mode == GroupDropMode.Onto && key.StartsWith("group:", StringComparison.Ordinal)
                    && target is { IsGroupEntry: true, GroupId: { } tid })
                    await _vm.MoveGroupIntoAsync(key["group:".Length..], tid);
                else if (mode is GroupDropMode.Before or GroupDropMode.After)
                    await _vm.MoveChildAsync(containerGroupId, key, targetKey, mode == GroupDropMode.After);
            }
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        // フォルダをグループ見出しにドロップ → そのグループへ追加
        if (GroupAddTarget(e) is { DataContext: FileSystemItem { GroupId: { } addId } })
        {
            var sources0 = (string[])e.Data.GetData(DataFormats.FileDrop);
            e.Handled = true;
            await _vm.AddPathsToGroupAsync(addId, sources0);
            return;
        }

        var effect = ResolveDropEffect(sender, e);
        if (effect == DragDropEffects.None)
            return;

        var targetDir = ResolveTargetDir(sender, e);
        if (targetDir is null)
            return;

        var sources = (string[])e.Data.GetData(DataFormats.FileDrop);
        e.Handled = true;
        await _vm.HandleDropAsync(sources, targetDir, copy: effect == DragDropEffects.Copy);
    }

    /// <summary>ドロップ先フォルダ: カーソル下がフォルダならそれ、なければ列のフォルダ。</summary>
    private static string? ResolveTargetDir(object sender, DragEventArgs e)
    {
        var overItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as FileSystemItem;
        if (overItem?.IsDirectory == true)
            return overItem.Path;
        return (sender as ListBox)?.DataContext is ColumnModel { Path: { } path } ? path : null;
    }

    private static DragDropEffects ResolveDropEffect(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return DragDropEffects.None;
        var targetDir = ResolveTargetDir(sender, e);
        if (targetDir is null)
            return DragDropEffects.None;

        if (e.KeyStates.HasFlag(DragDropKeyStates.ControlKey))
            return DragDropEffects.Copy;
        if (e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey))
            return DragDropEffects.Move;

        // 既定: 同じドライブ内なら移動、別ドライブならコピー (エクスプローラーと同じ)
        var sources = (string[])e.Data.GetData(DataFormats.FileDrop);
        var targetRoot = System.IO.Path.GetPathRoot(targetDir);
        var sameVolume = sources.All(s =>
            string.Equals(System.IO.Path.GetPathRoot(s), targetRoot, StringComparison.OrdinalIgnoreCase));
        return sameVolume ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    private void UpdateDropHighlight(DragEventArgs e)
    {
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var target = container?.DataContext is FileSystemItem { IsDirectory: true } ? container : null;
        if (ReferenceEquals(target, _dropHighlight))
            return;
        if (_dropHighlight is not null)
            DragDropHelper.SetDropHighlight(_dropHighlight, false);
        _dropHighlight = target;
        if (_dropHighlight is not null)
            DragDropHelper.SetDropHighlight(_dropHighlight, true);
    }

    private void ClearDropHighlight()
    {
        if (_dropHighlight is not null)
            DragDropHelper.SetDropHighlight(_dropHighlight, false);
        _dropHighlight = null;
    }

    /// <summary>統一並べ替えのドロップ位置を求める。ホーム列・グループ列の両方で動作。
    /// 対象はお気に入り / グループ見出し / メンバー。グループ→グループの中央=入れ子化。</summary>
    private static ListBoxItem? ResolveChildDrop(object sender, DragEventArgs e, string sourceKey, out GroupDropMode mode)
    {
        mode = GroupDropMode.None;
        // ホーム列 (Path==null,GroupId==null) または グループ列 (GroupId!=null)。フォルダ列(Path!=null)は対象外
        if (sender is not ListBox { DataContext: ColumnModel { Path: null } })
            return null;

        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not FileSystemItem target
            || (!target.IsFavoriteEntry && !target.IsGroupEntry && !target.IsGroupMember))
            return null; // 並べ替え対象はお気に入り / グループ / メンバーのみ

        var targetKey = target.IsGroupEntry ? "group:" + target.GroupId : target.Path;
        if (string.Equals(targetKey, sourceKey, StringComparison.Ordinal))
            return null; // 自分自身の上は無効

        var y = e.GetPosition(container).Y;
        var h = container.ActualHeight;
        // 入れ子化はグループ→グループのときだけ (中央=Onto)
        if (sourceKey.StartsWith("group:", StringComparison.Ordinal) && target.IsGroupEntry)
            mode = y < h * 0.25 ? GroupDropMode.Before : y > h * 0.75 ? GroupDropMode.After : GroupDropMode.Onto;
        else
            mode = y > h / 2 ? GroupDropMode.After : GroupDropMode.Before;
        return container;
    }

    /// <summary>ドラッグ中の視覚フィードバック: 入れ子化は枠、並べ替えは挿入線。</summary>
    private void ShowGroupDropFeedback(ListBoxItem? container, GroupDropMode mode)
    {
        if (mode == GroupDropMode.Onto)
        {
            ClearInsertIndicator();
            SetOntoHighlight(container);
        }
        else
        {
            ClearDropHighlight();
            SetInsertIndicator(container, mode == GroupDropMode.After);
        }
    }

    private void SetOntoHighlight(ListBoxItem? container)
    {
        if (ReferenceEquals(container, _dropHighlight))
            return;
        if (_dropHighlight is not null)
            DragDropHelper.SetDropHighlight(_dropHighlight, false);
        _dropHighlight = container;
        if (_dropHighlight is not null)
            DragDropHelper.SetDropHighlight(_dropHighlight, true);
    }

    private void SetInsertIndicator(ListBoxItem? container, bool after)
    {
        var side = container is null ? InsertSide.None : (after ? InsertSide.After : InsertSide.Before);
        if (ReferenceEquals(container, _insertIndicator) &&
            (_insertIndicator is null || DragDropHelper.GetDropInsert(_insertIndicator) == side))
            return;
        if (_insertIndicator is not null)
            DragDropHelper.SetDropInsert(_insertIndicator, InsertSide.None);
        _insertIndicator = container;
        if (_insertIndicator is not null)
            DragDropHelper.SetDropInsert(_insertIndicator, side);
    }

    private void ClearInsertIndicator()
    {
        if (_insertIndicator is not null)
            DragDropHelper.SetDropInsert(_insertIndicator, InsertSide.None);
        _insertIndicator = null;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    // ---- アドレスバー (パンくず / 編集) ----

    private async void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Crumb crumb)
            await _vm.NavigateCrumbAsync(crumb);
    }

    private void AddressBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // パンくずの余白をクリック → 編集モード (テキスト入力)
        if (e.OriginalSource is Border || e.OriginalSource is Grid)
            BeginAddressEdit();
    }

    private void BeginAddressEdit()
    {
        _vm.IsEditingAddress = true;
        PathBox.Text = _vm.CurrentPath;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
        }));
    }

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        if (e.Key == Key.Enter)
        {
            _vm.IsEditingAddress = false;
            await _vm.NavigateToPathAsync(box.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.IsEditingAddress = false;
            e.Handled = true;
        }
    }

    private void PathBox_LostFocus(object sender, RoutedEventArgs e)
        => _vm.IsEditingAddress = false;

    // ---- 検索 ----

    /// <summary>ツールバーがこの幅を下回ったら検索ボックスをアイコンだけに畳む
    /// (アドレスバーの表示幅を優先する)。</summary>
    private const double SearchCompactThreshold = 800;

    private bool _searchCompact;
    private bool _searchExpanded;

    private void Toolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < SearchCompactThreshold;
        if (compact == _searchCompact)
            return;
        _searchCompact = compact;
        if (!compact)
            _searchExpanded = false;
        ApplySearchVisibility();
    }

    /// <summary>検索ボックスの展開幅 (XAML の SearchBorder.Width と一致させる)。</summary>
    private const double SearchBoxWidth = 170;

    private bool ShouldShowBox => !_searchCompact || _searchExpanded || SearchBox.Text.Length > 0;

    /// <summary>リサイズ由来など、アニメーションなしで即座に表示状態を合わせる。</summary>
    private void ApplySearchVisibility()
    {
        var showBox = ShouldShowBox;
        SearchBorder.BeginAnimation(WidthProperty, null); // 実行中のアニメを解除
        SearchBorder.Width = SearchBoxWidth;
        SearchBorder.Visibility = showBox ? Visibility.Visible : Visibility.Collapsed;
        SearchToggleButton.Visibility = showBox ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>アイコン → ボックスへ横に伸びる (幅 0 → 170)。</summary>
    private void OpenSearchAnimated()
    {
        SearchToggleButton.Visibility = Visibility.Collapsed;
        SearchBorder.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(0, SearchBoxWidth, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            // アニメを解除して通常のレイアウト幅に戻す
            SearchBorder.BeginAnimation(WidthProperty, null);
            SearchBorder.Width = SearchBoxWidth;
        };
        SearchBorder.BeginAnimation(WidthProperty, anim);
    }

    /// <summary>ボックス → アイコンへ横に縮む (幅 170 → 0)。</summary>
    private void CloseSearchAnimated()
    {
        var anim = new DoubleAnimation(SearchBorder.ActualWidth, 0, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) =>
        {
            SearchBorder.BeginAnimation(WidthProperty, null);
            SearchBorder.Width = SearchBoxWidth;
            SearchBorder.Visibility = Visibility.Collapsed;
            SearchToggleButton.Visibility = Visibility.Visible;
        };
        SearchBorder.BeginAnimation(WidthProperty, anim);
    }

    private void ExpandSearchBox()
    {
        var wasHidden = _searchCompact && SearchBorder.Visibility != Visibility.Visible;
        _searchExpanded = true;
        if (wasHidden)
            OpenSearchAnimated();
        else
            ApplySearchVisibility();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>ボックスをアイコンへ畳む (畳むべき状態でなければ何もしない)。</summary>
    private void CollapseSearchBox()
    {
        _searchExpanded = false;
        if (_searchCompact && !ShouldShowBox && SearchBorder.Visibility == Visibility.Visible)
            CloseSearchAnimated();
        else
            ApplySearchVisibility();
    }

    private void SearchToggle_Click(object sender, RoutedEventArgs e) => ExpandSearchBox();

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        _vm.CancelSearch();
        SearchBox.Focus();
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 空のままフォーカスが外れたらアイコンに戻す (入力が残っていれば出したまま)
        if (SearchBox.Text.Length == 0)
            CollapseSearchBox();
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _vm.SearchAsync(SearchBox.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (SearchBox.Text.Length > 0)
            {
                // 1 回目の Esc: 入力を消して実行中の検索を止める (結果列は残す)
                SearchBox.Clear();
                _vm.CancelSearch();
            }
            else
            {
                // 2 回目の Esc: 結果列を閉じて一覧へ戻る
                _vm.CloseSearch();
                FocusColumn((_vm.ActiveTab?.Columns.Count ?? 1) - 1, selectFirst: false);
            }
        }
    }

    // ---- ウィンドウ全体のショートカット ----

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // プレビューが開いている間はフォーカスがどこにあっても Space / Esc で閉じる。
        // 他アプリから戻った直後は列 (ListBox) にフォーカスが無いことがあり、
        // 列のハンドラーだけに頼ると「キーが効かない」ように見えるため
        if (Keyboard.Modifiers == ModifierKeys.None
            && e.Key is Key.Space or Key.Escape
            && Keyboard.FocusedElement is not TextBox
            && _quickLook is { IsVisible: true })
        {
            _quickLook.CloseQuickLook();
            // 列にフォーカスを戻し、続けて Space で再プレビュー / ↑↓ 移動ができるようにする
            FocusSelectionColumn();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            switch (e.SystemKey)
            {
                case Key.Left:
                    await _vm.GoBackAsync();
                    e.Handled = true;
                    break;
                case Key.Right:
                    await _vm.GoForwardAsync();
                    e.Handled = true;
                    break;
                case Key.Up:
                    await _vm.GoUpAsync();
                    e.Handled = true;
                    break;
            }
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (e.Key)
            {
                case Key.Tab:
                    CycleTab(-1);
                    e.Handled = true;
                    break;
                case Key.N:
                    e.Handled = true;
                    await NewFolderFlow();
                    break;
                case Key.Z:
                    if (Keyboard.FocusedElement is not TextBox)
                    {
                        e.Handled = true;
                        await _vm.RedoAsync();
                    }
                    break;
            }
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        switch (e.Key)
        {
            case Key.Tab:
                CycleTab(1);
                e.Handled = true;
                break;
            case Key.T:
                await _vm.NewTabAsync(null);
                e.Handled = true;
                break;
            case Key.W:
                if (_vm.ActiveTab is { } tab)
                    _vm.CloseTab(tab);
                e.Handled = true;
                break;
            case Key.H:
                _vm.ShowHidden = !_vm.ShowHidden;
                e.Handled = true;
                break;
            case Key.L:
                BeginAddressEdit();
                e.Handled = true;
                break;
            case Key.F:
                ExpandSearchBox();
                e.Handled = true;
                break;
            case Key.V:
                // 現在のフォルダへ貼り付け。パス入力欄などのテキスト編集中は
                // ネイティブの貼り付けを妨げない
                if (Keyboard.FocusedElement is not TextBox && _vm.FavoriteTarget is { } dir)
                {
                    e.Handled = true;
                    await _vm.PasteAsync(dir);
                    RefreshClipboardMarksAllWindows();
                }
                break;
            case Key.Z:
                // 直前のファイル操作を取り消す (テキスト編集中はネイティブの Undo を妨げない)
                if (Keyboard.FocusedElement is not TextBox)
                {
                    e.Handled = true;
                    await _vm.UndoAsync();
                }
                break;
            case Key.Y:
                // やり直し (Ctrl+Shift+Z と同じ)
                if (Keyboard.FocusedElement is not TextBox)
                {
                    e.Handled = true;
                    await _vm.RedoAsync();
                }
                break;
        }
    }

    /// <summary>Ctrl+Shift+N: 現在のフォルダに新しいフォルダを作る。</summary>
    private async Task NewFolderFlow()
    {
        if (_vm.SuggestNewFolderName() is not { } suggested)
            return;
        if (PromptText("新しいフォルダ", "フォルダ名:", suggested) is { } name)
            await _vm.CreateFolderAsync(name);
    }

    /// <summary>最後のウィンドウを閉じるとき、タブ構成を保存する (次回起動時に復元)。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Application.Current.Windows.OfType<MainWindow>().Count() == 1)
            _vm.SaveSession();
        base.OnClosing(e);
    }

    // ---- 補助 ----

    private int GetColumnIndex(ListBox listBox)
        => listBox.DataContext is ColumnModel column && _vm.ActiveTab is { } tab
            ? tab.Columns.IndexOf(column)
            : -1;

    /// <summary>選択中の項目がある列 (無ければ最後の列) にキーボードフォーカスを戻す。
    /// プレビューを閉じた直後や他アプリから戻った直後に呼び、Space / ↑↓ を
    /// クリック無しで再び効くようにする。</summary>
    public void FocusSelectionColumn()
    {
        if (_vm.ActiveTab is not { } tab || tab.Columns.Count == 0)
            return;
        int index = tab.Columns.Count - 1;
        for (int i = tab.Columns.Count - 1; i >= 0; i--)
        {
            if (tab.Columns[i].SelectedItem is not null)
            {
                index = i;
                break;
            }
        }
        FocusColumn(index, selectFirst: false);
    }

    private void FocusColumn(int index, bool selectFirst)
    {
        if (_vm.ActiveTab is not { } tab || index < 0 || index >= tab.Columns.Count)
            return;

        var listBoxes = new List<ListBox>();
        CollectDescendants(ColumnsHost, listBoxes);
        if (index >= listBoxes.Count)
            return;

        var target = listBoxes[index];
        if (target.SelectedIndex < 0 && selectFirst && target.Items.Count > 0)
            target.SelectedIndex = 0;

        target.Focus();
        if (target.SelectedItem is not null
            && target.ItemContainerGenerator.ContainerFromItem(target.SelectedItem) is ListBoxItem container)
        {
            container.Focus();
        }
    }

    private static void CollectDescendants<T>(DependencyObject root, List<T> results) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                results.Add(match);
            CollectDescendants(child, results);
        }
    }
}
