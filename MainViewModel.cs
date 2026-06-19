using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace ColumnView;

public class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Current;

    public ObservableCollection<TabModel> Tabs { get; } = new();

    private TabModel? _activeTab;
    public TabModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (Set(ref _activeTab, value))
            {
                UpdateCurrentPathFromTab();
                RaiseNavState();
            }
        }
    }

    // ---- 戻る / 進む / 上へ ----

    public bool CanGoBack => ActiveTab is { } t && t.HistoryIndex > 0;
    public bool CanGoForward => ActiveTab is { } t && t.HistoryIndex < t.History.Count - 1;
    public bool CanGoUp => ActiveTab?.Columns.FirstOrDefault()?.Path is not null;

    private void RaiseNavState()
    {
        Raise(nameof(CanGoBack));
        Raise(nameof(CanGoForward));
        Raise(nameof(CanGoUp));
    }

    private void PushHistory(TabModel tab, string? path)
    {
        if (tab.HistoryIndex < tab.History.Count - 1)
            tab.History.RemoveRange(tab.HistoryIndex + 1, tab.History.Count - tab.HistoryIndex - 1);
        tab.History.Add(path);
        tab.HistoryIndex = tab.History.Count - 1;
        RaiseNavState();
    }

    public async Task GoBackAsync()
    {
        if (ActiveTab is not { } tab || tab.HistoryIndex <= 0)
            return;
        tab.HistoryIndex--;
        await ResetTabAsync(tab, tab.History[tab.HistoryIndex]);
        RaiseNavState();
    }

    public async Task GoForwardAsync()
    {
        if (ActiveTab is not { } tab || tab.HistoryIndex >= tab.History.Count - 1)
            return;
        tab.HistoryIndex++;
        await ResetTabAsync(tab, tab.History[tab.HistoryIndex]);
        RaiseNavState();
    }

    public async Task GoUpAsync()
    {
        if (ActiveTab is not { } tab)
            return;
        var root = tab.Columns.FirstOrDefault()?.Path;
        if (root is null)
            return;
        var parent = Directory.GetParent(root)?.FullName;
        await ResetTabAsync(tab, parent);
        PushHistory(tab, parent);
    }

    public async Task RenameAsync(FileSystemItem item, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.Name)
            return;
        var dir = Path.GetDirectoryName(item.Path);
        if (dir is null)
            return;
        try
        {
            var dest = Path.Combine(dir, newName);
            if (item.IsDirectory)
                Directory.Move(item.Path, dest);
            else
                File.Move(item.Path, dest);
            StatusText = $"名前を変更しました: {newName}";
            await RefreshColumnsAsync(new[] { dir });
        }
        catch (Exception ex)
        {
            StatusText = "名前を変更できませんでした: " + ex.Message;
        }
    }

    private string _currentPath = "";
    public string CurrentPath
    {
        get => _currentPath;
        set
        {
            if (Set(ref _currentPath, value))
            {
                Raise(nameof(IsCurrentFavorite));
                BuildBreadcrumbs();
            }
        }
    }

    // ---- アドレスバー (パンくず / 編集) ----

    public ObservableCollection<Crumb> Breadcrumbs { get; } = new();

    private bool _isEditingAddress;
    public bool IsEditingAddress
    {
        get => _isEditingAddress;
        set => Set(ref _isEditingAddress, value);
    }

    private void BuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new Crumb("ホーム", "", true));

        var path = CurrentPath;
        if (string.IsNullOrEmpty(path))
            return;

        var dir = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? path);
        var root = Path.GetPathRoot(dir);
        if (string.IsNullOrEmpty(root))
            return;

        var acc = root;
        Breadcrumbs.Add(new Crumb(root.TrimEnd('\\'), root, false));
        var rest = dir.Length > root.Length ? dir[root.Length..] : "";
        foreach (var part in rest.Split('\\', '/'))
        {
            if (string.IsNullOrEmpty(part))
                continue;
            acc = Path.Combine(acc, part);
            Breadcrumbs.Add(new Crumb(part, acc, false));
        }
    }

    public async Task NavigateCrumbAsync(Crumb crumb)
    {
        if (ActiveTab is null)
            return;
        if (string.IsNullOrEmpty(crumb.Path))
        {
            await ResetTabAsync(ActiveTab, null);
            PushHistory(ActiveTab, null);
        }
        else
        {
            await NavigateToPathAsync(crumb.Path);
        }
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool ShowHidden
    {
        get => _settings.ShowHidden;
        set
        {
            if (_settings.ShowHidden == value)
                return;
            _settings.ShowHidden = value;
            _settings.Save();
            Raise();
            _ = ReloadActiveTabAsync();
        }
    }

    // ---- 並べ替え ----

    public int SortKeyIndex
    {
        get => (int)_settings.SortKey;
        set
        {
            if ((int)_settings.SortKey == value || value < 0)
                return;
            _settings.SortKey = (SortKey)value;
            _settings.Save();
            Raise();
            ResortAll();
        }
    }

    public bool SortDescending
    {
        get => _settings.SortDescending;
        set
        {
            if (_settings.SortDescending == value)
                return;
            _settings.SortDescending = value;
            _settings.Save();
            Raise();
            ResortAll();
        }
    }

    private Comparison<FileSystemItem> CurrentComparison
        => FileSystemItem.BuildComparison(_settings.SortKey, _settings.SortDescending);

    private void ResortAll()
    {
        _navigating = true;
        try
        {
            var comparison = CurrentComparison;
            foreach (var tab in Tabs)
                foreach (var column in tab.Columns)
                    column.ApplySort(comparison);
        }
        finally
        {
            _navigating = false;
        }
        StatusText = $"並べ替え: {SortKeyLabel(_settings.SortKey)} ({(_settings.SortDescending ? "降順" : "昇順")})";
    }

    private static string SortKeyLabel(SortKey key) => key switch
    {
        SortKey.Modified => "更新日時",
        SortKey.Size => "サイズ",
        SortKey.Type => "種類",
        _ => "名前",
    };

    // ---- お気に入り ----

    /// <summary>お気に入り登録対象 (現在のパスがファイルなら親フォルダ)</summary>
    public string? FavoriteTarget
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentPath))
                return null;
            if (Directory.Exists(CurrentPath))
                return Path.GetFullPath(CurrentPath);
            if (File.Exists(CurrentPath))
                return Path.GetDirectoryName(CurrentPath);
            return null;
        }
    }

    public bool IsCurrentFavorite
        => FavoriteTarget is { } target && _settings.IsFavorite(target);

    public async Task ToggleFavoriteAsync(string? path)
    {
        path ??= FavoriteTarget;
        if (path is null)
        {
            StatusText = "お気に入りに登録できるのはフォルダーのみです";
            return;
        }
        if (File.Exists(path))
            path = Path.GetDirectoryName(path);
        if (path is null || !Directory.Exists(path))
            return;

        var added = _settings.ToggleFavorite(Path.GetFullPath(path));
        Raise(nameof(IsCurrentFavorite));
        StatusText = added ? $"お気に入りに追加: {path}" : $"お気に入りから削除: {path}";
        await RefreshHomeColumnsAsync();
    }

    private async Task RefreshHomeColumnsAsync()
    {
        _navigating = true;
        try
        {
            foreach (var tab in Tabs)
            {
                foreach (var column in tab.Columns.Where(c => c.Path is null))
                {
                    var selectedPath = column.SelectedItem?.Path;
                    await column.LoadAsync(ShowHidden, CurrentComparison);
                    if (selectedPath is not null)
                        column.SelectedItem = column.Items.FirstOrDefault(
                            i => string.Equals(i.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        finally
        {
            _navigating = false;
        }
    }

    // ---- タブ・ナビゲーション ----

    private string? _lastPreviewPath;
    private bool _navigating;

    /// <summary>タブが 0 個になったとき (最後のタブを閉じた / 引き剝がした) に発火。</summary>
    public event Action? TabsEmptied;

    public MainViewModel() : this(true) { }

    public MainViewModel(bool createInitialTab)
    {
        if (createInitialTab)
            _ = NewTabAsync(null);
    }

    public async Task NewTabAsync(string? path)
    {
        var tab = new TabModel();
        Tabs.Add(tab);
        ActiveTab = tab;
        await ResetTabAsync(tab, path);
        PushHistory(tab, path);
    }

    public void CloseTab(TabModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;
        var wasActive = ActiveTab == tab;
        Tabs.Remove(tab);
        if (Tabs.Count == 0)
        {
            TabsEmptied?.Invoke();
            return;
        }
        if (wasActive || ActiveTab is null)
            ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    /// <summary>タブの中身を指定パス起点で作り直す (パスバーから移動 / 隠しファイル切替)</summary>
    public async Task ResetTabAsync(TabModel tab, string? path)
    {
        _navigating = true;
        try
        {
            tab.Columns.Clear();
            var column = new ColumnModel(path);
            tab.Columns.Add(column);
            await column.LoadAsync(ShowHidden, CurrentComparison);
            tab.Title = path is null ? "ホーム" : TrimTitle(Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path);
            CurrentPath = path ?? "";
            UpdateStatus(column);
        }
        finally
        {
            _navigating = false;
        }
    }

    public async Task NavigateToPathAsync(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (ActiveTab is null || !Directory.Exists(path))
        {
            StatusText = $"フォルダーが見つかりません: {path}";
            return;
        }
        await ResetTabAsync(ActiveTab, path);
        PushHistory(ActiveTab, path);
    }

    private async Task ReloadActiveTabAsync()
    {
        if (ActiveTab is null)
            return;
        var root = ActiveTab.Columns.FirstOrDefault()?.Path;
        await ResetTabAsync(ActiveTab, root);
    }

    /// <summary>列で項目が選択されたときの中核ロジック</summary>
    public async Task OnItemSelectedAsync(ColumnModel column, FileSystemItem item)
    {
        if (_navigating || ActiveTab is null)
            return;

        var tab = ActiveTab;
        var index = tab.Columns.IndexOf(column);
        if (index < 0)
            return;

        // 選択列より右の列を畳む
        while (tab.Columns.Count > index + 1)
            tab.Columns.RemoveAt(tab.Columns.Count - 1);

        CurrentPath = item.Path;

        if (item.IsDirectory)
        {
            var next = new ColumnModel(item.Path);
            tab.Columns.Add(next);
            tab.Title = TrimTitle(item.Name);
            await next.LoadAsync(ShowHidden, CurrentComparison);
            UpdateStatus(next);
        }
        else
        {
            tab.Title = TrimTitle(Path.GetFileName(Path.GetDirectoryName(item.Path)) ?? item.Name);
            StatusText = $"{item.Name}  ({FormatSize(item.Size)})";
        }

        // Seer のプレビューが開いていれば選択に追従させる (Files と同じ方式)
        if (SeerInterop.IsPreviewVisible && _lastPreviewPath != item.Path)
        {
            SeerInterop.Toggle(item.Path);
            _lastPreviewPath = item.Path;
        }
    }

    /// <summary>複数選択時: 子の列を畳んで件数を表示する。</summary>
    public void OnMultiSelect(ColumnModel column, int count)
    {
        if (_navigating || ActiveTab is not { } tab)
            return;
        var index = tab.Columns.IndexOf(column);
        if (index < 0)
            return;
        while (tab.Columns.Count > index + 1)
            tab.Columns.RemoveAt(tab.Columns.Count - 1);
        StatusText = $"{count} 個を選択";
    }

    public void TogglePreview(FileSystemItem? item)
    {
        if (item is null)
            return;
        if (!SeerInterop.Toggle(item.Path))
        {
            StatusText = "Seer が起動していません。Seer を起動するとスペースキーでプレビューできます。";
            return;
        }
        _lastPreviewPath = item.Path;
    }

    public void OpenItem(FileSystemItem? item)
    {
        if (item is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "開けませんでした: " + ex.Message;
        }
    }

    public void RevealInExplorer(FileSystemItem? item)
    {
        if (item is null)
            return;
        Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
    }

    public void CopyPath(FileSystemItem? item)
    {
        if (item is null)
            return;
        try
        {
            System.Windows.Clipboard.SetText(item.Path);
            StatusText = $"パスをコピーしました: {item.Path}";
        }
        catch (Exception ex)
        {
            StatusText = "コピーできませんでした: " + ex.Message;
        }
    }

    // ---- ドラッグ&ドロップ ----

    public async Task HandleDropAsync(string[] sources, string targetDir, bool copy)
    {
        var affected = FileOps.Transfer(sources, targetDir, copy, out var error);
        if (error is not null)
            StatusText = error;
        else
            StatusText = copy ? $"コピーしました → {targetDir}" : $"移動しました → {targetDir}";
        await RefreshColumnsAsync(affected);
    }

    /// <summary>指定フォルダーを表示している列を再読み込みする (選択は保持)。</summary>
    public async Task RefreshColumnsAsync(IEnumerable<string> dirs)
    {
        var set = new HashSet<string>(dirs, StringComparer.OrdinalIgnoreCase);
        _navigating = true;
        try
        {
            foreach (var tab in Tabs)
            {
                foreach (var column in tab.Columns)
                {
                    if (column.Path is null || !set.Contains(column.Path))
                        continue;
                    var selectedPath = column.SelectedItem?.Path;
                    await column.LoadAsync(ShowHidden, CurrentComparison);
                    if (selectedPath is not null)
                        column.SelectedItem = column.Items.FirstOrDefault(
                            i => string.Equals(i.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        finally
        {
            _navigating = false;
        }
    }

    private void UpdateCurrentPathFromTab()
    {
        if (ActiveTab is null)
            return;
        var deepest = ActiveTab.Columns.LastOrDefault();
        CurrentPath = deepest?.SelectedItem?.Path ?? deepest?.Path ?? "";
    }

    private void UpdateStatus(ColumnModel column)
    {
        if (column.Error is not null)
        {
            StatusText = column.Error;
            return;
        }
        var dirs = column.Items.Count(i => i.IsDirectory);
        var files = column.Items.Count - dirs;
        StatusText = $"{dirs} フォルダー、{files} ファイル";
    }

    private static string TrimTitle(string title)
        => title.Length > 24 ? title[..22] + "…" : title;

    private static string FormatSize(long bytes)
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
}
