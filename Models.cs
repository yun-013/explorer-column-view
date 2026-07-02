using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ColumnView;

/// <summary>全要素の入れ替えを変更通知 1 回 (Reset) で行える ObservableCollection。
/// 大量項目を 1 件ずつ Add すると通知数ぶんレイアウト評価が走り重いため。</summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }
}

public enum CloudStatus
{
    None,       // クラウド管理外
    CloudOnly,  // オンラインのみ (実体はローカルにない)
    Local,      // ローカルにあり (空き容量確保で解放され得る)
    Pinned,     // 常にこのデバイスに保持
}

public enum SortKey
{
    Name,
    Modified,
    Size,
    Type,
}

// Segoe MDL2 Assets のグリフ。ソースを ASCII に保つため ConvertFromUtf32 で定義する
public static class Glyphs
{
    public static readonly string Cloud = char.ConvertFromUtf32(0xE753);
    public static readonly string Check = char.ConvertFromUtf32(0xE73E);
    public static readonly string Pin = char.ConvertFromUtf32(0xE840);
    public static readonly string Star = char.ConvertFromUtf32(0xE735);
    public static readonly string Cut = char.ConvertFromUtf32(0xE8C6);
    public static readonly string Copy = char.ConvertFromUtf32(0xE8C8);
}

/// <summary>クリップボードに載っている項目の視覚状態。</summary>
public enum ClipboardMarkKind
{
    None,
    Copied, // コピー済み (バッジ表示)
    Cut,    // 切り取り済み (半透明 + バッジ)
}

public record Crumb(string Display, string Path, bool IsFirst);

/// <summary>列の行テンプレートを切り替える: グループ見出しは専用、それ以外は通常のファイル行。</summary>
public class ColumnItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NormalTemplate { get; set; }
    public DataTemplate? GroupTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is FileSystemItem { IsGroupEntry: true } ? GroupTemplate : NormalTemplate;
}

