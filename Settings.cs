using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColumnView;

/// <summary>よく使うフォルダの組合せ (タブグループ)。フォルダとサブグループを入れ子に持てる。</summary>
public class FavoriteGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Paths { get; set; } = new();
    public List<FavoriteGroup> Subgroups { get; set; } = new();

    /// <summary>子 (サブグループ＋フォルダ) の統一並び順。キー: フォルダ=パス、サブグループ="group:"+Id。</summary>
    public List<string> ChildOrder { get; set; } = new();
}

public class AppSettings
{
    public SortKey SortKey { get; set; } = SortKey.Name;
    public bool SortDescending { get; set; } = true;

    /// <summary>フォルダを先頭にまとめるか (false = フォルダとファイルを同列に並べる)。</summary>
    public bool FoldersFirst { get; set; }

    public bool ShowHidden { get; set; }
    public List<string> Favorites { get; set; } = new();
    public List<FavoriteGroup> Groups { get; set; } = new();

    /// <summary>ホーム列の「お気に入り＋トップレベルのグループ」の統一並び順。
    /// キー: お気に入り=パス、グループ="group:"+Id。未登録のものは末尾。</summary>
    public List<string> HomeOrder { get; set; } = new();

    // ---- 退避ごみ箱 (ごみ箱の無い NAS・リムーバブル用) ----

    /// <summary>false にすると退避せず、シェルの警告付き完全削除にする (取り消し不可)。</summary>
    public bool UseAppTrash { get; set; } = true;

    /// <summary>退避ごみ箱の保持日数。過ぎたものは起動時と削除時に掃除される。</summary>
    public int TrashRetentionDays { get; set; } = 30;

    /// <summary>退避ごみ箱を作ったことのあるボリューム (起動時の掃除の巡回先)。</summary>
    public List<string> TrashRoots { get; set; } = new();

