using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace ColumnView;

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
}

public record Crumb(string Display, string Path, bool IsFirst);

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

    /// <summary>実アイコンを読み込むべきか (クラウド専用ファイルはダウンロード回避のため除外)。</summary>
    public bool WantsRealIcon => !UseRealIcon && !IsDirectory && Cloud != CloudStatus.CloudOnly;

    public string Badge => IsFavoriteEntry ? Glyphs.Star : Cloud switch
    {
        CloudStatus.CloudOnly => Glyphs.Cloud,
        CloudStatus.Local => Glyphs.Check,
        CloudStatus.Pinned => Glyphs.Pin,
        _ => "",
    };

    public Brush BadgeBrush => IsFavoriteEntry ? Brushes.Goldenrod : Cloud switch
    {
        CloudStatus.CloudOnly => Brushes.SteelBlue,
        CloudStatus.Local => Brushes.SeaGreen,
        CloudStatus.Pinned => Brushes.SeaGreen,
        _ => Brushes.Transparent,
    };

    public string BadgeTooltip => IsFavoriteEntry ? "お気に入り" : Cloud switch
    {
        CloudStatus.CloudOnly => "オンラインのみ (開くとダウンロード)",
        CloudStatus.Local => "このデバイスで使用可能",
        CloudStatus.Pinned => "常にこのデバイスに保持",
        _ => "",
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

public class ColumnModel : ObservableObject
{
    /// <summary>null のときはホーム列 (お気に入り + 特殊フォルダ + ドライブ)</summary>
    public string? Path { get; }

    public ObservableCollection<FileSystemItem> Items { get; } = new();

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

    public ColumnModel(string? path) => Path = path;

    public async Task LoadAsync(bool showHidden, Comparison<FileSystemItem> comparison)
    {
        Error = null;
        List<FileSystemItem> items;
        if (Path is null)
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

        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        _iconCts?.Cancel();
        _iconCts = new CancellationTokenSource();
        _ = LoadIconsAsync(_iconCts.Token);
    }

    private CancellationTokenSource? _iconCts;

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

        Items.Clear();
        foreach (var item in dirs.Concat(files))
            Items.Add(item);
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

    private static List<FileSystemItem> BuildHomeItems()
    {
        var items = new List<FileSystemItem>();

        // お気に入り (先頭に表示)
        foreach (var fav in AppSettings.Current.Favorites)
        {
            if (!Directory.Exists(fav))
                continue;
            var name = System.IO.Path.GetFileName(fav.TrimEnd('\\'));
            items.Add(new FileSystemItem
            {
                Path = fav,
                Name = string.IsNullOrEmpty(name) ? fav : name,
                IsDirectory = true,
                UseRealIcon = true,
                IsFavoriteEntry = true,
            });
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
