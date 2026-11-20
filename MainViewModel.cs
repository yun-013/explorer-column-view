using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;

namespace ColumnView;

public class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Current;

    public ObservableCollection<TabModel> Tabs { get; } = new();

    /// <summary>アクティブタブが切り替わる直前 (引数は離れるタブ)。
    /// 画面側がスクロール位置などの表示状態を控えるのに使う。</summary>
    public event Action<TabModel>? ActiveTabChanging;

    private TabModel? _activeTab;
    public TabModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (_activeTab is { } leaving && leaving != value)
                ActiveTabChanging?.Invoke(leaving);
            if (Set(ref _activeTab, value))
            {
                if (value?.Pending is not null)
                    _ = MaterializeAsync(value);
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

    /// <summary>ファイル操作の前に、関わる共有に届くかを確かめる。ファイル操作そのものは
    /// UI スレッドで同期的に走るため、届かない共有が混ざっていると数十秒固まる。
    /// 届かなければ操作をやめて理由をステータスに出し、false を返す。</summary>
    private async Task<bool> EnsureReachableAsync(IEnumerable<string> paths)
    {
        var shares = paths.Where(Reachability.IsNetworkPath)
            .Select(p => Reachability.ShareRoot(p) ?? p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (shares.Count == 0)
            return true;
        var reachable = await Task.WhenAll(shares.Select(s => Reachability.IsReachableAsync(s)));
        if (shares.Where((_, i) => !reachable[i]).FirstOrDefault() is not { } unreachable)
            return true;
        StatusText = $"接続先に届かないため、操作をやめました: {unreachable}";
        return false;
    }

    /// <summary>ファイル操作 (専用スレッドで実行) を 1 つずつ順番に通す。前の操作が終わって
    /// 取り消し履歴が積まれてから次へ進むので、実行中に Ctrl+Z を押しても順序が狂わない。
    /// UndoStack と同じく全ウィンドウで共有する。</summary>
    private static readonly SemaphoreSlim _fileOpGate = new(1, 1);

    public async Task RenameAsync(FileSystemItem item, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.Name)
            return;
        if (!await EnsureReachableAsync(new[] { item.Path }))
            return;
        var dir = Path.GetDirectoryName(item.Path);
        if (dir is null)
            return;
        await _fileOpGate.WaitAsync();
        try
        {
            var source = item.Path;
            var dest = Path.Combine(dir, newName);
            var isDirectory = item.IsDirectory;
            await FileWorker.RunAsync(0, () =>
            {
                if (isDirectory)
                    Directory.Move(source, dest);
                else
                    File.Move(source, dest);
                return true;
            });
            UndoStack.Push(new RenameOp(source, dest));
            StatusText = $"名前を変更しました: {newName}";
        }
        catch (Exception ex)
        {
            StatusText = "名前を変更できませんでした: " + ex.Message;
            return;
        }
        finally
        {
            _fileOpGate.Release();
        }
        await RefreshColumnsAsync(new[] { dir });
    }

    private string _currentPath = "";
    public string CurrentPath => _currentPath;

    /// <summary>CurrentPath がフォルダか (false ならファイル)。項目を選んだ時点で分かっているので、
    /// 星ボタンやパンくずのためにファイルシステムへ聞き直さない — 届かない共有だと
    /// その存在確認が UI スレッドで数十秒止まる。</summary>
    private bool _currentIsDirectory = true;

    private void SetCurrentPath(string path, bool isDirectory)
    {
        var directoryChanged = _currentIsDirectory != isDirectory;
        _currentIsDirectory = isDirectory;
        if (Set(ref _currentPath, path, nameof(CurrentPath)) || directoryChanged)
        {
            Raise(nameof(IsCurrentFavorite));
            BuildBreadcrumbs();
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

        var dir = _currentIsDirectory ? path : (Path.GetDirectoryName(path) ?? path);
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
            _ = ReloadAllColumnsAsync();
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

    /// <summary>フォルダを先頭にまとめるか (false = フォルダとファイルを同列に並べる)。</summary>
    public bool FoldersFirst
    {
        get => _settings.FoldersFirst;
        set
        {
            if (_settings.FoldersFirst == value)
                return;
            _settings.FoldersFirst = value;
            _settings.Save();
            Raise();
            ResortAll();
        }
    }

    private Comparison<FileSystemItem> CurrentComparison
        => FileSystemItem.BuildComparison(_settings.SortKey, _settings.SortDescending, _settings.FoldersFirst);

    private void ResortAll()
    {
        _navigatingDepth++;
        try
        {
            var comparison = CurrentComparison;
            foreach (var tab in Tabs)
                foreach (var column in tab.Columns)
                    column.ApplySort(comparison);
        }
        finally
        {
            _navigatingDepth--;
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

    /// <summary>お気に入り登録対象 (現在のパスがファイルなら親フォルダ)。
    /// バインディング経由で UI スレッドから頻繁に読まれるので、ファイルシステムには触れない。</summary>
    public string? FavoriteTarget
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentPath))
                return null;
            try
            {
                return _currentIsDirectory ? Path.GetFullPath(CurrentPath) : Path.GetDirectoryName(CurrentPath);
            }
            catch
            {
                return null;
            }
        }
    }

    public bool IsCurrentFavorite
        => FavoriteTarget is { } target && _settings.IsFavorite(target);

    /// <summary>お気に入りに登録済みか (文字列で比べるだけで、ファイルシステムには触れない)。</summary>
    public bool IsFavoritePath(string path) => _settings.IsFavorite(path);

    public async Task ToggleFavoriteAsync(string? path)
    {
        path ??= FavoriteTarget;
        if (path is null)
        {
            StatusText = "お気に入りに登録できるのはフォルダのみです";
            return;
        }

        // 登録済みなら存在を確かめずに外す (届かない共有のお気に入りも外せるように)
        var full = Path.GetFullPath(path);
        if (!_settings.IsFavorite(full))
        {
            // 追加はフォルダに限る。存在確認は届かない共有だと数十秒かかるので裏で行う
            var folder = await Task.Run(() =>
                Directory.Exists(full) ? full : File.Exists(full) ? Path.GetDirectoryName(full) : null);
            if (folder is null)
            {
                StatusText = $"お気に入りに追加できませんでした (フォルダが見つかりません): {path}";
                return;
            }
            full = folder; // ファイルなら親フォルダを対象にする (登録済みなら外す)
        }

        var added = _settings.ToggleFavorite(full);
        Raise(nameof(IsCurrentFavorite));
        StatusText = added ? $"お気に入りに追加: {full}" : $"お気に入りから削除: {full}";
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

    /// <summary>UI スレッドから使うフォルダの存在確認。確認は裏で行い、届かない共有は
    /// 短い時間で諦めて false を返す (UI スレッドで Directory.Exists すると数十秒固まる)。</summary>
    private static async Task<bool> DirectoryExistsAsync(string path)
    {
        if (Reachability.IsNetworkPath(path) && !await Reachability.IsReachableAsync(path))
            return false;
        return await Task.Run(() => Directory.Exists(path));
    }

    /// <summary>フォルダが消えたと言えるか。共有ごと届かないときは「消えた」とは見なさない
    /// (列は残して、読み込み側の「接続先に届きません」に任せる)。同期版は裏スレッドから呼ぶこと
    /// (届かない共有の確認を最長 <see cref="Reachability.DefaultTimeout"/> だけ待つ)。</summary>
    private static bool IsFolderGone(string path)
    {
        if (Reachability.IsNetworkPath(path) && !Reachability.IsReachableAsync(path).GetAwaiter().GetResult())
            return false;
        return !Directory.Exists(path);
    }

    private static Task<bool> IsFolderGoneAsync(string path) => Task.Run(() => IsFolderGone(path));

    /// <summary>存在するフォルダだけを元の順で返す (並行に確かめる)。</summary>
    private static async Task<List<string>> ExistingDirectoriesAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var exists = await Task.WhenAll(list.Select(DirectoryExistsAsync));
        return list.Where((_, i) => exists[i]).ToList();
    }

    /// <summary>グループ配下 (サブグループ含む) の全フォルダをそれぞれ新しいタブで開く。</summary>
    public async Task OpenGroupAsync(string id)
    {
        var group = _settings.FindGroup(id);
        if (group is null)
            return;
        var paths = await ExistingDirectoriesAsync(_settings.CollectPaths(id));
        if (paths.Count == 0)
        {
            StatusText = $"『{group.Name}』に開けるフォルダがありません";
            return;
        }
        foreach (var path in paths)
            await NewTabAsync(path);
        StatusText = $"グループ『{group.Name}』を {paths.Count} 個のタブで開きました";
    }

    /// <summary>グループ直下のフォルダだけをタブで開く (サブグループは含めない)。</summary>
    public async Task OpenGroupDirectAsync(string id)
    {
        var group = _settings.FindGroup(id);
        if (group is null)
            return;
        var paths = await ExistingDirectoriesAsync(group.Paths);
        if (paths.Count == 0)
        {
            StatusText = $"『{group.Name}』の直下に開けるフォルダがありません";
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

    /// <summary>新しいグループを作成する。parentId 指定で入れ子、seed でフォルダを初期登録。</summary>
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

    /// <summary>現在表示中のフォルダをグループに追加する。</summary>
    public async Task AddCurrentFolderToGroupAsync(string id)
    {
        var target = FavoriteTarget;
        if (target is null)
        {
            StatusText = "追加できるフォルダがありません";
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
            StatusText = "グループからフォルダを削除しました";
            await RefreshGroupColumnsAsync();
        }
    }

    /// <summary>フォルダ群をグループに追加する (グループ見出しへのドラッグ＆ドロップ用)。</summary>
    public async Task AddPathsToGroupAsync(string id, IEnumerable<string> paths)
    {
        if (_settings.FindGroup(id) is not { } group)
            return;
        var added = 0;
        foreach (var path in await ExistingDirectoriesAsync(paths))
            if (_settings.AddToGroup(id, path))
                added++;
        StatusText = added > 0
            ? $"『{group.Name}』に {added} 個のフォルダを追加しました"
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

    /// <summary>グループ内のメンバーフォルダをドラッグで並べ替える。</summary>
    public async Task MovePathInGroupAsync(string groupId, string source, string target, bool insertAfter)
    {
        if (_settings.MovePathInGroup(groupId, source, target, insertAfter))
        {
            StatusText = "メンバーを並べ替えました";
            await RefreshGroupColumnsAsync();
        }
    }

    /// <summary>現在開いている各タブのルートフォルダ一覧 (ホームタブは除外)。
    /// メニューを開くたびに UI スレッドで呼ばれるので、存在確認はしない
    /// (開いているタブの列なので存在する前提。届かないと分かっている共有だけ外す)。</summary>
    public List<string> OpenTabRoots()
        => Tabs.Select(t => t.Columns.FirstOrDefault()?.Path)
            .Where(p => p is not null && !Reachability.IsKnownUnreachable(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>ドライブ・機器の抜き差しでホーム列の顔ぶれを取り直す
    /// (MainWindow が WM_DEVICECHANGE を受けて呼ぶ)。</summary>
    public Task RefreshHomeColumnsAsync() => RefreshGroupColumnsAsync();

    /// <summary>記憶されたネットワークドライブ (NAS 等) を裏で繋ぎ直し、
    /// 繋がったらホーム列を並べ直す。起動時とネットワーク復帰時に呼ぶ。
    /// ホーム列の表示は待たせない — 先に「切断中」の行として出しておき、
    /// 繋がってから普通のドライブ (容量ゲージ付き) に差し替える。</summary>
    public async Task ReconnectNetworkDrivesAsync()
    {
        if (!await Task.Run(ComputerFolder.ReconnectRememberedDrives))
            return;
        StatusText = "ネットワークドライブに接続しました";
        await RefreshHomeColumnsAsync();
    }

    /// <summary>ホーム列・グループ列 (どちらも Path==null) を再読み込みする (選択は保持)。</summary>
    private async Task RefreshGroupColumnsAsync()
    {
        _navigatingDepth++;
        try
        {
            foreach (var tab in Tabs)
            {
                foreach (var column in tab.Columns.Where(c => c is { Path: null, IsSearch: false }))
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
            _navigatingDepth--;
        }
    }

    // ---- 検索 (現在のフォルダ以下をその場で走査) ----

    /// <summary>結果の上限。インデックスを持たない走査型なので、際限なく集めない。</summary>
    private const int MaxSearchResults = 500;

    private CancellationTokenSource? _searchCts;

    /// <summary>現在のフォルダ以下を名前で再帰検索し、結果を専用の列へ流し込む。
    /// インデックスや常駐監視は持たず、Enter のたびにその場で走査する (キャンセル可・上限あり)。</summary>
    public async Task SearchAsync(string query)
    {
        query = query.Trim();
        if (ActiveTab is not { } tab || string.IsNullOrEmpty(query))
            return;
        if (FavoriteTarget is not { } scope)
        {
            StatusText = "検索できるフォルダがありません";
            return;
        }

        _searchCts?.Cancel();

        // 検索起点を表示している列の右に結果列を出す (見つからなければ末尾に足す)
        var index = -1;
        for (var i = 0; i < tab.Columns.Count; i++)
            if (string.Equals(tab.Columns[i].Path, scope, StringComparison.OrdinalIgnoreCase))
                index = i;
        TrimColumns(tab, index >= 0 ? index + 1 : tab.Columns.Count);

        var column = ColumnModel.CreateSearch(query);
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        column.SearchCts = cts; // 列が閉じられたら (ナビゲーション等) 走査も止まる
        tab.Columns.Add(column);
        SetCurrentPath(scope, isDirectory: true);
        StatusText = $"検索中… ({scope})";

        await RunSearchAsync(column, scope, query, ShowHidden, CurrentComparison, cts.Token);
    }

    /// <summary>実行中の検索を中断する (表示済みの結果は残す)。</summary>
    public void CancelSearch() => _searchCts?.Cancel();

    /// <summary>検索結果の列を閉じる (Esc)。</summary>
    public void CloseSearch()
    {
        _searchCts?.Cancel();
        if (ActiveTab is not { } tab)
            return;
        for (var i = tab.Columns.Count - 1; i >= 0; i--)
        {
            if (!tab.Columns[i].IsSearch)
                continue;
            var column = tab.Columns[i];
            tab.Columns.RemoveAt(i);
            column.Dispose();
        }
    }

    private Task RunSearchAsync(ColumnModel column, string scope, string query,
        bool showHidden, Comparison<FileSystemItem> comparison, CancellationToken ct)
    {
        // * ? を含むならワイルドカード一致、含まなければ部分一致
        Func<string, bool> matches = query.IndexOfAny(new[] { '*', '?' }) >= 0
            ? name => FileSystemName.MatchesSimpleExpression(query, name, ignoreCase: true)
            : name => name.Contains(query, StringComparison.OrdinalIgnoreCase);
        return RunScanAsync(column, new[] { new SavedSearch.Scope(scope, Recursive: true) }, Array.Empty<string>(),
            (name, _) => matches(name), MaxSearchResults, showHidden, comparison, ct);
    }

    /// <summary>保存された検索の結果の上限。写真フォルダのように数千件が普通にあるので、
    /// 名前検索 (MaxSearchResults) より多めに取る。</summary>
    private const int MaxSavedSearchResults = 5000;

    private Task RunSavedSearchAsync(ColumnModel column, SavedSearch search,
        bool showHidden, Comparison<FileSystemItem> comparison, CancellationToken ct)
        => RunScanAsync(column, search.Includes, search.Excludes, search.Matches,
            MaxSavedSearchResults, showHidden, comparison, ct);

    /// <summary>範囲のフォルダ以下を走査し、条件に合う項目を検索列へ流し込む。</summary>
    private async Task RunScanAsync(ColumnModel column, IReadOnlyList<SavedSearch.Scope> scopes,
        IReadOnlyList<string> excludes, Func<string, bool, bool> matches, int maxResults,
        bool showHidden, Comparison<FileSystemItem> comparison, CancellationToken ct)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        var found = new List<FileSystemItem>();
        var capped = false;

        // 走査スレッドから随時呼ぶ: ソート済みの写しを作って UI スレッドへまとめて反映
        // (1 件ずつ通知するとレイアウト評価が件数ぶん走って重いため)
        void Flush(bool done)
        {
            var snapshot = found.ToList();
            snapshot.Sort(comparison);
            var count = found.Count;
            dispatcher.BeginInvoke(() =>
            {
                if (ct.IsCancellationRequested)
                    return;
                column.Items.ReplaceAll(snapshot);
                StatusText = done
                    ? capped ? $"検索結果: {count} 件 (上限 {maxResults} 件で打ち切り)" : $"検索結果: {count} 件"
                    : $"検索中… {count} 件";
            });
        }

        var excluded = new HashSet<string>(excludes.Select(Path.TrimEndingDirectorySeparator),
            StringComparer.OrdinalIgnoreCase);

        column.IsSearchRunning = true;
        try
        {
            await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                // 幅優先: 浅い階層 (現在地に近い結果) から先に出す。範囲が重なっていても二度は数えない
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<(string Dir, string Root, bool Recursive)>();
                foreach (var scope in scopes)
                    if (!excluded.Contains(Path.TrimEndingDirectorySeparator(scope.Path)))
                        queue.Enqueue((scope.Path, scope.Path, scope.Recursive));
                while (queue.Count > 0 && !capped)
                {
                    ct.ThrowIfCancellationRequested();
                    var (current, root, recursive) = queue.Dequeue();
                    if (!visited.Add(Path.TrimEndingDirectorySeparator(current)))
                        continue;
                    try
                    {
                        var dir = new DirectoryInfo(current);
                        foreach (var info in dir.EnumerateFileSystemInfos())
                        {
                            ct.ThrowIfCancellationRequested();
                            var attrs = info.Attributes;
                            if (!showHidden && (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                                continue;
                            var isDir = (attrs & FileAttributes.Directory) != 0;
                            if (isDir && excluded.Contains(info.FullName))
                                continue;
                            // ジャンクション / シンボリックリンクは無限ループ回避のため辿らない。
                            // クラウドのプレースホルダー (OneDrive・接続デバイス等) も再解析ポイントだが、
                            // こちらはリンクではないので辿る
                            if (isDir && recursive && !IsLinkOrJunction(info, attrs))
                                queue.Enqueue((info.FullName, root, true));
                            if (!matches(info.Name, isDir))
                                continue;

                            var parent = Path.GetDirectoryName(info.FullName);
                            found.Add(new FileSystemItem
                            {
                                Path = info.FullName,
                                Name = info.Name,
                                IsDirectory = isDir,
                                Cloud = FileSystemItem.GetCloudStatus(attrs),
                                Modified = info.LastWriteTime,
                                Size = isDir ? 0 : (info as FileInfo)?.Length ?? 0,
                                Location = parent is not null
                                    && !string.Equals(Path.TrimEndingDirectorySeparator(parent),
                                        Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
                                    ? Path.GetRelativePath(root, parent) : null,
                            });
                            if (found.Count >= maxResults)
                            {
                                capped = true;
                                break;
                            }
                            if (sw.ElapsedMilliseconds >= 150)
                            {
                                Flush(done: false);
                                sw.Restart();
                            }
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        // アクセスできないフォルダは無視して続行
                    }
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            column.IsSearchRunning = false;
        }
        Flush(done: true);
    }

    /// <summary>シンボリックリンク / ジャンクションか (クラウドのプレースホルダー等、
    /// リンクではない再解析ポイントは false)。判別できなければ辿らない側に倒す。</summary>
    private static bool IsLinkOrJunction(FileSystemInfo info, FileAttributes attrs)
    {
        if ((attrs & FileAttributes.ReparsePoint) == 0)
            return false;
        try
        {
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    // ---- タブ・ナビゲーション ----

    /// <summary>ナビゲーション中 (列の作り直し・選択の復帰) の入れ子カウント。
    /// 0 でない間は列の選択変更をユーザー操作として扱わない。</summary>
    private int _navigatingDepth;

    private bool Navigating => _navigatingDepth > 0;

    /// <summary>タブが 0 個になったとき (最後のタブを閉じた / 引き剝がした) に発火。</summary>
    public event Action? TabsEmptied;

    public MainViewModel() : this(true) { }

    public MainViewModel(bool createInitialTab)
    {
        // パンくずは CurrentPath が変わったときだけ作り直すので、初期値 (ホーム) の分をここで一度作る。
        // これが無いと、復元後の最初のタブがホームのままだとアドレスバーが空のままになる。
        BuildBreadcrumbs();
        if (createInitialTab)
            _ = InitTabsAsync();
    }

    /// <summary>起動用ファクトリ: セッション復元に加えて、フォルダーの既定アプリとして
    /// 起動されたときの引数フォルダーをアクティブなタブで開く。</summary>
    public static MainViewModel CreateForStartup(string? initialFolder)
    {
        var vm = new MainViewModel(createInitialTab: false);
        _ = vm.InitTabsAsync(initialFolder);
        return vm;
    }

    /// <summary>起動時: 前回のセッションがあれば復元し、なければホームタブを開く。
    /// extraFolder があればそれを最後に開いてアクティブにする (引数起動)。</summary>
    private async Task InitTabsAsync(string? extraFolder = null)
    {
        // NAS 等は Windows のログオン時再接続に失敗して「切断中」で残ることがある。
        // 起動のたびに裏で繋ぎ直しておく (タブの復元は待たせない)。
        _ = ReconnectNetworkDrivesAsync();

        if (_settings.RestoreSession && LoadSession() is { Count: > 0 } session)
        {
            // 列構成の組み立て (フォルダの存在確認を含む) はまとめて別スレッドで行い、
            // ネットワークパスの応答待ちで UI を止めない。ホーム列の顔ぶれは
            // 道筋を足すタブがあるときだけ、全タブ分まとめて 1 回作る。
            var plans = await Task.Run(() =>
            {
                var anchors = session.Any(t => t.Columns.FirstOrDefault() is { GroupId: null, Path: not null })
                    ? RouteAnchors()
                    : new List<string>();
                return session.Select(t => PlanRestore(t.Columns, anchors)).ToList();
            });
            foreach (var plan in plans)
                RestoreTab(plan);
            if (Tabs.Count > 0)
            {
                var active = _settings.SessionActiveTab;
                ActiveTab = Tabs[active >= 0 && active < Tabs.Count ? active : 0];
                // 引数フォルダーは復元タブの後ろに追加して前面に出す
                // (復元をやめて単独で開くと、閉じたとき SaveSession が元のセッションを潰すため)
                if (extraFolder is not null)
                    await NewTabAsync(extraFolder);
                return;
            }
        }
        await NewTabAsync(extraFolder);
    }

    /// <summary>保存済みセッションを取り出す。旧形式 (最深フォルダだけの SessionTabs) は
    /// 1 列のタブとして読み、復元時にホームからの道筋を足してもらう。</summary>
    private List<SessionTab> LoadSession()
    {
        if (_settings.SessionColumns.Count > 0)
            return _settings.SessionColumns;
        return _settings.SessionTabs
            .Select(p => new SessionTab { Columns = { new SessionColumn { Path = p } } })
            .ToList();
    }

    /// <summary>現在のタブ構成 (各タブの全列と選択) を設定へ保存する。最後のウィンドウを閉じるときに呼ぶ。</summary>
    public void SaveSession()
    {
        _settings.SessionColumns = Tabs.Select(CaptureTab).ToList();
        _settings.SessionTabs.Clear(); // 旧形式は移行済み (残すと次回どちらを読むか曖昧になる)
        _settings.SessionActiveTab = ActiveTab is null ? 0 : Tabs.IndexOf(ActiveTab);
        _settings.Save();
    }

    /// <summary>タブの列構成を控える。検索列は結果を再現できないので、そこで打ち切る。</summary>
    private static SessionTab CaptureTab(TabModel tab)
    {
        if (tab.Pending is { } pending)
            return new SessionTab { Columns = pending }; // 一度も開かなかったタブは控えを持ち越す

        var state = new SessionTab();
        foreach (var column in tab.Columns)
        {
            if (column.IsSearch)
                break;
            state.Columns.Add(new SessionColumn
            {
                Path = column.Path,
                GroupId = column.GroupId,
                Selected = column.SelectedItem?.Path,
            });
        }
        return state;
    }

    /// <summary>控えた列構成をタブ 1 枚として並べる (開ける列が 1 つも無ければ何もしない)。
    /// 列の読み込みは初めてアクティブになるまで先送りするので、タブが何枚あっても
    /// 起動時に実際に読むのは 1 タブ分だけで済む。</summary>
    private TabModel? RestoreTab(List<SessionColumn> plan) => RestoreTab(plan, Tabs.Count);

    /// <summary>控えた列構成をタブ 1 枚として指定位置に差し込む (末尾以外は
    /// 「閉じたタブを開き直す」用)。開ける列が 1 つも無ければ何もしない。</summary>
    private TabModel? RestoreTab(List<SessionColumn> plan, int index)
    {
        if (plan.Count == 0)
            return null;
        var last = plan[^1];
        var tab = new TabModel
        {
            Pending = plan,
            Title = TrimTitle(ColumnTitle(last.Path, last.GroupId)),
            History = { plan[0].Path },
            HistoryIndex = 0,
        };
        Tabs.Insert(Math.Clamp(index, 0, Tabs.Count), tab);
        return tab;
    }

    /// <summary>復元待ちのタブを実際の列へ展開する (初めてアクティブになったとき)。</summary>
    private async Task MaterializeAsync(TabModel tab)
    {
        if (tab.Pending is not { } plan)
            return;
        tab.Pending = null; // 再入防止 (最初の await より前に落とす)
        _navigatingDepth++;
        try
        {
            foreach (var desc in plan)
            {
                // 控えてから開くまでの間に消えたグループ / フォルダがあれば、そこで打ち切る
                ColumnModel column;
                if (desc.GroupId is { } gid)
                {
                    if (_settings.FindGroup(gid) is not { } group)
                        break;
                    column = new ColumnModel(group);
                }
                else
                {
                    if (desc.Path is { } path && await IsFolderGoneAsync(path))
                        break;
                    column = new ColumnModel(desc.Path);
                }
                tab.Columns.Add(column);
                await column.LoadAsync(ShowHidden, CurrentComparison);
                // 選択の復帰はナビゲーション中なので、右の列を開き直す動きにはならない
                if (desc.Selected is { } key)
                    column.SelectedItem = column.Items.FirstOrDefault(
                        i => string.Equals(i.Path, key, StringComparison.OrdinalIgnoreCase));
            }
            if (tab.Columns.Count == 0)
            {
                var home = new ColumnModel((string?)null); // 全部消えていた: ホーム列で開く
                tab.Columns.Add(home);
                await home.LoadAsync(ShowHidden, CurrentComparison);
            }
            var opened = tab.Columns[^1];
            tab.Title = TrimTitle(ColumnTitle(opened.Path, opened.GroupId));
            if (ActiveTab == tab)
            {
                UpdateCurrentPathFromTab();
                UpdateStatus(opened);
                RaiseNavState();
            }
        }
        finally
        {
            _navigatingDepth--;
        }
    }

    /// <summary>列の見出し (ホーム列 / グループ列 / フォルダ列)。</summary>
    private string ColumnTitle(string? path, string? groupId)
    {
        if (groupId is { } gid)
            return _settings.FindGroup(gid)?.Name ?? "グループ";
        if (path is null)
            return "ホーム";
        return Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path;
    }

    // ---- セッション復元: 控えた列構成を「実際に開ける列の並び」へ組み直す ----

    /// <summary>控えた列構成を復元用に整える。
    /// (1) 消えたフォルダ・グループがあればそこで打ち切る (先頭が消えていればタブごと捨てる)、
    /// (2) フォルダ列から始まるタブ (お気に入り・グループ・引数から直接開いたタブ) には
    ///     ホームからそこまでの道筋を前に足して、対象フォルダを最右の列にする、
    /// (3) 選択が控えられていない列は、その右隣を開いた項目で補う (旧形式からの移行用)。</summary>
    private List<SessionColumn> PlanRestore(List<SessionColumn> saved, List<string> anchors)
    {
        var plan = TrimToAvailable(saved);
        if (plan.Count == 0)
            return plan;

        if (plan[0] is { GroupId: null, Path: { } root })
        {
            var route = HomeRouteTo(root, anchors);
            route[^1].Selected = plan[0].Selected;
            plan.RemoveAt(0);
            plan.InsertRange(0, route);
        }

        for (var i = 0; i < plan.Count - 1; i++)
            plan[i].Selected ??= KeyOf(plan[i + 1]);
        return plan;

        static string? KeyOf(SessionColumn column)
            => column.GroupId is { } gid ? "group:" + gid : column.Path;
    }

    /// <summary>控えた列構成のうち、いま開けるところまでを取り出す
    /// (消えたフォルダ・グループから右は再現できないので切り落とす)。</summary>
    private List<SessionColumn> TrimToAvailable(List<SessionColumn> saved)
    {
        var plan = new List<SessionColumn>();
        foreach (var desc in saved)
        {
            if (desc.GroupId is { } gid && _settings.FindGroup(gid) is null)
                break;
            if (desc is { GroupId: null, Path: { } path } && IsFolderGone(path))
                break;
            plan.Add(new SessionColumn { Path = desc.Path, GroupId = desc.GroupId, Selected = desc.Selected });
        }
        return plan;
    }

    /// <summary>ホームからの道筋の起点候補 (お気に入り・既知フォルダ・クラウドルート)。
    /// ホーム列に並ぶ顔ぶれと揃えているが、ドライブは Path.GetPathRoot で代用できるので省く
    /// (DriveInfo の列挙はネットワークドライブがあると遅い)。存在確認もしない —
    /// 起点は復元対象フォルダの先祖なので、対象さえ在れば必ず在る。</summary>
    private List<string> RouteAnchors()
    {
        var anchors = new List<string>(_settings.Favorites);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        anchors.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        anchors.Add(System.IO.Path.Combine(profile, "Downloads"));
        anchors.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        anchors.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        anchors.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        anchors.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        anchors.Add(profile);
        foreach (var cloud in CloudSync.Roots)
            anchors.Add(cloud.Path);
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            anchors.Add(Environment.GetEnvironmentVariable(name) ?? "");
        return anchors.Where(a => !string.IsNullOrEmpty(a)).ToList();
    }

    /// <summary>ホームから path までの列構成を作る (末尾が path 自身の列)。
    /// ホーム列に並ぶフォルダやグループのメンバーのうち path の最も深い先祖 (自身を含む) を
    /// 起点にするので、ダウンロードなら「ホーム → ダウンロード」の 2 列で済む。</summary>
    private List<SessionColumn> HomeRouteTo(string path, List<string> anchors)
    {
        var route = new List<SessionColumn> { new() }; // ホーム列

        var anchor = anchors
            .Where(entry => IsAncestorOrSelf(entry, path))
            .OrderByDescending(entry => entry.TrimEnd('\\').Length)
            .FirstOrDefault();

        // グループのメンバーにもっと深い起点があればそちらを使う (ホーム → グループ → フォルダ)
        if (FindGroupAnchor(path, anchor?.TrimEnd('\\').Length ?? 0) is { } via)
        {
            foreach (var group in via.Groups)
                route.Add(new SessionColumn { GroupId = group.Id });
            anchor = via.Path;
        }

        // ホームから辿れないパス (UNC など) はドライブ / 共有のルートから 1 階層ずつ並べる
        anchor ??= Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(anchor))
            return new List<SessionColumn> { new() { Path = path } };

        route.Add(new SessionColumn { Path = anchor });
        foreach (var step in Descend(anchor, path))
            route.Add(new SessionColumn { Path = step });
        return route;
    }

    /// <summary>グループのメンバーから path の先祖 (自身を含む) で最も深いものを探し、
    /// そこへ至るグループの入れ子と一緒に返す。minDepth 以下の浅い候補は無視する。
    /// RouteAnchors と同じ理由で存在確認はしない。</summary>
    private (List<FavoriteGroup> Groups, string Path)? FindGroupAnchor(string path, int minDepth)
    {
        (List<FavoriteGroup>, string)? best = null;
        var trail = new List<FavoriteGroup>();
        Walk(_settings.Groups);
        return best;

        void Walk(List<FavoriteGroup> list)
        {
            foreach (var group in list)
            {
                trail.Add(group);
                foreach (var member in group.Paths)
                {
                    var depth = member.TrimEnd('\\').Length;
                    if (depth > minDepth && IsAncestorOrSelf(member, path))
                    {
                        minDepth = depth;
                        best = (new List<FavoriteGroup>(trail), member);
                    }
                }
                Walk(group.Subgroups);
                trail.RemoveAt(trail.Count - 1);
            }
        }
    }

    /// <summary>anchor の 1 つ下から path までのフォルダを順に返す (anchor 自身は含まない)。</summary>
    private static List<string> Descend(string anchor, string path)
    {
        var steps = new List<string>();
        var end = anchor.TrimEnd('\\');
        for (var dir = path; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (string.Equals(dir.TrimEnd('\\'), end, StringComparison.OrdinalIgnoreCase))
                break;
            steps.Add(dir);
        }
        steps.Reverse();
        return steps;
    }

    /// <summary>ancestor が path と同じか、その先祖フォルダか。</summary>
    private static bool IsAncestorOrSelf(string ancestor, string path)
    {
        var a = ancestor.TrimEnd('\\');
        var p = path.TrimEnd('\\');
        if (a.Length > p.Length || !p.StartsWith(a, StringComparison.OrdinalIgnoreCase))
            return false;
        return p.Length == a.Length || p[a.Length] == '\\';
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

    // ---- 閉じたタブの復元 (Ctrl+Shift+T) ----

    /// <summary>閉じたタブの控え (新しいものが末尾)。全ウィンドウで共有するので、
    /// 最後のタブを閉じてウィンドウごと消えた場合も別のウィンドウで開き直せる。</summary>
    private static readonly List<(List<SessionColumn> Columns, int Index)> _closedTabs = new();

    private const int ClosedTabLimit = 10;

    /// <summary>閉じたタブを「列の並び (選択込み) + タブ列での位置」で控える
    /// (セッション復元と同じ形なので、開き直すと閉じたときの列構成がそのまま戻る)。</summary>
    private void RememberClosedTab(TabModel tab, int index)
    {
        var columns = CaptureTab(tab).Columns;
        if (columns.Count == 0)
            return; // 検索列しか無いタブ: 再現できるものが無いので控えない
        _closedTabs.Add((columns, index));
        if (_closedTabs.Count > ClosedTabLimit)
            _closedTabs.RemoveAt(0);
    }

    /// <summary>直前に閉じたタブをこのウィンドウの元の位置に開き直す (別ウィンドウで
    /// 閉じたものは位置が丸められる)。消えたフォルダから右は落とし、起点ごと消えていた
    /// 控えは捨ててさらに前の控えへさかのぼる (控えが尽きたら何もしない)。</summary>
    public async Task ReopenClosedTabAsync()
    {
        while (_closedTabs.Count > 0)
        {
            var (columns, index) = _closedTabs[^1];
            _closedTabs.RemoveAt(_closedTabs.Count - 1);
            // 存在確認は裏で (届かない共有で UI を止めない)
            var plan = await Task.Run(() => TrimToAvailable(columns));
            if (RestoreTab(plan, index) is not { } tab)
                continue;
            ActiveTab = tab; // 列の読み込みはセッション復元と同じく展開に任せる
            return;
        }
    }

    public void CloseTab(TabModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;
        RememberClosedTab(tab, index);
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
        _navigatingDepth++;
        try
        {
            TrimColumns(tab, 0);
            var column = new ColumnModel(path);
            tab.Columns.Add(column);
            await column.LoadAsync(ShowHidden, CurrentComparison);
            tab.Title = path is null ? "ホーム" : TrimTitle(Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path);
            SetCurrentPath(path ?? "", isDirectory: true);
            UpdateStatus(column);
        }
        finally
        {
            _navigatingDepth--;
        }
    }

    public async Task NavigateToPathAsync(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (ActiveTab is null)
            return;
        if (Reachability.IsNetworkPath(path) && !await Reachability.IsReachableAsync(path))
        {
            StatusText = $"接続先に届きません: {path}";
            return;
        }
        if (!await Task.Run(() => Directory.Exists(path)))
        {
            StatusText = $"フォルダが見つかりません: {path}";
            return;
        }
        await ResetTabAsync(ActiveTab, path);
        PushHistory(ActiveTab, path);
    }

    /// <summary>全タブの全列をその場で再読込する (隠しファイル切替など)。開いている階層と選択は維持。</summary>
    private async Task ReloadAllColumnsAsync()
    {
        _navigatingDepth++;
        try
        {
            foreach (var tab in Tabs)
            {
                foreach (var column in tab.Columns)
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
            _navigatingDepth--;
        }
    }

    /// <summary>アクティブなタブを再読み込みする (F5)。開いている階層と選択はそのまま保ち、
    /// 消えたフォルダの列だけ畳む。検索列は結果を復元できないので同じ検索語で走査し直す。</summary>
    public async Task ReloadActiveTabAsync()
    {
        // 復元待ちのタブは列がまだ無い (MaterializeAsync が作る) ので触らない
        if (_reloading || ActiveTab is not { Pending: null } tab)
            return;

        // F5 の長押し (キーリピート) で再読み込みが重なると、同じ列への
        // 列挙とアイコン読み込みが無駄に走るので 1 本に絞る
        _reloading = true;
        try
        {
            await ReloadTabAsync(tab);
        }
        finally
        {
            _reloading = false;
        }
    }

    private bool _reloading;

    /// <summary>再読み込みの実行中。ウィンドウ側が合図の演出を二重に出さないために見る。</summary>
    public bool IsReloading => _reloading;

    private async Task ReloadTabAsync(TabModel tab)
    {
        // 消えたフォルダは読み直せないため、その列から先を畳む。
        // 先頭列ごと消えていたら、残っている一番近い親 (無ければホーム) から作り直す
        for (var i = 0; i < tab.Columns.Count; i++)
        {
            if (tab.Columns[i].Path is not { } path || !await IsFolderGoneAsync(path))
                continue;
            if (i > 0)
            {
                TrimColumns(tab, i);
                break;
            }
            var fallback = await Task.Run(() => NearestExistingAncestor(path));
            await ResetTabAsync(tab, fallback);
            PushHistory(tab, fallback);
            StatusText = $"フォルダが見つかりません: {path}";
            return;
        }

        _navigatingDepth++;
        try
        {
            foreach (var column in tab.Columns.ToList())
            {
                if (column.IsSearch)
                    continue; // 走査に時間がかかるので、他の列を戻してから下でやり直す
                var selectedPath = column.SelectedItem?.Path;
                await column.LoadAsync(ShowHidden, CurrentComparison);
                if (selectedPath is not null)
                    column.SelectedItem = column.Items.FirstOrDefault(
                        i => string.Equals(i.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _navigatingDepth--;
        }

        UpdateCurrentPathFromTab();
        if (tab.Columns.LastOrDefault() is { IsSearch: false } last)
        {
            UpdateStatus(last);
            // 中身が変わっていないと件数も同じで動きが見えないので、実行したこと自体を言う
            // (Error のときは UpdateStatus がその内容を出しているので上書きしない)
            if (last.Error is null)
                StatusText = "再読み込みしました — " + StatusText;
        }

        // 検索列は最後に (件数の途中経過を出すので、ステータスの上書き合戦にならない)
        if (tab.Columns.LastOrDefault(c => c.IsSearch) is { } search)
            await RerunSearchAsync(tab, search);
    }

    /// <summary>検索結果の列を同じ検索語で走査し直す。起点は結果列より左の一番近いフォルダ列。</summary>
    private async Task RerunSearchAsync(TabModel tab, ColumnModel column)
    {
        // 保存された検索は自分の範囲を持っているので、左の列とは関係なくそのままやり直す
        if (column.SavedSearch is { } saved)
        {
            var savedCts = StartSearch(column);
            StatusText = $"検索中… ({saved.Name})";
            await RunSavedSearchAsync(column, saved, ShowHidden, CurrentComparison, savedCts.Token);
            return;
        }

        string? scope = null;
        for (var i = tab.Columns.IndexOf(column) - 1; i >= 0; i--)
            if (tab.Columns[i].Path is { } path)
            {
                scope = path;
                break;
            }
        if (string.IsNullOrEmpty(column.SearchQuery) || scope is null || !await DirectoryExistsAsync(scope))
            return;

        var cts = StartSearch(column);
        StatusText = $"検索中… ({scope})";
        await RunSearchAsync(column, scope, column.SearchQuery, ShowHidden, CurrentComparison, cts.Token);
    }

    /// <summary>実行中の検索を止めて、この列の走査用のキャンセルを新しく用意する。</summary>
    private CancellationTokenSource StartSearch(ColumnModel column)
    {
        _searchCts?.Cancel();
        column.SearchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        column.SearchCts = cts; // 列が閉じられたら走査も止まる
        return cts;
    }

    /// <summary>実在する一番近い親フォルダ。どこまでたどっても無ければ null (= ホーム)。
    /// 届かない共有の存在確認は待つので、裏スレッドから呼ぶこと。</summary>
    private static string? NearestExistingAncestor(string path)
    {
        if (Reachability.IsKnownUnreachable(path))
            return null;
        for (var dir = Path.GetDirectoryName(path); dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(dir))
                return dir;
        return null;
    }

    /// <summary>列で項目が選択されたときの中核ロジック</summary>
    public async Task OnItemSelectedAsync(ColumnModel column, FileSystemItem item)
    {
        // IsRefreshing: 外部変更の再読み込みによる選択復帰はナビゲーションとして扱わない
        if (Navigating || column.IsRefreshing || ActiveTab is null)
            return;

        var tab = ActiveTab;
        var index = tab.Columns.IndexOf(column);
        if (index < 0)
            return;

        // 選択列より右の列を畳む
        TrimColumns(tab, index + 1);
        var selection = ++_selectionVersion;

        // グループ見出し: 中身 (サブグループ＋フォルダ) を次の列に展開する。
        // CurrentPath は据え置き (直前のフォルダを「現在のフォルダ」として追加できるように)。
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

        // ポータブルデバイス (iPhone 等): 実パスが無いので列には展開できない。
        // CurrentPath は据え置きにして、開く操作はエクスプローラーに任せる。
        if (item.IsShellDevice)
        {
            StatusText = $"{item.Name} — ポータブル デバイス (Enter / ダブルクリックでエクスプローラーで開く)";
            return;
        }

        // 切断中のネットワークドライブ: そのまま列挙しても「見つかりません」になるか、
        // 届かない接続先を待って数十秒止まるだけ。存在確認も同じだけ待たされるので
        // ここでは触らず、明示操作 (Enter / ダブルクリック) のときに裏で繋ぎ直す。
        if (item.IsDisconnectedDrive)
        {
            StatusText = $"{item.Name} — 切断中 (Enter / ダブルクリックで接続)";
            return;
        }

        // ショートカット / 保存された検索: 行き先がフォルダか保存された検索なら、
        // ファイルとして扱わずに中身を次の列に出す (調べるのは裏で、通常は一瞬)
        if (item.MayOpenAsColumn && await item.GetLinkTargetAsync() is { } link)
        {
            // 調べている間に別の項目が選ばれていたら、そちらに任せる
            if (selection != _selectionVersion || ActiveTab != tab || tab.Columns.IndexOf(column) != index)
                return;
            // 信頼していないサーバーを指すものは、選んだだけでは繋ぎに行かない
            if (link.Kind == LinkTargetKind.NeedsConfirmation)
            {
                SetCurrentPath(item.Path, isDirectory: false);
                tab.Title = TrimTitle(Path.GetFileName(Path.GetDirectoryName(item.Path)) ?? item.Name);
                var where = NetworkTrust.HostOf(link.Path) is { } host ? $"ネットワーク上の場所 ({host})" : "ネットワーク上の場所";
                StatusText = $"{item.Name} — {where} を指しています (Enter / ダブルクリックで開く)";
                PreviewFollow?.Invoke(item);
                return;
            }
            await OpenLinkColumnAsync(tab, item, link);
            PreviewFollow?.Invoke(item);
            return;
        }
        if (selection != _selectionVersion)
            return;

        SetCurrentPath(item.Path, item.IsDirectory);

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
            // 検索結果はどこのファイルか分かるようフルパスを見せる
            StatusText = column.IsSearch
                ? $"{item.Path}  ({FormatSize(item.Size)})"
                : $"{item.Name}  ({FormatSize(item.Size)})";
        }

        // プレビュー (Quick Look) が開いていれば選択に追従させる。
        // 実際の追従は View 側 (MainWindow) が SelectionChanged で行う。
        PreviewFollow?.Invoke(item);
    }

    /// <summary>列の選択が変わるたびに進む番号。ショートカットの行き先を調べている間に
    /// 別の項目が選ばれたかを見分ける。</summary>
    private int _selectionVersion;

    /// <summary>ショートカットの行き先を次の列に出す。フォルダならその中身、
    /// 保存された検索ならその検索結果 (現在のフォルダはショートカットのある場所のまま)。</summary>
    private async Task OpenLinkColumnAsync(TabModel tab, FileSystemItem item, LinkTarget link)
    {
        tab.Title = TrimTitle(Path.GetFileNameWithoutExtension(item.Name));
        if (link.Kind == LinkTargetKind.Folder)
        {
            SetCurrentPath(link.Path, isDirectory: true);
            var next = new ColumnModel(link.Path);
            tab.Columns.Add(next);
            await next.LoadAsync(ShowHidden, CurrentComparison);
            UpdateStatus(next);
            return;
        }

        SetCurrentPath(item.Path, isDirectory: false);
        var search = link.Search!;
        var column = ColumnModel.CreateSavedSearch(search);
        var cts = StartSearch(column);
        tab.Columns.Add(column);
        StatusText = $"検索中… ({search.Name})";
        await RunSavedSearchAsync(column, search, ShowHidden, CurrentComparison, cts.Token);
    }

    /// <summary>選択済みの項目 (奥の列を開いている途中のフォルダ) を再クリックしたとき:
    /// 選択が変わらないので SelectionChanged が来ない。その項目を選び直した扱いにして、
    /// 奥の列を畳み、その中身の列を末尾 (= 現在のフォルダ) にする。</summary>
    public async Task ReselectAsync(ColumnModel column, FileSystemItem item)
    {
        if (Navigating || ActiveTab is not { } tab)
            return;
        var index = tab.Columns.IndexOf(column);
        if (index < 0)
            return;
        // 選んだまま行き先の列が出ていないショートカット (前回終了時に選ばれていたもの等) は開く
        if (item.MayOpenAsColumn && index + 1 == tab.Columns.Count)
        {
            await OnItemSelectedAsync(column, item);
            return;
        }
        // 畳む対象が無い (すぐ右が中身の列で、そこで何も選んでいない) なら作り直さない
        if (index + 1 >= tab.Columns.Count
            || (index + 2 == tab.Columns.Count && tab.Columns[index + 1].SelectedItem is null))
            return;
        await OnItemSelectedAsync(column, item);
    }

    /// <summary>Quick Look が開いているとき、選択変更を View に伝えて追従させる。</summary>
    public Action<FileSystemItem>? PreviewFollow { get; set; }

    /// <summary>複数選択時: 子の列を畳んで件数を表示する。</summary>
    public void OnMultiSelect(ColumnModel column, int count)
    {
        if (Navigating || column.IsRefreshing || ActiveTab is not { } tab)
            return;
        var index = tab.Columns.IndexOf(column);
        if (index < 0)
            return;
        TrimColumns(tab, index + 1);
        _selectionVersion++; // 調べかけのショートカットが後から列を開かないように
        StatusText = $"{count} 個を選択";
    }

    public void OpenItem(FileSystemItem? item)
    {
        if (item is null)
            return;
        try
        {
            // デバイスはシェル解析名 ("::{...}") なので ShellExecute では開けない
            if (item.IsShellDevice)
            {
                if (!ComputerFolder.OpenInExplorer(item.Path))
                    StatusText = $"{item.Name} を開けませんでした (機器がロックされている可能性があります)";
                return;
            }
            // 切断中のネットワークドライブは、繋ぎ直してから開く
            // (存在確認も接続先を待つので UI スレッドではしない)
            if (item.IsDisconnectedDrive)
            {
                _ = ConnectDriveAsync(item);
                return;
            }
            // フォルダ / 保存された検索を指すショートカットは選択した時点で次の列に出ているので、
            // 別ウィンドウでは開かない (行き先を調べている途中なら、その結果で決める)
            if (item.MayOpenAsColumn)
            {
                _ = OpenShortcutAsync(item);
                return;
            }
            // ShellExecute は UI スレッドで対象に問い合わせるので、届かないと分かっている共有は開きに行かない
            if (Reachability.IsKnownUnreachable(item.Path))
            {
                StatusText = $"接続先に届きません: {item.Path}";
                return;
            }
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "開けませんでした: " + ex.Message;
        }
    }

    /// <summary>列で開けないショートカット (アプリ・ファイルを指すもの) だけを普通に開く。
    /// 列で開けるもので行き先の列がまだ出ていなければ (前回終了時に選ばれていたもの等)、ここで出す。
    /// 信頼していないサーバーを指すものも、この明示操作のときだけ繋ぎに行って開く。</summary>
    private async Task OpenShortcutAsync(FileSystemItem item)
    {
        var link = await item.GetLinkTargetAsync();
        if (link is { Kind: LinkTargetKind.NeedsConfirmation })
        {
            StatusText = $"{item.Name} を開いています…";
            var location = link.Path;
            link = await Task.Run(() => Shortcut.Classify(item.Path, allowUntrusted: true));
            // 届かなかった場所をシェルに開かせると、UI スレッドで接続待ちになる
            if (link is null && Reachability.IsKnownUnreachable(location))
            {
                StatusText = $"接続先に届きません: {Reachability.ShareRoot(location) ?? location}";
                return;
            }
            if (link is not null)
            {
                if (ActiveTab is { } tab && tab.Columns.LastOrDefault() is { } last && last.SelectedItem == item)
                {
                    _selectionVersion++;
                    await OpenLinkColumnAsync(tab, item, link);
                }
                return;
            }
        }
        else if (link is not null)
        {
            if (ActiveTab?.Columns.LastOrDefault() is { } last && last.SelectedItem == item)
                await OnItemSelectedAsync(last, item);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "開けませんでした: " + ex.Message;
        }
    }

    /// <summary>切断中のネットワークドライブに繋ぎ直し、成功したらそのまま開く。
    /// 資格情報が必要なら Windows 自身のダイアログが出る (パスワードはアプリでは扱わない)。</summary>
    private async Task ConnectDriveAsync(FileSystemItem item)
    {
        StatusText = $"{item.Name} に接続しています…";
        var error = await Task.Run(() => ComputerFolder.ReconnectDrive(item.Path, interactive: true));
        if (error is not null)
        {
            StatusText = $"{item.Name} に接続できませんでした: {error}";
            return;
        }
        StatusText = $"{item.Name} に接続しました";
        // 繋がったのでホーム列では普通のドライブ (容量ゲージ付き) として並び直す
        await RefreshHomeColumnsAsync();
        await NavigateToPathAsync(item.Path);
    }

    public void RevealInExplorer(FileSystemItem? item)
    {
        if (item is null)
            return;
        // デバイスには親フォルダで選択して見せる相手がいないので、そのまま開く
        if (item.IsShellDevice)
        {
            ComputerFolder.OpenInExplorer(item.Path);
            return;
        }
        Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
    }

    public void CopyPath(FileSystemItem? item)
    {
        if (item is null)
            return;
        StatusText = ClipboardOps.SetText(item.Path)
            ? $"パスをコピーしました: {item.Path}"
            : ClipboardBusyMessage();
    }

    private static string ClipboardBusyMessage()
        => ClipboardOps.LastBlocker is { } who
            ? $"クリップボードを使用できませんでした ({who} が使用中)"
            : "クリップボードを使用できませんでした";

    // ---- クリップボード (コピー / 切り取り / 貼り付け) ・削除・新規フォルダ ----

    /// <summary>選択ファイル群をクリップボードへ (エクスプローラー互換)。cut=true で切り取り。</summary>
    public void CopyToClipboard(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0)
            return;
        if (!ClipboardOps.SetFiles(paths, cut))
        {
            StatusText = ClipboardBusyMessage();
            return;
        }
        ClipboardMarks.Set(paths, cut);
        StatusText = cut ? $"切り取り: {paths.Count} 個" : $"コピー: {paths.Count} 個";
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
        if (!await EnsureReachableAsync(files.Append(targetDir)))
            return;
        var owner = FileOps.ActiveWindow();
        IReadOnlyCollection<string> affected;
        string? error;
        await _fileOpGate.WaitAsync();
        try
        {
            StatusText = cut ? "移動しています…" : "コピーしています…";
            List<(string Source, string Dest)> performed;
            (affected, error, performed) = await FileWorker.RunAsync(owner, () =>
            {
                var a = FileOps.Transfer(files, targetDir, copy: !cut, out var e, out var p);
                return (a, e, p);
            });
            PushTransferUndo(performed, copy: !cut);
        }
        finally
        {
            _fileOpGate.Release();
        }
        if (cut)
        {
            ClipboardOps.ClearAfterMove();
            ClipboardMarks.Clear();
        }
        StatusText = error ?? (cut ? $"移動しました → {targetDir}" : $"貼り付けました → {targetDir}");
        await RefreshColumnsAsync(affected);
    }

    private static void PushTransferUndo(List<(string Source, string Dest)> performed, bool copy,
                                         string label = "コピー")
    {
        if (performed.Count == 0)
            return;
        UndoStack.Push(copy
            ? new RecycleOp(performed.Select(p => p.Dest).ToList(), label)
            : new MoveOp(performed));
    }

    /// <summary>直前のファイル操作 (名前変更 / 移動 / コピー / 新規フォルダ / 削除) を取り消す。</summary>
    public Task UndoAsync() => UndoOrRedoAsync(redo: false);

    /// <summary>取り消した操作をやり直す (Ctrl+Shift+Z / Ctrl+Y)。</summary>
    public Task RedoAsync() => UndoOrRedoAsync(redo: true);

    /// <summary>取り消し / やり直しの本体 (移動・ごみ箱からの復元) を専用スレッドで実行する。
    /// 実行中のファイル操作があれば、それが終わって履歴が積まれてから取り出す
    /// (先に取り出すと、コピー中の Ctrl+Z が一つ前の操作を取り消してしまう)。</summary>
    private async Task UndoOrRedoAsync(bool redo)
    {
        var owner = FileOps.ActiveWindow();
        IReadOnlyCollection<string> affected;
        await _fileOpGate.WaitAsync();
        try
        {
            if ((redo ? UndoStack.PopRedo() : UndoStack.PopUndo()) is not { } op)
            {
                StatusText = redo ? "やり直す操作はありません" : "元に戻す操作はありません";
                return;
            }
            StatusText = redo ? "やり直しています…" : "元に戻しています…";
            string? error;
            (affected, error) = await FileWorker.RunAsync(owner, () =>
            {
                var a = op.Undo(out var e);
                return (a, e);
            });
            if (error is null)
            {
                // Inverse は Undo の実行後に呼ぶ規約 (RecycleOp は実行時の記録から逆操作を組む)
                if (redo)
                    UndoStack.PushUndoKeepRedo(op.Inverse());
                else
                    UndoStack.PushRedo(op.Inverse());
                StatusText = redo ? $"やり直しました: {op.Description}" : $"元に戻しました: {op.Description}";
            }
            else
            {
                StatusText = error;
            }
        }
        finally
        {
            _fileOpGate.Release();
        }
        await RefreshColumnsAsync(affected);
    }

    /// <summary>ClipboardMarks の現在の状態を、このウィンドウの全項目の表示に反映する。</summary>
    public void ApplyClipboardMarks()
    {
        foreach (var tab in Tabs)
            foreach (var column in tab.Columns)
                foreach (var item in column.Items)
                    if (item is { UseRealIcon: false, IsGroupEntry: false })
                        item.ClipMark = ClipboardMarks.MarkFor(item.Path);
    }

    /// <summary>選択ファイル群を削除する (permanent=false はごみ箱へ)。
    /// ごみ箱の無い場所 (NAS 等) はアプリの退避ごみ箱へ移動し、どちらも Ctrl+Z で戻せる。</summary>
    public async Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, nint ownerHwnd)
    {
        if (paths.Count == 0)
            return;
        if (!await EnsureReachableAsync(paths))
            return;

        await _fileOpGate.WaitAsync();
        try
        {
            StatusText = "削除しています…";
            if (permanent)
            {
                var (affected, error) = await FileWorker.RunAsync(ownerHwnd, () =>
                {
                    var a = FileOps.Delete(paths, permanent: true, ownerHwnd, out var e);
                    return (a, e);
                });
                StatusText = error ?? $"削除しました: {paths.Count} 個";
                await RefreshColumnsAsync(affected);
                return;
            }

            // 通常は 1 フォルダ内の選択なのでどちらか一方だけになる
            var recyclable = paths.Where(FileOps.SupportsRecycleBin).ToList();
            var trashable = paths.Where(p => !FileOps.SupportsRecycleBin(p)).ToList();
            var useAppTrash = _settings.UseAppTrash;
            var r = await FileWorker.RunAsync(ownerHwnd, () => DeleteCore(recyclable, trashable, useAppTrash, ownerHwnd));

            // 取り消し履歴は UI スレッドで積む (UndoStack は全ウィンドウ共有)
            if (r.Recycled)
                UndoStack.Push(new RestoreOp(recyclable, "削除"));
            if (r.Trashed.Count > 0)
                UndoStack.Push(new MoveOp(r.Trashed, "削除"));

            var done = (r.Recycled ? recyclable.Count : 0) + r.Trashed.Count;
            StatusText = r.Error ?? (r.PermanentDone > 0 && done == 0
                ? $"削除しました: {r.PermanentDone} 個"
                : $"ごみ箱へ移動しました: {done} 個");
            await RefreshColumnsAsync(r.Affected);
        }
        finally
        {
            _fileOpGate.Release();
        }
    }

    private sealed record DeleteResult(
        HashSet<string> Affected, string? Error, bool Recycled,
        List<(string Source, string Dest)> Trashed, int PermanentDone);

    /// <summary>削除の本体 (専用スレッドで実行)。ごみ箱の無い場所 (NAS 等) は退避ごみ箱へ。</summary>
    private static DeleteResult DeleteCore(
        List<string> recyclable, List<string> trashable, bool useAppTrash, nint ownerHwnd)
    {
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? err = null;
        var recycled = false;
        var trashed = new List<(string Source, string Dest)>();
        var permanentDone = 0;

        if (recyclable.Count > 0)
        {
            affected.UnionWith(FileOps.Delete(recyclable, permanent: false, ownerHwnd, out var e));
            err ??= e;
            recycled = e is null;
        }
        if (trashable.Count > 0)
        {
            if (useAppTrash)
            {
                trashed = FileOps.MoveToAppTrash(trashable, out var e);
                err ??= e;
                foreach (var (source, _) in trashed)
                    if (Path.GetDirectoryName(source) is { } dir)
                        affected.Add(dir);
            }
            else
            {
                // 退避を使わない設定: シェルに任せる (ごみ箱が無い場所なので完全削除の警告が出る)
                affected.UnionWith(FileOps.Delete(trashable, permanent: false, ownerHwnd, out var e));
                err ??= e;
                if (e is null)
                    permanentDone += trashable.Count;
            }
        }
        return new DeleteResult(affected, err, recycled, trashed, permanentDone);
    }

    /// <summary>現在のフォルダに新しいフォルダを作る。既定名の重複は連番を付ける。</summary>
    public string? SuggestNewFolderName()
    {
        if (FavoriteTarget is not { } parent)
            return null;
        if (Reachability.IsKnownUnreachable(parent))
        {
            StatusText = $"接続先に届きません: {parent}";
            return null;
        }
        var name = "新しいフォルダ";
        var n = 2;
        while (Directory.Exists(Path.Combine(parent, name)) || File.Exists(Path.Combine(parent, name)))
            name = $"新しいフォルダ ({n++})";
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
            // 確認と作成は裏で (遅い共有で UI を止めない)。取り消し履歴の順序を保つため、
            // 他のファイル操作とは順番に通す
            await _fileOpGate.WaitAsync();
            try
            {
                var created = await Task.Run(() =>
                {
                    if (Directory.Exists(path) || File.Exists(path))
                        return false;
                    Directory.CreateDirectory(path);
                    return true;
                });
                if (!created)
                {
                    StatusText = $"『{name}』は既に存在します";
                    return;
                }
                UndoStack.Push(new RecycleOp(new[] { path }, "新しいフォルダ"));
            }
            finally
            {
                _fileOpGate.Release();
            }
            StatusText = $"フォルダを作成しました: {name}";
            await RefreshColumnsAsync(new[] { parent });

            // 作ったフォルダを選択する (通常のクリックと同じ扱いで次の列が開く)
            var column = ActiveTab?.Columns.FirstOrDefault(
                c => string.Equals(c.Path, parent, StringComparison.OrdinalIgnoreCase));
            if (column?.Items.FirstOrDefault(
                    i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) is { } createdItem)
                column.SelectedItem = createdItem;
        }
        catch (Exception ex)
        {
            StatusText = "フォルダを作成できませんでした: " + ex.Message;
        }
    }

    // ---- ドラッグ&ドロップ ----

    public async Task HandleDropAsync(string[] sources, string targetDir, DropMode mode)
    {
        if (!await EnsureReachableAsync(sources.Append(targetDir)))
            return;
        bool copy = mode == DropMode.Copy;
        var owner = FileOps.ActiveWindow();
        IReadOnlyCollection<string> affected;
        string? error;
        List<(string Source, string Dest)> performed;
        await _fileOpGate.WaitAsync();
        try
        {
            StatusText = mode switch
            {
                DropMode.Link => "ショートカットを作成しています…",
                DropMode.Copy => "コピーしています…",
                _ => "移動しています…",
            };
            (affected, error, performed) = await FileWorker.RunAsync(owner, () =>
            {
                var a = mode == DropMode.Link
                    ? FileOps.CreateShortcuts(sources, targetDir, out var e, out var p)
                    : FileOps.Transfer(sources, targetDir, copy, out e, out p);
                return (a, e, p);
            });
            // リンクもコピーと同じく「作られたものを消す」ことで元に戻せる
            PushTransferUndo(performed, copy: mode != DropMode.Move,
                             label: mode == DropMode.Link ? "ショートカット作成" : "コピー");
        }
        finally
        {
            _fileOpGate.Release();
        }
        if (error is not null)
            StatusText = error;
        else if (performed.Count > 0)
            StatusText = mode switch
            {
                DropMode.Link => $"ショートカットを作成しました → {targetDir}",
                DropMode.Copy => $"コピーしました → {targetDir}",
                _ => $"移動しました → {targetDir}",
            };
        // 実際に何か動いたときだけ再読込する。同じフォルダへの移動などの no-op で
        // 列を作り直すと、ドラッグ前の複数選択が単一復元に潰れてしまう
        if (performed.Count > 0)
            await RefreshColumnsAsync(affected);
    }

    /// <summary>指定フォルダを表示している列を再読み込みする (選択は保持)。</summary>
    public async Task RefreshColumnsAsync(IEnumerable<string> dirs)
    {
        var set = new HashSet<string>(dirs, StringComparer.OrdinalIgnoreCase);
        _navigatingDepth++;
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
            _navigatingDepth--;
        }
    }

    private void UpdateCurrentPathFromTab()
    {
        if (ActiveTab is null)
            return;
        var deepest = ActiveTab.Columns.LastOrDefault();
        if (deepest?.SelectedItem is { } selected)
            SetCurrentPath(selected.Path, selected.IsDirectory);
        else
            SetCurrentPath(deepest?.Path ?? "", isDirectory: true);
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
            StatusText = $"{subs} サブグループ、{folders} フォルダ";
            return;
        }
        var dirs = column.Items.Count(i => i.IsDirectory);
        var files = column.Items.Count - dirs;
        StatusText = $"{dirs} フォルダ、{files} ファイル";
    }

    private static string TrimTitle(string title)
        => title.Length > 24 ? title[..22] + "…" : title;

    private static string FormatSize(long bytes) => FileSystemItem.FormatSize(bytes);
}
