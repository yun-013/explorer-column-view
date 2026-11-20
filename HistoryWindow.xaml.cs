using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ColumnView;

/// <summary>
/// 操作履歴パネル (アプリに 1 つ)。ファイル操作を新しい順に一覧し、
/// 「ここまで戻す / ここまでやり直す」でその手までまとめて取り消し / やり直しできる。
/// 各手を開くと、1 項目ずつ元の場所・移動先と「今そこにあるか」を確かめられる。
/// </summary>
public partial class HistoryWindow : Window
{
    private static HistoryWindow? _instance;

    /// <summary>詳細に並べる項目の上限 (大量の移動でもパネルが重くならないように)。</summary>
    private const int MaxRows = 200;

    private bool _busy;

    private HistoryWindow()
    {
        InitializeComponent();
        EntryList.ItemsSource = FileHistory.Entries;
        FileHistory.Changed += OnHistoryChanged;
        Closed += (_, _) =>
        {
            FileHistory.Changed -= OnHistoryChanged;
            foreach (var e in FileHistory.Entries)
                e.InRange = false;
            _instance = null;
        };
        UpdateState();
    }

    /// <summary>パネルを開く (開いていれば前に出す)。focus を渡すとその手を開いて見える位置へ送る。</summary>
    public static void ShowFor(MainWindow owner, HistoryEntry? focus)
    {
        _instance ??= new HistoryWindow();
        var w = _instance;
        // 操作は呼び出したウィンドウ (の列) に対して行う
        if (!ReferenceEquals(w.Owner, owner))
            w.Owner = owner;
        if (!w.IsVisible)
            w.Show();
        if (w.WindowState == WindowState.Minimized)
            w.WindowState = WindowState.Normal;
        w.Activate();

        if (focus is not null && FileHistory.Entries.Contains(focus))
        {
            focus.IsExpanded = true;
            w.BuildRows(focus);
            w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (w.EntryList.ItemContainerGenerator.ContainerFromItem(focus) is FrameworkElement fe)
                    fe.BringIntoView();
            });
        }
    }

    private MainViewModel? ViewModel
        => (Owner as MainWindow)?.ViewModel
           ?? Application.Current.Windows.OfType<MainWindow>().FirstOrDefault()?.ViewModel;

    private void OnHistoryChanged()
    {
        UpdateState();
        // 状態が変わった手は、開いている詳細を作り直す (今ある場所が入れ替わるため)
        foreach (var e in FileHistory.Entries.Where(e => e.IsExpanded))
            BuildRows(e);
    }

    private void UpdateState()
    {
        EmptyText.Visibility = FileHistory.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = !_busy && FileHistory.LatestDone is not null;
        RedoButton.IsEnabled = !_busy && FileHistory.OldestUndone is not null;
        EntryList.IsEnabled = !_busy;
    }

    // ---- 取り消し / やり直し ----

    private async Task RunAsync(Func<MainViewModel, Task> action)
    {
        if (_busy || ViewModel is not { } vm)
            return;
        _busy = true;
        UpdateState();
        ResultText.Text = "実行しています…";
        try
        {
            await action(vm);
            ResultText.Text = vm.StatusText;
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    private async void Step_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HistoryEntry entry })
            return;
        ClearRange();
        await RunAsync(vm => entry.IsDone ? vm.UndoToAsync(entry) : vm.RedoToAsync(entry));
    }

    private async void Undo_Click(object sender, RoutedEventArgs e) => await RunAsync(vm => vm.UndoAsync());

    private async void Redo_Click(object sender, RoutedEventArgs e) => await RunAsync(vm => vm.RedoAsync());

    /// <summary>「ここまで戻す」にマウスを載せたら、巻き込まれる手をすべて色付けして見せる。</summary>
    private void Step_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Button { Tag: HistoryEntry entry } button)
            return;
        var chain = entry.IsDone ? FileHistory.UndoChain(entry) : FileHistory.RedoChain(entry);
        foreach (var c in chain)
            c.InRange = true;
        button.ToolTip = chain.Count <= 1
            ? (entry.IsDone ? "この操作を取り消します" : "この操作をやり直します")
            : (entry.IsDone
                ? $"色の付いた {chain.Count} 件の操作を、新しい順にまとめて取り消します"
                : $"色の付いた {chain.Count} 件の操作を、古い順にまとめてやり直します");
    }

    private void Step_MouseLeave(object sender, MouseEventArgs e) => ClearRange();

    private static void ClearRange()
    {
        foreach (var e in FileHistory.Entries)
            e.InRange = false;
    }

    // ---- 詳細 (1 項目ずつ) ----

    /// <summary>詳細を開いたとき (クリック・キーボード・支援技術のどれでも) に中身を作る。</summary>
    private void Expand_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryEntry entry })
            BuildRows(entry);
    }

    private void BuildRows(HistoryEntry entry)
    {
        var items = entry.Items;
        var rows = new List<object>();
        foreach (var m in items.Take(MaxRows))
        {
            var row = HistoryItemRow.Create(entry, m);
            rows.Add(row);
            _ = row.CheckAsync();
        }
        if (items.Count > MaxRows)
            rows.Add(HistoryItemRow.More(items.Count - MaxRows));
        if (items.Count == 0)
            rows.Add(HistoryItemRow.More(0));
        entry.Rows = rows;
    }

    private async void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HistoryItemRow { CheckPath: { } path } }
            || Owner is not MainWindow owner)
            return;
        await owner.ViewModel.RevealPathAsync(path);
        owner.Activate();
    }

    // ---- キー ----

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && e.Key == Key.Z)
        {
            e.Handled = true;
            await RunAsync(vm => vm.UndoAsync());
        }
        else if ((mods == ModifierKeys.Control && e.Key == Key.Y)
                 || (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z))
        {
            e.Handled = true;
            await RunAsync(vm => vm.RedoAsync());
        }
    }
}

