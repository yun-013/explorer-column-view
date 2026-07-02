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
        await RefreshGroupColumnsAsync();
    }

    /// <summary>お気に入りをドラッグで並べ替える (source を target の前後へ移動)。</summary>
    public async Task MoveFavoriteAsync(string source, string target, bool insertAfter)
    {
        if (!_settings.MoveFavorite(source, target, insertAfter))
            return;
        StatusText = "お気に入りを並べ替えました";
        await RefreshGroupColumnsAsync();
    }

    // ---- タブグループ (入れ子・ID 管理) ----

    /// <summary>トップレベルのグループ一覧 (ツールバーのメニュー構築用)。</summary>
    public IReadOnlyList<FavoriteGroup> Groups => _settings.Groups;

    public FavoriteGroup? FindGroup(string id) => _settings.FindGroup(id);

    /// <summary>移動先候補 (source 自身とその子孫を除く)。深さ付き。</summary>
    public IEnumerable<(FavoriteGroup Group, int Depth)> GroupsExcept(string id)
        => _settings.EnumerateGroupsExcept(id);

    /// <summary>グループ配下 (サブグループ含む) の全フォルダーをそれぞれ新しいタブで開く。</summary>
    public async Task OpenGroupAsync(string id)
    {
        var group = _settings.FindGroup(id);
        if (group is null)
            return;
        var paths = _settings.CollectPaths(id).Where(Directory.Exists).ToList();
        if (paths.Count == 0)
        {
            StatusText = $"『{group.Name}』に開けるフォルダーがありません";
            return;
        }
        foreach (var path in paths)
            await NewTabAsync(path);
        StatusText = $"グループ『{group.Name}』を {paths.Count} 個のタブで開きました";
    }

    /// <summary>グループ直下のフォルダーだけをタブで開く (サブグループは含めない)。</summary>
    public async Task OpenGroupDirectAsync(string id)
    {
        var group = _settings.FindGroup(id);
        if (group is null)
            return;
        var paths = group.Paths.Where(Directory.Exists).ToList();
        if (paths.Count == 0)
        {
            StatusText = $"『{group.Name}』の直下に開けるフォルダーがありません";
            return;
        }
        foreach (var path in paths)
            await NewTabAsync(path);
        StatusText = $"グループ『{group.Name}』の直下 {paths.Count} 個をタブで開きました";
    }

    /// <summary>ホーム列 / グループ列の項目を統一順で並べ替える (containerGroupId==null はホーム列)。</summary>
    public async Task MoveChildAsync(string? containerGroupId, string sourceKey, string targetKey, bool insertAfter)
    {
        if (_settings.MoveChild(containerGroupId, sourceKey, targetKey, insertAfter))
        {
            StatusText = "並べ替えました";
            await RefreshGroupColumnsAsync();
        }
    }

    /// <summary>新しいグループを作成する。parentId 指定で入れ子、seed でフォルダーを初期登録。</summary>
    public async Task CreateGroupAsync(string name, IEnumerable<string>? seed = null, string? parentId = null)
    {
        var group = _settings.CreateGroup(name, parentId);
        if (group is null)
        {
            StatusText = "グループを作成できませんでした (名前が空、または親が見つかりません)";
            return;
        }
        if (seed is not null)
            foreach (var path in seed)
                _settings.AddToGroup(group.Id, path);
        StatusText = $"グループ『{group.Name}』を作成しました";
        await RefreshGroupColumnsAsync();
    }

    public async Task RenameGroupAsync(string id, string newName)
    {
        if (!_settings.RenameGroup(id, newName))
        {
            StatusText = "グループ名を変更できませんでした";
            return;
        }
        StatusText = "グループ名を変更しました";
        await RefreshGroupColumnsAsync();
    }

    public async Task DeleteGroupAsync(string id)
    {
        var name = _settings.FindGroup(id)?.Name;
        if (!_settings.DeleteGroup(id))
            return;
        StatusText = $"グループ『{name}』を削除しました";
        await RefreshGroupColumnsAsync();
    }

    /// <summary>現在表示中のフォルダーをグループに追加する。</summary>
    public async Task AddCurrentFolderToGroupAsync(string id)
    {
        var target = FavoriteTarget;
        if (target is null)
        {
            StatusText = "追加できるフォルダーがありません";
            return;
        }
        var groupName = _settings.FindGroup(id)?.Name ?? "";
        var label = Path.GetFileName(target.TrimEnd('\\'));
        StatusText = _settings.AddToGroup(id, target)
            ? $"『{(string.IsNullOrEmpty(label) ? target : label)}』を『{groupName}』に追加しました"
            : $"『{groupName}』には既に含まれています";
        await RefreshGroupColumnsAsync();
    }

    public async Task RemoveFromGroupAsync(string id, string path)
    {
        if (_settings.RemoveFromGroup(id, path))
        {
            StatusText = "グループからフォルダーを削除しました";
            await RefreshGroupColumnsAsync();
        }
    }

    /// <summary>フォルダー群をグループに追加する (グループ見出しへのドラッグ＆ドロップ用)。</summary>
    public async Task AddPathsToGroupAsync(string id, IEnumerable<string> paths)
    {
        if (_settings.FindGroup(id) is not { } group)
            return;
        var added = 0;
        foreach (var path in paths)
            if (Directory.Exists(path) && _settings.AddToGroup(id, path))
                added++;
        StatusText = added > 0
            ? $"『{group.Name}』に {added} 個のフォルダーを追加しました"
            : $"『{group.Name}』には既に含まれています";
        await RefreshGroupColumnsAsync();
    }

    /// <summary>グループをドラッグで並べ替える (source を target の前後へ)。</summary>
    public async Task MoveGroupRelativeAsync(string sourceId, string targetId, bool insertAfter)
    {
        if (_settings.MoveGroupRelative(sourceId, targetId, insertAfter))
        {
            StatusText = "グループを並べ替えました";
            await RefreshGroupColumnsAsync();
        }
    }

    /// <summary>グループを別のグループの入れ子にする。</summary>
    public async Task MoveGroupIntoAsync(string sourceId, string targetId)
    {
        if (_settings.MoveGroupInto(sourceId, targetId))
        {
            var name = _settings.FindGroup(targetId)?.Name;
            StatusText = $"『{name}』の中に移動しました";
            await RefreshGroupColumnsAsync();
        }
    }

    /// <summary>グループをトップレベルへ戻す。</summary>
    public async Task MoveGroupToTopAsync(string sourceId)
    {
        if (_settings.MoveGroupToTop(sourceId))
        {
            StatusText = "グループをトップレベルへ移動しました";
            await RefreshGroupColumnsAsync();
        }
    }

    /// <summary>グループ内のメンバーフォルダーをドラッグで並べ替える。</summary>
    public async Task MovePathInGroupAsync(string groupId, string source, string target, bool insertAfter)
    {
        if (_settings.MovePathInGroup(groupId, source, target, insertAfter))
        {
            StatusText = "メンバーを並べ替えました";
            await RefreshGroupColumnsAsync();
        }
    }

    /// <summary>現在開いている各タブのルートフォルダー一覧 (ホームタブは除外)。</summary>
    public List<string> OpenTabRoots()
        => Tabs.Select(t => t.Columns.FirstOrDefault()?.Path)
            .Where(p => p is not null && Directory.Exists(p!))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>ホーム列・グループ列 (どちらも Path==null) を再読み込みする (選択は保持)。</summary>
    private async Task RefreshGroupColumnsAsync()
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
            _ = InitTabsAsync();
    }

    /// <summary>起動時: 前回のセッションがあれば復元し、なければホームタブを開く。</summary>
    private async Task InitTabsAsync()
    {
        if (_settings.RestoreSession && _settings.SessionTabs.Count > 0)
        {
            foreach (var path in _settings.SessionTabs)
            {
                if (path is null)
                    await NewTabAsync(null);
                else if (Directory.Exists(path))
                    await NewTabAsync(path);
            }
            if (Tabs.Count > 0)
            {
                var active = _settings.SessionActiveTab;
                if (active >= 0 && active < Tabs.Count)
                    ActiveTab = Tabs[active];
                return;
            }
        }
        await NewTabAsync(null);
    }

    /// <summary>現在のタブ構成 (各タブの最深フォルダー) を設定へ保存する。最後のウィンドウを閉じるときに呼ぶ。</summary>
    public void SaveSession()
    {
        _settings.SessionTabs = Tabs
            .Select(t => t.Columns.LastOrDefault(c => c.Path is not null)?.Path)
            .ToList();
        _settings.SessionActiveTab = ActiveTab is null ? 0 : Tabs.IndexOf(ActiveTab);
        _settings.Save();
    }

    public async Task NewTabAsync(string? path)
    {
        var tab = new TabModel();
        Tabs.Add(tab);
        ActiveTab = tab;
        await ResetTabAsync(tab, path);
        PushHistory(tab, path);
    }

    /// <summary>タブの列を末尾から keep 個になるまで取り除く (監視も止める)。</summary>
    private static void TrimColumns(TabModel tab, int keep)
    {
        while (tab.Columns.Count > keep)
        {
            var last = tab.Columns[^1];
            tab.Columns.RemoveAt(tab.Columns.Count - 1);
            last.Dispose();
        }
    }

    public void CloseTab(TabModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;
        var wasActive = ActiveTab == tab;
        Tabs.Remove(tab);
        TrimColumns(tab, 0);
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
            TrimColumns(tab, 0);
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
        // IsRefreshing: 外部変更の再読み込みによる選択復帰はナビゲーションとして扱わない
        if (_navigating || column.IsRefreshing || ActiveTab is null)
            return;

        var tab = ActiveTab;
        var index = tab.Columns.IndexOf(column);
        if (index < 0)
            return;

        // 選択列より右の列を畳む
        TrimColumns(tab, index + 1);

        // グループ見出し: 中身 (サブグループ＋フォルダー) を次の列に展開する。
        // CurrentPath は据え置き (直前のフォルダーを「現在のフォルダー」として追加できるように)。
        if (item.IsGroupEntry)
        {
            if (item.GroupId is not { } gid || _settings.FindGroup(gid) is not { } group)
                return;
            var groupColumn = new ColumnModel(group);
            tab.Columns.Add(groupColumn);
            tab.Title = TrimTitle(group.Name);
            await groupColumn.LoadAsync(ShowHidden, CurrentComparison);
            UpdateStatus(groupColumn);
            return;
        }

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
        if (_navigating || column.IsRefreshing || ActiveTab is not { } tab)
            return;
        var index = tab.Columns.IndexOf(column);
        if (index < 0)
            return;
        TrimColumns(tab, index + 1);
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

    // ---- クリップボード (コピー / 切り取り / 貼り付け) ・削除・新規フォルダー ----

    /// <summary>選択ファイル群をクリップボードへ (エクスプローラー互換)。cut=true で切り取り。</summary>
    public void CopyToClipboard(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0)
            return;
        StatusText = ClipboardOps.SetFiles(paths, cut)
            ? (cut ? $"切り取り: {paths.Count} 個" : $"コピー: {paths.Count} 個")
            : "クリップボードを使用できませんでした";
    }

    /// <summary>クリップボードのファイルを targetDir へ貼り付ける (切り取りなら移動)。</summary>
    public async Task PasteAsync(string targetDir)
    {
        var files = ClipboardOps.GetFiles(out var cut);
        if (files is null)
        {
            StatusText = "貼り付けるファイルがありません";
            return;
        }
        var affected = FileOps.Transfer(files, targetDir, copy: !cut, out var error);
        if (cut)
            ClipboardOps.ClearAfterMove();
        StatusText = error ?? (cut ? $"移動しました → {targetDir}" : $"貼り付けました → {targetDir}");
        await RefreshColumnsAsync(affected);
    }

    /// <summary>選択ファイル群を削除する (permanent=false はごみ箱へ)。</summary>
    public async Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, nint ownerHwnd)
    {
        if (paths.Count == 0)
            return;
        var affected = FileOps.Delete(paths, permanent, ownerHwnd, out var error);
        StatusText = error ?? (permanent ? $"削除しました: {paths.Count} 個" : $"ごみ箱へ移動しました: {paths.Count} 個");
        await RefreshColumnsAsync(affected);
    }

    /// <summary>現在のフォルダーに新しいフォルダーを作る。既定名の重複は連番を付ける。</summary>
    public string? SuggestNewFolderName()
    {
        if (FavoriteTarget is not { } parent)
            return null;
        var name = "新しいフォルダー";
        var n = 2;
        while (Directory.Exists(Path.Combine(parent, name)) || File.Exists(Path.Combine(parent, name)))
            name = $"新しいフォルダー ({n++})";
        return name;
    }

    public async Task CreateFolderAsync(string name)
    {
        name = name.Trim();
        if (FavoriteTarget is not { } parent || string.IsNullOrEmpty(name))
            return;
        try
        {
            var path = Path.Combine(parent, name);
            if (Directory.Exists(path) || File.Exists(path))
            {
                StatusText = $"『{name}』は既に存在します";
                return;
            }
            Directory.CreateDirectory(path);
            StatusText = $"フォルダーを作成しました: {name}";
            await RefreshColumnsAsync(new[] { parent });
        }
        catch (Exception ex)
        {
            StatusText = "フォルダーを作成できませんでした: " + ex.Message;
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
        if (column.GroupId is not null)
        {
            var subs = column.Items.Count(i => i.IsGroupEntry);
            var folders = column.Items.Count(i => i.IsGroupMember);
            StatusText = $"{subs} サブグループ、{folders} フォルダー";
            return;
        }
        var dirs = column.Items.Count(i => i.IsDirectory);
        var files = column.Items.Count - dirs;
        StatusText = $"{dirs} フォルダー、{files} ファイル";
    }

    private static string TrimTitle(string title)
        => title.Length > 24 ? title[..22] + "…" : title;

    private static string FormatSize(long bytes) => FileSystemItem.FormatSize(bytes);
}