    public void RegisterTrashRoot(string root)
    {
        if (TrashRoots.Any(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase)))
            return;
        TrashRoots.Add(root);
        Save();
    }

    // ---- セッション復元 ----

    /// <summary>起動時に前回のタブ構成を復元するか。</summary>
    public bool RestoreSession { get; set; } = true;

    /// <summary>最後に閉じたウィンドウの各タブのフォルダ (null = ホーム)。</summary>
    public List<string?> SessionTabs { get; set; } = new();

    /// <summary>前回アクティブだったタブの位置。</summary>
    public int SessionActiveTab { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ColumnView", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
                if (settings.NormalizeGroups())
                    settings.Save(); // 旧データ(Id 無し)に Id を振ったら一度書き戻す
                return settings;
            }
        }
        catch (Exception)
        {
            // 壊れた設定ファイルは既定値で上書き
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
            // 保存失敗は致命的ではないので無視 (次回起動時は既定値)
        }
    }

    public bool IsFavorite(string path)
        => Favorites.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>追加したら true、削除したら false を返す</summary>
    public bool ToggleFavorite(string path)
    {
        var existing = Favorites.FirstOrDefault(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Favorites.Remove(existing);
            Save();
            return false;
        }
        Favorites.Add(path);
        Save();
        return true;
    }

    // ---- 統一並び順 (ホーム列＝お気に入り＋グループ / グループ列＝サブグループ＋フォルダ) ----

    private static List<string> OrderBy(List<string> keys, List<string> order)
        => keys.OrderBy(k =>
        {
            var i = order.IndexOf(k);
            return i < 0 ? int.MaxValue : i;
        }).ToList();

    /// <summary>ホーム列に並ぶキーを統一順で返す (HomeOrder 優先、未登録は末尾)。</summary>
    public List<string> OrderedHomeKeys()
    {
        var keys = new List<string>();
        foreach (var g in Groups)
            keys.Add("group:" + g.Id);
        keys.AddRange(Favorites);
        return OrderBy(keys, HomeOrder);
    }

    /// <summary>グループの子 (サブグループ＋フォルダ) を統一順で返す。</summary>
    public List<string> OrderedChildKeys(FavoriteGroup group)
    {
        var keys = new List<string>();
        foreach (var s in group.Subgroups)
            keys.Add("group:" + s.Id);
        keys.AddRange(group.Paths);
        return OrderBy(keys, group.ChildOrder);
    }

    /// <summary>項目を target の前後へ移動する。containerGroupId==null はホーム列、それ以外はそのグループの中身。</summary>
    public bool MoveChild(string? containerGroupId, string sourceKey, string targetKey, bool insertAfter)
    {
        if (string.Equals(sourceKey, targetKey, StringComparison.Ordinal))
            return false;

        List<string> order;
        FavoriteGroup? container = null;
        if (containerGroupId is null)
        {
            order = OrderedHomeKeys();
        }
        else
        {
            container = FindGroup(containerGroupId);
            if (container is null)
                return false;
            order = OrderedChildKeys(container);
        }

        if (!order.Contains(sourceKey) || !order.Contains(targetKey))
            return false;
        order.Remove(sourceKey);
        var ti = order.IndexOf(targetKey);
        if (ti < 0)
            return false;
        if (insertAfter)
            ti++;
        order.Insert(ti, sourceKey);

        if (container is null)
            HomeOrder = order;
        else
            container.ChildOrder = order;
        Save();
        return true;
    }

    /// <summary>お気に入り <paramref name="source"/> を <paramref name="target"/> の直前 / 直後へ移動する。</summary>
    public bool MoveFavorite(string source, string target, bool insertAfter)
    {
        var srcIndex = Favorites.FindIndex(f => string.Equals(f, source, StringComparison.OrdinalIgnoreCase));
        if (srcIndex < 0)
            return false;

        var item = Favorites[srcIndex];
        Favorites.RemoveAt(srcIndex);

        var dstIndex = Favorites.FindIndex(f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase));
        if (dstIndex < 0)
        {
            // 並べ替え対象が見つからない (= source 自身など): 元に戻して何もしない
            Favorites.Insert(srcIndex, item);
            return false;
        }
        if (insertAfter)
            dstIndex++;

        Favorites.Insert(dstIndex, item);
        Save();
        return true;
    }

    // ---- タブグループ (入れ子・ID 管理) ----

    private static bool NameEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Id 未設定の旧データに Id を振り、リストの null を補う。変更があれば true。</summary>
    public bool NormalizeGroups(List<FavoriteGroup>? list = null)
    {
        list ??= Groups;
        var changed = false;
        foreach (var g in list)
        {
            if (string.IsNullOrEmpty(g.Id))
            {
                g.Id = Guid.NewGuid().ToString("N");
                changed = true;
            }
            g.Paths ??= new();
            g.Subgroups ??= new();
            g.ChildOrder ??= new();
            if (NormalizeGroups(g.Subgroups))
                changed = true;
        }
        return changed;
    }

    public FavoriteGroup? FindGroup(string id) => FindGroup(id, Groups);

    private static FavoriteGroup? FindGroup(string id, List<FavoriteGroup> list)
    {
        foreach (var g in list)
        {
            if (g.Id == id)
                return g;
            if (FindGroup(id, g.Subgroups) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>id のグループを含む親リスト (トップレベルなら Groups) を返す。</summary>
    private List<FavoriteGroup>? FindParentList(string id, List<FavoriteGroup>? list = null)
    {
        list ??= Groups;
        if (list.Any(g => g.Id == id))
            return list;
        foreach (var g in list)
            if (FindParentList(id, g.Subgroups) is { } found)
                return found;
        return null;
    }

    private static bool IsSelfOrDescendant(FavoriteGroup group, string id)
        => group.Id == id || group.Subgroups.Any(s => IsSelfOrDescendant(s, id));

    /// <summary>新しいグループを作る。parentId が null ならトップレベル。</summary>
    public FavoriteGroup? CreateGroup(string name, string? parentId = null)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name))
            return null;
        var list = parentId is null ? Groups : FindGroup(parentId)?.Subgroups;
        if (list is null)
            return null;
        var group = new FavoriteGroup { Id = Guid.NewGuid().ToString("N"), Name = name };
        list.Add(group);
        Save();
        return group;
    }

    public bool RenameGroup(string id, string newName)
    {
        newName = newName.Trim();
        var group = FindGroup(id);
        if (group is null || string.IsNullOrEmpty(newName))
            return false;
        group.Name = newName;
        Save();
        return true;
    }

    public bool DeleteGroup(string id)
    {
        var list = FindParentList(id);
        var group = FindGroup(id);
        if (list is null || group is null)
            return false;
        list.Remove(group);
        Save();
        return true;
    }

    /// <summary>グループにフォルダを追加する (既に含まれていれば false)。</summary>
    public bool AddToGroup(string id, string path)
    {
        var group = FindGroup(id);
        if (group is null)
            return false;
        path = Path.GetFullPath(path);
        if (group.Paths.Any(p => NameEq(p, path)))
            return false;
        group.Paths.Add(path);
        Save();
        return true;
    }

    public bool RemoveFromGroup(string id, string path)
    {
        var group = FindGroup(id);
        if (group is null)
            return false;
        var index = group.Paths.FindIndex(p => NameEq(p, path));
        if (index < 0)
            return false;
        group.Paths.RemoveAt(index);
        Save();
        return true;
    }

    /// <summary>source を target の直前/直後へ移動する (target の階層へ。同階層内の並べ替えも含む)。</summary>
    public bool MoveGroupRelative(string sourceId, string targetId, bool insertAfter)
    {
        if (sourceId == targetId)
            return false;
        var src = FindGroup(sourceId);
        var srcList = FindParentList(sourceId);
        var dstList = FindParentList(targetId);
        if (src is null || srcList is null || dstList is null)
            return false;
        if (IsSelfOrDescendant(src, targetId))
            return false; // 自分の子孫の隣には入れられない (循環)
        srcList.Remove(src);
        var dstIndex = dstList.FindIndex(g => g.Id == targetId);
        if (dstIndex < 0)
        {
            srcList.Add(src); // 取り消して何もしない
            return false;
        }
        if (insertAfter)
            dstIndex++;
        dstList.Insert(dstIndex, src);
        Save();
        return true;
    }

    /// <summary>source を target の入れ子 (Subgroups の末尾) へ移動する。</summary>
    public bool MoveGroupInto(string sourceId, string targetId)
    {
        if (sourceId == targetId)
            return false;
        var src = FindGroup(sourceId);
        var srcList = FindParentList(sourceId);
        var target = FindGroup(targetId);
        if (src is null || srcList is null || target is null)
            return false;
        if (IsSelfOrDescendant(src, targetId))
            return false; // 自分の子孫には入れられない (循環)
        srcList.Remove(src);
        target.Subgroups.Add(src);
        Save();
        return true;
    }

    /// <summary>source をトップレベルの末尾へ移動する。</summary>
    public bool MoveGroupToTop(string sourceId)
    {
        var src = FindGroup(sourceId);
        var srcList = FindParentList(sourceId);
        if (src is null || srcList is null || ReferenceEquals(srcList, Groups))
            return false;
        srcList.Remove(src);
        Groups.Add(src);
        Save();
        return true;
    }

    /// <summary>group 内のフォルダ source を target の直前/直後へ移動する (メンバーの並べ替え)。</summary>
    public bool MovePathInGroup(string groupId, string source, string target, bool insertAfter)
    {
        var group = FindGroup(groupId);
        if (group is null)
            return false;
        var srcIndex = group.Paths.FindIndex(p => NameEq(p, source));
        if (srcIndex < 0)
            return false;
        var item = group.Paths[srcIndex];
        group.Paths.RemoveAt(srcIndex);
        var dstIndex = group.Paths.FindIndex(p => NameEq(p, target));
        if (dstIndex < 0)
        {
            group.Paths.Insert(srcIndex, item);
            return false;
        }
        if (insertAfter)
            dstIndex++;
        group.Paths.Insert(dstIndex, item);
        Save();
        return true;
    }

    /// <summary>id 配下 (サブグループ含む) の全フォルダを順に集める (重複除去)。「すべて開く」用。</summary>
    public List<string> CollectPaths(string id)
    {
        var result = new List<string>();
        if (FindGroup(id) is { } group)
            Collect(group, result);
        return result;

        static void Collect(FavoriteGroup g, List<string> acc)
        {
            foreach (var p in g.Paths)
                if (!acc.Contains(p, StringComparer.OrdinalIgnoreCase))
                    acc.Add(p);
            foreach (var sub in g.Subgroups)
                Collect(sub, acc);
        }
    }

    /// <summary>移動先候補 (source 自身とその子孫を除く全グループ) を平坦に列挙する。</summary>
    public IEnumerable<(FavoriteGroup Group, int Depth)> EnumerateGroupsExcept(string excludeId)
    {
        var result = new List<(FavoriteGroup, int)>();
        Walk(Groups, 0);
        return result;

        void Walk(List<FavoriteGroup> list, int depth)
        {
            foreach (var g in list)
            {
                if (g.Id == excludeId)
                    continue; // 自分とその子孫は対象外
                result.Add((g, depth));
                Walk(g.Subgroups, depth + 1);
            }
        }
    }
}