/// <summary>履歴の詳細の 1 行 (1 項目の元の場所・移動先と、今そこにあるか)。</summary>
public sealed class HistoryItemRow : ObservableObject
{
    public string Name { get; private init; } = "";
    public string FromText { get; private init; } = "";
    public string ToText { get; private init; } = "";

    /// <summary>今あるはずの場所 (ごみ箱の中なら null)。</summary>
    public string? CheckPath { get; private init; }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    private string _statusGlyph = "";
    public string StatusGlyph
    {
        get => _statusGlyph;
        private set => Set(ref _statusGlyph, value);
    }

    private Brush? _statusBrush;
    public Brush? StatusBrush
    {
        get => _statusBrush;
        private set => Set(ref _statusBrush, value);
    }

    private bool _canReveal;
    public bool CanReveal
    {
        get => _canReveal;
        private set => Set(ref _canReveal, value);
    }

    private static Brush Res(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>ツールチップ用のフルパス。</summary>
    public string FromFull { get; private init; } = "";
    public string ToFull { get; private init; } = "";

    private static string Show(string? path, bool isFrom)
    {
        if (path is null)
            return isFrom ? "(新しく作成)" : "ごみ箱";
        var compact = Compact(path);
        return path.Contains(@"\.ColumnViewTrash\", StringComparison.OrdinalIgnoreCase)
            ? "退避ごみ箱 (" + compact + ")"
            : compact;
    }

    /// <summary>長いパスは先頭側を省き、効いてくる末尾 (親フォルダ 2 階層 + 名前) を残す。
    /// 末尾を「…」で切ると、肝心のフォルダ名が見えなくなるため。</summary>
    private static string Compact(string path)
    {
        var parts = UndoOps.Trim(path).Split(Path.DirectorySeparatorChar);
        return parts.Length <= 4 ? path : "…\\" + string.Join("\\", parts[^3..]);
    }

    public static HistoryItemRow Create(HistoryEntry entry, HistoryMove m)
    {
        // 実行済みなら操作の後の場所、取り消し済みなら操作の前の場所に今あるはず
        var check = entry.IsDone ? m.To : m.From;
        var row = new HistoryItemRow
        {
            Name = Path.GetFileName(UndoOps.Trim(m.To ?? m.From ?? "")),
            FromText = Show(m.From, isFrom: true),
            ToText = Show(m.To, isFrom: false),
            FromFull = m.From ?? "",
            ToFull = m.To ?? "",
            CheckPath = check,
        };
        if (check is null)
        {
            row.StatusGlyph = "";
            row.StatusText = "ごみ箱の中にあるはずです";
            row.StatusBrush = Res("TextSecondaryBrush");
        }
        else
        {
            row.StatusText = "確認しています…";
            row.StatusBrush = Res("TextTertiaryBrush");
        }
        return row;
    }

    public static HistoryItemRow More(int count) => new()
    {
        Name = count > 0 ? $"ほか {count} 項目" : "(項目の記録がありません)",
    };

    /// <summary>今あるはずの場所に実際にあるかを裏で確かめる。届かない共有では待たない。</summary>
    public async Task CheckAsync()
    {
        if (CheckPath is not { } path)
            return;
        if (Reachability.IsNetworkPath(path) && !await Reachability.IsReachableAsync(path))
        {
            Apply("", "接続先に届かないため確認できません", "TextSecondaryBrush", reveal: false);
            return;
        }
        var exists = await Task.Run(() => UndoOps.Exists(path));
        if (exists)
            Apply("", "今ここにあります", "DropCopyAccentBrush", reveal: true);
        else
            Apply("", "ここに見当たりません (アプリの外で移動・削除された可能性があります)", "AccentBrush", reveal: false);
    }

    private void Apply(string glyph, string text, string brushKey, bool reveal)
    {
        StatusGlyph = glyph;
        StatusText = text;
        StatusBrush = Res(brushKey);
        CanReveal = reveal;
    }
}