public class FileSystemItem : ObservableObject
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public CloudStatus Cloud { get; init; }
    public DateTime Modified { get; init; }
    public long Size { get; init; }

    /// <summary>ホーム列のドライブ・特殊フォルダなど、実パスのアイコンを使う項目</summary>
    public bool UseRealIcon { get; init; }

    /// <summary>ホーム列のお気に入り項目 (★バッジ表示)</summary>
    public bool IsFavoriteEntry { get; init; }

    // ---- タブグループ ----

    /// <summary>グループ見出し行。クリックで中身を次の列に展開する。</summary>
    public bool IsGroupEntry { get; init; }

    /// <summary>グループ内のフォルダー (メンバー) 行。グループ列に並ぶ。</summary>
    public bool IsGroupMember { get; init; }

    /// <summary>グループ見出しなら自身の Id、メンバーなら所属グループの Id。</summary>
    public string? GroupId { get; init; }

    /// <summary>グループ見出しの直下の項目数 (サブグループ＋フォルダー)。</summary>
    public int MemberCount { get; init; }

    private ImageSource? _resolvedIcon;
    public ImageSource? Icon => _resolvedIcon ?? GenericIcon;

    private ImageSource? GenericIcon => UseRealIcon
        ? IconCache.GetByPath(Path)
        : IconCache.GetByExtension(IsDirectory ? null : System.IO.Path.GetExtension(Name), IsDirectory);

    /// <summary>実ファイルのアイコン (.exe の埋め込みアイコン等) を後から差し替える。</summary>
    public void ApplyRealIcon(ImageSource icon)
    {
        _resolvedIcon = icon;
        Raise(nameof(Icon));
    }

    /// <summary>
    /// 実ファイル固有のアイコンを持つ拡張子だけを実アイコン読み込みの対象にする。
    /// .jpg や文書ファイル等は実アイコン＝拡張子アイコンなので、ファイルへ触れる
    /// SHGetFileInfo を 1 件ずつ呼ぶのは無駄打ち＆もたつきの原因 → 拡張子アイコンで済ます。
    /// </summary>
    private static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".scr", ".cpl", ".msc", ".appref-ms",
    };

    /// <summary>実アイコンを読み込むべきか (クラウド専用ファイルはダウンロード回避のため除外)。</summary>
    public bool WantsRealIcon => !UseRealIcon && !IsDirectory && Cloud != CloudStatus.CloudOnly
        && PerFileIconExtensions.Contains(System.IO.Path.GetExtension(Name));

    /// <summary>コピー / 切り取りの視覚状態。クリップボードが変わったら戻す。
    /// マーク中はクラウドバッジより優先して表示する (一時的な状態なので)。</summary>
    private ClipboardMarkKind _clipMark;
    public ClipboardMarkKind ClipMark
    {
        get => _clipMark;
        set
        {
            if (_clipMark == value)
                return;
            _clipMark = value;
            Raise();
            Raise(nameof(Badge));
            Raise(nameof(BadgeBrush));
            Raise(nameof(BadgeTooltip));
        }
    }

    public string Badge => ClipMark switch
    {
        ClipboardMarkKind.Cut => Glyphs.Cut,
        ClipboardMarkKind.Copied => Glyphs.Copy,
        _ => IsFavoriteEntry ? Glyphs.Star : Cloud switch
        {
            CloudStatus.CloudOnly => Glyphs.Cloud,
            CloudStatus.Local => Glyphs.Check,
            CloudStatus.Pinned => Glyphs.Pin,
            _ => "",
        },
    };

    public Brush BadgeBrush => ClipMark switch
    {
        ClipboardMarkKind.Cut => Brushes.Gray,
        ClipboardMarkKind.Copied => Brushes.SteelBlue,
        _ => IsFavoriteEntry ? Brushes.Goldenrod : Cloud switch
        {
            CloudStatus.CloudOnly => Brushes.SteelBlue,
            CloudStatus.Local => Brushes.SeaGreen,
            CloudStatus.Pinned => Brushes.SeaGreen,
            _ => Brushes.Transparent,
        },
    };

    public string BadgeTooltip => ClipMark switch
    {
        ClipboardMarkKind.Cut => "切り取り済み (貼り付けで移動)",
        ClipboardMarkKind.Copied => "コピー済み",
        _ => IsFavoriteEntry ? "お気に入り" : Cloud switch
        {
            CloudStatus.CloudOnly => "オンラインのみ (開くとダウンロード)",
            CloudStatus.Local => "このデバイスで使用可能",
            CloudStatus.Pinned => "常にこのデバイスに保持",
            _ => "",
        },
    };

    public Visibility ChevronVisibility => IsDirectory ? Visibility.Visible : Visibility.Collapsed;

    // ---- ホバー時のツールチップ情報 ----

    /// <summary>「種類」(ファイルのみ表示)。フォルダーは行ごと非表示。</summary>
    public string TypeName => IsDirectory
        ? "ファイル フォルダー"
        : IconCache.GetTypeName(System.IO.Path.GetExtension(Name));

    /// <summary>種類の行はファイルのときだけ見せる。</summary>
    public Visibility TypeRowVisibility => IsDirectory ? Visibility.Collapsed : Visibility.Visible;

    public string ModifiedText => Modified.ToString("yyyy/MM/dd HH:mm");

    private string _folderSizeText = "計算中…";
    private bool _folderSizeComputed;

    /// <summary>サイズ表示。ファイルは即時、フォルダーはホバー時に再帰計算した値。</summary>
    public string SizeText => IsDirectory ? _folderSizeText : FormatSize(Size);

    /// <summary>フォルダーの合計サイズをバックグラウンドで集計する (ホバー時に一度だけ)。</summary>
    public async Task EnsureFolderSizeAsync(CancellationToken ct)
    {
        if (!IsDirectory || _folderSizeComputed)
            return;
        _folderSizeComputed = true;
        try
        {
            var bytes = await Task.Run(() => DirectorySize(Path, ct), ct);
            _folderSizeText = FormatSize(bytes);
        }
        catch (OperationCanceledException)
        {
            _folderSizeComputed = false; // 次回ホバーで再計算
            return;
        }
        catch
        {
            _folderSizeText = "—";
        }
        Raise(nameof(SizeText));
    }

    /// <summary>配下のファイルサイズを合計する。メタデータの長さのみ参照しクラウド実体は取得しない。</summary>
    private static long DirectorySize(string path, CancellationToken ct)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            DirectoryInfo dir;
            try
            {
                dir = new DirectoryInfo(stack.Pop());
                foreach (var file in dir.EnumerateFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    total += file.Length;
                }
                foreach (var sub in dir.EnumerateDirectories())
                {
                    // ジャンクション/シンボリックリンクは無限ループ回避のため辿らない
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0)
                        stack.Push(sub.FullName);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // アクセスできない配下は無視して合計を続行
            }
        }
        return total;
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    private const int FILE_ATTRIBUTE_OFFLINE = 0x1000;
    private const int FILE_ATTRIBUTE_PINNED = 0x80000;
    private const int FILE_ATTRIBUTE_UNPINNED = 0x100000;
    private const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    private const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    public static CloudStatus GetCloudStatus(FileAttributes attributes)
    {
        var a = (int)attributes;
        if ((a & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_OFFLINE)) != 0)
            return CloudStatus.CloudOnly;
        if ((a & FILE_ATTRIBUTE_PINNED) != 0)
            return CloudStatus.Pinned;
        if ((a & FILE_ATTRIBUTE_UNPINNED) != 0)
            return CloudStatus.Local;
        return CloudStatus.None;
    }

    public static Comparison<FileSystemItem> BuildComparison(SortKey key, bool descending)
    {
        Comparison<FileSystemItem> cmp = key switch
        {
            SortKey.Modified => (a, b) => a.Modified.CompareTo(b.Modified),
            SortKey.Size => (a, b) => a.Size.CompareTo(b.Size),
            SortKey.Type => (a, b) =>
            {
                var c = string.Compare(ExtensionOf(a), ExtensionOf(b), StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : NaturalSort.Compare(a.Name, b.Name);
            },
            _ => (a, b) => NaturalSort.Compare(a.Name, b.Name),
        };
        return descending ? (a, b) => cmp(b, a) : cmp;
    }

    private static string ExtensionOf(FileSystemItem item)
        => item.IsDirectory ? "" : System.IO.Path.GetExtension(item.Name);
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }
}

