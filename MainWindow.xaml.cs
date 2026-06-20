using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ColumnView;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow() : this(new MainViewModel()) { }

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
        _vm.TabsEmptied += OnTabsEmptied;
    }

    /// <summary>タブが 0 個になったとき: 他にウィンドウがあれば閉じ、無ければ新規タブを開く。</summary>
    private void OnTabsEmptied()
    {
        if (Application.Current.Windows.OfType<MainWindow>().Count() > 1)
            Close();
        else
            _ = _vm.NewTabAsync(null);
    }

    // ---- ウィンドウ操作 (カスタムタイトルバー) ----

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // 最大化時はリサイズ枠ぶんはみ出すのでパディングで内側に収める
        RootDock.Margin = maximized ? new Thickness(7) : new Thickness(0);
        RootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
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

        // フレーム同期でカーソルを追跡 (キャプチャ不要・最も滑らか)
        CompositionTarget.Rendering += DragTick;
    }

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

        // 左ボタンが離されたら確定
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
            EndFloatingDrag();
    }

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

    private void EndFloatingDrag()
    {
        CompositionTarget.Rendering -= DragTick;

        var floating = _floating;
        if (floating is not null)
        {
            floating.Topmost = false;
            floating.Opacity = 1.0;
        }

        if (_mergeTarget is not null && _floatingTab is not null && floating is not null)
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
        if (item is not null)
            lb.SelectedItem = item;

        var targetPath = item?.Path ?? column.Path;
        if (targetPath is null)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        var screen = lb.PointToScreen(e.GetPosition(lb));
        e.Handled = true;

        var result = ShellContextMenu.Show(hwnd, targetPath, (int)screen.X, (int)screen.Y);
        var refreshDir = item is not null ? System.IO.Path.GetDirectoryName(item.Path) : column.Path;

        switch (result)
        {
            case ShellMenuResult.ShellInvoked when refreshDir is not null:
                await _vm.RefreshColumnsAsync(new[] { refreshDir });
                break;
            case ShellMenuResult.Rename when item is not null:
                if (PromptRename(item.Name) is { } newName)
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

    private string? PromptRename(string current)
    {
        var dialog = new Window
        {
            Title = "名前の変更",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = Brushes.White,
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        var box = new TextBox { Text = current, Padding = new Thickness(4), FontSize = 13 };
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
        panel.Children.Add(new TextBlock { Text = "新しい名前:", Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(box);
        panel.Children.Add(row);
        dialog.Content = panel;

        string? result = null;
        ok.Click += (_, _) => { result = box.Text; dialog.DialogResult = true; };
        dialog.Loaded += (_, _) =>
        {
            box.Focus();
            var dot = current.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : current.Length); // 拡張子を除いて選択
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

    private void Column_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;
        var selected = listBox.SelectedItem as FileSystemItem;

        switch (e.Key)
        {
            case Key.Space:
                _vm.TogglePreview(selected);
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

    // ---- ホバー時のツールチップ (フォルダーはサイズを遅延計算) ----

    private CancellationTokenSource? _tooltipCts;

    private async void Item_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileSystemItem { IsDirectory: true } item)
            return;
        _tooltipCts?.Cancel();
        _tooltipCts = new CancellationTokenSource();
        await item.EnsureFolderSizeAsync(_tooltipCts.Token);
    }

    private void Item_ToolTipClosing(object sender, ToolTipEventArgs e)
        => _tooltipCts?.Cancel();

    // ---- ドラッグ&ドロップ ----

    private Point _dragStart;
    private FileSystemItem? _dragCandidate;
    private FileSystemItem? _favDragCandidate;
    private FileSystemItem? _reclickItem;
    private bool _isDragging;
    private ListBoxItem? _dropHighlight;
    private ListBoxItem? _insertIndicator;

    /// <summary>ホーム列のお気に入りを並べ替えるときのドラッグデータ形式。</summary>
    private const string FavoriteFormat = "ColumnView.FavoriteReorder";

    private void Column_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _reclickItem = null;
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as FileSystemItem;
        // ドライブ・特殊フォルダー・お気に入りはナビゲーション用なのでファイル D&D の対象外
        _dragCandidate = item is { UseRealIcon: false } ? item : null;
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
    }

    private void Column_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging || e.LeftButton != MouseButtonState.Pressed)
            return;
        if (_dragCandidate is null && _favDragCandidate is null)
            return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // お気に入りはホーム列内での並べ替え (パスを運んで挿入位置で入れ替え)
        if (_favDragCandidate is not null)
        {
            var favData = new DataObject(FavoriteFormat, _favDragCandidate.Path);
            _isDragging = true;
            try
            {
                DragDrop.DoDragDrop((DependencyObject)sender, favData, DragDropEffects.Move);
            }
            finally
            {
                _isDragging = false;
                _favDragCandidate = null;
                _dragCandidate = null;
                ClearInsertIndicator();
            }
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
        _isDragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            _isDragging = false;
            _dragCandidate = null;
            _reclickItem = null;
        }
    }

    private void Column_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(FavoriteFormat))
        {
            var source = e.Data.GetData(FavoriteFormat) as string ?? "";
            var container = ResolveFavoriteDrop(sender, e, source, out var after);
            e.Effects = container is not null ? DragDropEffects.Move : DragDropEffects.None;
            SetInsertIndicator(container, after);
            e.Handled = true;
            return;
        }

        e.Effects = ResolveDropEffect(sender, e);
        UpdateDropHighlight(e);
        e.Handled = true;
    }

    private void Column_DragLeave(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        ClearInsertIndicator();
    }

    private async void Column_Drop(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        ClearInsertIndicator();

        if (e.Data.GetDataPresent(FavoriteFormat))
        {
            var source = e.Data.GetData(FavoriteFormat) as string;
            if (source is null)
                return;
            var container = ResolveFavoriteDrop(sender, e, source, out var after);
            if (container?.DataContext is FileSystemItem target)
            {
                e.Handled = true;
                await _vm.MoveFavoriteAsync(source, target.Path, after);
            }
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

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

    /// <summary>ドロップ先フォルダー: カーソル下がフォルダーならそれ、なければ列のフォルダー。</summary>
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

    /// <summary>お気に入りの並べ替えで、カーソル下のお気に入り項目とその前後どちらに差し込むかを求める。</summary>
    private static ListBoxItem? ResolveFavoriteDrop(object sender, DragEventArgs e, string source, out bool after)
    {
        after = false;
        // 並べ替えはホーム列 (Path == null) 内でのみ許可する
        if (sender is not ListBox { DataContext: ColumnModel { Path: null } })
            return null;

        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not FileSystemItem { IsFavoriteEntry: true } item)
            return null;
        // 自分自身の上にいるときは無効 (移動しても変化しないため)
        if (string.Equals(item.Path, source, StringComparison.OrdinalIgnoreCase))
            return null;

        after = e.GetPosition(container).Y > container.ActualHeight / 2;
        return container;
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

    // ---- ウィンドウ全体のショートカット ----

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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

        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        switch (e.Key)
        {
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
        }
    }

    // ---- 補助 ----

    private int GetColumnIndex(ListBox listBox)
        => listBox.DataContext is ColumnModel column && _vm.ActiveTab is { } tab
            ? tab.Columns.IndexOf(column)
            : -1;

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