public class ColumnModel : ObservableObject, IDisposable
{
    /// <summary>フォルダー列のパス。ホーム列・グループ列では null。</summary>
    public string? Path { get; }

    /// <summary>非 null のとき、この列はグループの中身 (サブグループ＋フォルダー) を表す。</summary>
    public string? GroupId { get; }

    public RangeObservableCollection<FileSystemItem> Items { get; } = new();

    private FileSystemItem? _selectedItem;
    public FileSystemItem? SelectedItem
    {
        get => _selectedItem;
        set => Set(ref _selectedItem, value);
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    /// <summary>フォルダー列 (path) / ホーム列 (null)。</summary>
    public ColumnModel(string? path) => Path = path;

    /// <summary>グループの中身を表す列。</summary>
    public ColumnModel(FavoriteGroup group) => GroupId = group.Id;

    public async Task LoadAsync(bool showHidden, Comparison<FileSystemItem> comparison)
    {
        Error = null;
        List<FileSystemItem> items;
        if (GroupId is not null)
        {
            var id = GroupId;
            items = await Task.Run(() => BuildGroupItems(id));
        }
        else if (Path is null)
        {
            items = await Task.Run(BuildHomeItems);
        }
        else
        {
            try
            {
                items = await Task.Run(() => Enumerate(Path, showHidden, comparison));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Error = "アクセスできません: " + ex.Message;
                items = new();
            }
        }

        // 再読み込みで作り直された項目にコピー / 切り取りマークを引き継ぐ
        if (!ClipboardMarks.IsEmpty)
            foreach (var item in items)
                if (item is { UseRealIcon: false, IsGroupEntry: false })
                    item.ClipMark = ClipboardMarks.MarkFor(item.Path);

        Items.ReplaceAll(items);

        _iconCts?.Cancel();
        _iconCts = new CancellationTokenSource();
        _ = LoadIconsAsync(_iconCts.Token);

        StartWatching();
    }

    private CancellationTokenSource? _iconCts;

    // ---- 外部変更の自動反映 (FileSystemWatcher) ----

    /// <summary>外部変更による再読み込み中。選択復帰でドリルイン (子の列を開く) が誤発火しないよう
    /// ハンドラー側でこのフラグを見て抑止する。</summary>
    public bool IsRefreshing { get; private set; }

    private FileSystemWatcher? _watcher;
    private System.Windows.Threading.DispatcherTimer? _fsTimer;
    private int _fsDirty; // 0=静穏 / 1=再読込予約済み (イベントの殺到を 1 回に束ねる)

    /// <summary>フォルダー列の監視を開始する。名前・属性 (クラウド状態) のみ監視し、
    /// 書き込み中のサイズ変化などの高頻度イベントは拾わない (軽量化)。</summary>
    private void StartWatching()
    {
        if (_watcher is not null || Path is null)
            return;
        try
        {
            _fsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _fsTimer.Tick += async (_, _) =>
            {
                _fsTimer!.Stop();
                Interlocked.Exchange(ref _fsDirty, 0);
                await RefreshFromWatcherAsync();
            };

            _watcher = new FileSystemWatcher(Path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
                IncludeSubdirectories = false,
            };
            _watcher.Created += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Renamed += OnFsEvent;
            _watcher.Changed += OnFsEvent;
            _watcher.Error += (_, _) => OnFsEvent(this, null); // バッファ溢れ等 → 取りこぼし前提で再読込
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // ネットワークパスや権限で監視できないフォルダーは自動更新なしで続行
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>watcher スレッドから呼ばれる。最初の 1 件だけ UI スレッドへタイマー起動を投げ、
    /// 以降 500ms 分のイベントはフラグで吸収する (殺到しても再読込は最大 2 回/秒)。</summary>
    private void OnFsEvent(object sender, FileSystemEventArgs? e)
    {
        if (Interlocked.Exchange(ref _fsDirty, 1) == 1)
            return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _fsTimer?.Start());
    }

    /// <summary>この列だけを再読み込みし、選択を保持する。</summary>
    private async Task RefreshFromWatcherAsync()
    {
        if (Path is null || _watcher is null)
            return;
        IsRefreshing = true;
        try
        {
            var settings = AppSettings.Current;
            var selectedPath = SelectedItem?.Path;
            await LoadAsync(settings.ShowHidden,
                FileSystemItem.BuildComparison(settings.SortKey, settings.SortDescending));
            if (selectedPath is not null)
                SelectedItem = Items.FirstOrDefault(
                    i => string.Equals(i.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>列が閉じられたら監視とアイコン読み込みを確実に止める。</summary>
    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _fsTimer?.Stop();
        _fsTimer = null;
        _iconCts?.Cancel();
    }

    /// <summary>表示中ファイルの実アイコンをバックグラウンドで読み込み、順次差し替える。</summary>
    private async Task LoadIconsAsync(CancellationToken ct)
    {
        foreach (var item in Items.Where(i => i.WantsRealIcon).ToList())
        {
            if (ct.IsCancellationRequested)
                return;
            var path = item.Path;
            try
            {
                var icon = await Task.Run(() => IconCache.GetByPath(path), ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                    return;
                if (icon is not null)
                    item.ApplyRealIcon(icon);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>読み込み済みの項目を並べ替え直す (フォルダ優先は維持)。選択は保持する。</summary>
    public void ApplySort(Comparison<FileSystemItem> comparison)
    {
        if (Path is null || Items.Count == 0)
            return;

        var selected = SelectedItem;
        var dirs = Items.Where(i => i.IsDirectory).ToList();
        var files = Items.Where(i => !i.IsDirectory).ToList();
        dirs.Sort(comparison);
        files.Sort(comparison);

        Items.ReplaceAll(dirs.Concat(files));
        SelectedItem = selected;
    }

    private static List<FileSystemItem> Enumerate(string path, bool showHidden, Comparison<FileSystemItem> comparison)
    {
        var dir = new DirectoryInfo(path);
        var dirs = new List<FileSystemItem>();
        var files = new List<FileSystemItem>();

        foreach (var info in dir.EnumerateFileSystemInfos())
        {
            var attrs = info.Attributes;
            if (!showHidden && (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                continue;

            var isDir = (attrs & FileAttributes.Directory) != 0;
            var item = new FileSystemItem
            {
                Path = info.FullName,
                Name = info.Name,
                IsDirectory = isDir,
                Cloud = FileSystemItem.GetCloudStatus(attrs),
                Modified = info.LastWriteTime,
                Size = isDir ? 0 : (info as FileInfo)?.Length ?? 0,
            };
            (isDir ? dirs : files).Add(item);
        }

        dirs.Sort(comparison);
        files.Sort(comparison);
        dirs.AddRange(files);
        return dirs;
    }

    /// <summary>グループの中身 (サブグループ＋フォルダー) を統一並び順で 1 列ぶんの項目として組み立てる。</summary>
    private static List<FileSystemItem> BuildGroupItems(string groupId)
    {
        var items = new List<FileSystemItem>();
        var group = AppSettings.Current.FindGroup(groupId);
        if (group is null)
            return items;

        foreach (var key in AppSettings.Current.OrderedChildKeys(group))
        {
            if (key.StartsWith("group:", StringComparison.Ordinal))
            {
                var sub = AppSettings.Current.FindGroup(key["group:".Length..]);
                if (sub is null)
                    continue;
                items.Add(new FileSystemItem
                {
                    Path = "group:" + sub.Id,
                    Name = sub.Name,
                    IsGroupEntry = true,
                    GroupId = sub.Id,
                    MemberCount = sub.Subgroups.Count + sub.Paths.Count,
                });
            }
            else
            {
                var name = System.IO.Path.GetFileName(key.TrimEnd('\\'));
                items.Add(new FileSystemItem
                {
                    Path = key,
                    Name = string.IsNullOrEmpty(name) ? key : name,
                    IsDirectory = true,
                    UseRealIcon = Directory.Exists(key),
                    IsGroupMember = true,
                    GroupId = group.Id,
                });
            }
        }
        return items;
    }

    private static List<FileSystemItem> BuildHomeItems()
    {
        var items = new List<FileSystemItem>();

        // お気に入り＋トップレベルのグループ (統一並び順で最上段に表示)
        foreach (var key in AppSettings.Current.OrderedHomeKeys())
        {
            if (key.StartsWith("group:", StringComparison.Ordinal))
            {
                var group = AppSettings.Current.FindGroup(key["group:".Length..]);
                if (group is null)
                    continue;
                items.Add(new FileSystemItem
                {
                    Path = "group:" + group.Id,
                    Name = group.Name,
                    IsGroupEntry = true,
                    GroupId = group.Id,
                    MemberCount = group.Subgroups.Count + group.Paths.Count,
                });
            }
            else
            {
                if (!Directory.Exists(key))
                    continue;
                var name = System.IO.Path.GetFileName(key.TrimEnd('\\'));
                items.Add(new FileSystemItem
                {
                    Path = key,
                    Name = string.IsNullOrEmpty(name) ? key : name,
                    IsDirectory = true,
                    UseRealIcon = true,
                    IsFavoriteEntry = true,
                });
            }
        }

        void AddKnown(string name, string? path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return;
            items.Add(new FileSystemItem
            {
                Path = path,
                Name = name,
                IsDirectory = true,
                UseRealIcon = true,
            });
        }

        AddKnown("デスクトップ", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        AddKnown("ダウンロード", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        AddKnown("ドキュメント", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddKnown("ピクチャ", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddKnown("ホーム", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        var oneDrive = Environment.GetEnvironmentVariable("OneDrive")
            ?? Environment.GetEnvironmentVariable("OneDriveConsumer")
            ?? Environment.GetEnvironmentVariable("OneDriveCommercial");
        AddKnown("OneDrive", oneDrive);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
                continue;
            var label = string.IsNullOrEmpty(drive.VolumeLabel)
                ? (drive.DriveType == DriveType.Fixed ? "ローカル ディスク" : drive.DriveType.ToString())
                : drive.VolumeLabel;
            items.Add(new FileSystemItem
            {
                Path = drive.RootDirectory.FullName,
                Name = $"{label} ({drive.Name.TrimEnd('\\')})",
                IsDirectory = true,
                UseRealIcon = true,
            });
        }

        return items;
    }
}

public class TabModel : ObservableObject
{
    private string _title = "ホーム";
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public ObservableCollection<ColumnModel> Columns { get; } = new();

    /// <summary>ルートフォルダーの移動履歴 (null = ホーム)。Back/Forward 用。</summary>
    public List<string?> History { get; } = new();
    public int HistoryIndex { get; set; } = -1;
}
