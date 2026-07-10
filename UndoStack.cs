using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace ColumnView;

/// <summary>
/// ファイル操作の取り消し / やり直し履歴 (全ウィンドウ共有・各最大 20 件)。
/// 各操作は自身の「逆操作」を作れる (Inverse)。Undo すると逆操作が Redo 側へ積まれ、
/// Redo するとさらにその逆が Undo 側へ戻る。新しい操作をしたら Redo 履歴は無効になる。
/// </summary>
public static class UndoStack
{
    private const int Capacity = 20;
    private static readonly List<IUndoOp> _undo = new();
    private static readonly List<IUndoOp> _redo = new();

    /// <summary>新しいファイル操作を記録する (やり直し履歴はクリア)。</summary>
    public static void Push(IUndoOp op)
    {
        Add(_undo, op);
        _redo.Clear();
    }

    /// <summary>Redo 実行後、逆操作を Undo 側へ戻す (Redo 履歴は保持)。</summary>
    public static void PushUndoKeepRedo(IUndoOp op) => Add(_undo, op);

    public static void PushRedo(IUndoOp op) => Add(_redo, op);

    public static IUndoOp? PopUndo() => Pop(_undo);
    public static IUndoOp? PopRedo() => Pop(_redo);

    private static void Add(List<IUndoOp> list, IUndoOp op)
    {
        list.Add(op);
        if (list.Count > Capacity)
            list.RemoveAt(0);
    }

    private static IUndoOp? Pop(List<IUndoOp> list)
    {
        if (list.Count == 0)
            return null;
        var op = list[^1];
        list.RemoveAt(list.Count - 1);
        return op;
    }
}

public interface IUndoOp
{
    string Description { get; }

    /// <summary>取り消しを実行し、再読込すべきフォルダを返す。</summary>
    IReadOnlyCollection<string> Undo(out string? error);

    /// <summary>この操作の逆 (Undo した状態から元へ戻す操作)。</summary>
    IUndoOp Inverse();
}

/// <summary>名前の変更 → 元の名前へ戻す。</summary>
public sealed class RenameOp(string oldPath, string newPath) : IUndoOp
{
    public string Description => "名前の変更";

    public IUndoOp Inverse() => new RenameOp(newPath, oldPath);

    public IReadOnlyCollection<string> Undo(out string? error)
    {
        error = null;
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Path.GetDirectoryName(oldPath) is { } dir)
            affected.Add(dir);
        try
        {
            if (Directory.Exists(newPath))
                Directory.Move(newPath, oldPath);
            else if (File.Exists(newPath))
                File.Move(newPath, oldPath);
            else
                error = "対象が見つかりません (すでに移動または削除されています)";
        }
        catch (Exception ex)
        {
            error = "元に戻せませんでした: " + ex.Message;
        }
        return affected;
    }
}

/// <summary>移動 (切り取り貼り付け / D&D / アプリごみ箱への退避) → 元の場所へ戻す。</summary>
public sealed class MoveOp(IReadOnlyList<(string Source, string Dest)> pairs, string description = "移動") : IUndoOp
{
    public string Description => description;

    public IUndoOp Inverse()
        => new MoveOp(pairs.Select(p => (p.Dest, p.Source)).ToList(), description);

    public IReadOnlyCollection<string> Undo(out string? error)
    {
        error = null;
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, dest) in pairs.Reverse())
        {
            if (Path.GetDirectoryName(source.TrimEnd('\\')) is { } srcDir)
                affected.Add(srcDir);
            if (Path.GetDirectoryName(dest.TrimEnd('\\')) is { } destDir)
                affected.Add(destDir);
            try
            {
                // 移動先の親が消えていても戻せるように作り直す (掃除済みの退避フォルダへのやり直し等)
                if (Path.GetDirectoryName(source.TrimEnd('\\')) is { Length: > 0 } parent)
                    Directory.CreateDirectory(parent);
                if (Directory.Exists(dest))
                    FileSystem.MoveDirectory(dest, source, UIOption.AllDialogs);
                else if (File.Exists(dest))
                    FileSystem.MoveFile(dest, source, UIOption.AllDialogs);
                else
                    error = "一部の項目が見つかりませんでした";
            }
            catch (OperationCanceledException)
            {
                // ユーザーがダイアログでキャンセルした
            }
            catch (Exception ex)
            {
                error = "元に戻せませんでした: " + ex.Message;
            }
        }
        return affected;
    }
}

/// <summary>「これらのパスをごみ箱へ入れる」操作。コピー / 新規フォルダの取り消し、削除のやり直しに使う。
/// ごみ箱の無い場所 (NAS 等) はアプリの退避ごみ箱へ移動し、完全削除にはしない。
/// 逆操作は実行時にどこへ入れたか (ごみ箱 / 退避先) を記録して組み立てる。</summary>
public sealed class RecycleOp(IReadOnlyList<string> paths, string description) : IUndoOp
{
    private List<string> _recycled = new();
    private List<(string Source, string Dest)> _trashed = new();

    public string Description => description;

    public IUndoOp Inverse()
    {
        var ops = new List<IUndoOp>();
        if (_recycled.Count > 0)
            ops.Add(new RestoreOp(_recycled, description));
        if (_trashed.Count > 0)
            ops.Add(new MoveOp(_trashed, description));
        return ops.Count == 1 ? ops[0] : new CompositeOp(ops, description);
    }

    public IReadOnlyCollection<string> Undo(out string? error)
    {
        error = null;
        _recycled = new();
        _trashed = new();
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0)
        {
            error = "対象の項目が見つかりません";
            return affected;
        }

        var recyclable = existing.Where(FileOps.SupportsRecycleBin).ToList();
        var trashable = existing.Where(p => !FileOps.SupportsRecycleBin(p)).ToList();

        if (recyclable.Count > 0)
        {
            affected.UnionWith(FileOps.Delete(recyclable, permanent: false, 0, out var e));
            error ??= e;
            if (e is null)
                _recycled = recyclable;
        }
        if (trashable.Count > 0)
        {
            var performed = FileOps.MoveToAppTrash(trashable, out var e);
            error ??= e;
            _trashed = performed;
            foreach (var (source, _) in performed)
                if (Path.GetDirectoryName(source.TrimEnd('\\')) is { } dir)
                    affected.Add(dir);
        }
        return affected;
    }
}

/// <summary>複数の操作を 1 手として扱う (ごみ箱と退避ごみ箱が混在した場合など)。</summary>
public sealed class CompositeOp(IReadOnlyList<IUndoOp> ops, string description) : IUndoOp
{
    public string Description => description;

    public IUndoOp Inverse()
        => new CompositeOp(ops.Select(o => o.Inverse()).Reverse().ToList(), description);

    public IReadOnlyCollection<string> Undo(out string? error)
    {
        error = null;
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in ops)
        {
            affected.UnionWith(op.Undo(out var e));
            error ??= e;
        }
        return affected;
    }
}

/// <summary>「これらのパスをごみ箱から復元する」操作。削除の取り消し、コピー等のやり直しに使う。</summary>
public sealed class RestoreOp(IReadOnlyList<string> paths, string description) : IUndoOp
{
    public string Description => description;

    public IUndoOp Inverse() => new RecycleOp(paths, description);

    public IReadOnlyCollection<string> Undo(out string? error)
    {
        error = null;
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
            if (Path.GetDirectoryName(p.TrimEnd('\\')) is { } dir)
                affected.Add(dir);

        var restored = RecycleBin.Restore(paths);
        if (restored < paths.Count)
            error = restored == 0
                ? "ごみ箱から復元できませんでした"
                : $"一部のみ復元しました ({restored}/{paths.Count})";
        return affected;
    }
}

/// <summary>ごみ箱の項目をシェルの「元に戻す」動詞で復元する。</summary>
public static class RecycleBin
{
    private const int SsfBitBucket = 10; // ごみ箱

    /// <summary>元のフルパスが一致する項目を復元する。戻り値 = 復元できた数。</summary>
    public static int Restore(IReadOnlyList<string> originalPaths)
    {
        var remaining = new HashSet<string>(originalPaths, StringComparer.OrdinalIgnoreCase);
        var restored = 0;
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } shellType
                || Activator.CreateInstance(shellType) is not { } shellObj)
                return 0;
            dynamic shell = shellObj;
            var bin = shell.NameSpace(SsfBitBucket);
            if (bin is null)
                return 0;

            // 同名を複数回削除した場合に最近のものを優先するため、列挙の末尾から照合する
            var items = new List<dynamic>();
            foreach (var item in bin.Items())
                items.Add(item);

            for (var i = items.Count - 1; i >= 0 && remaining.Count > 0; i--)
            {
                var item = items[i];
                string name = bin.GetDetailsOf(item, 0);     // 名前 (拡張子非表示設定だと拡張子なし)
                string origDir = bin.GetDetailsOf(item, 1);  // 元の場所
                if (MatchTarget(remaining, origDir, name) is not { } target)
                    continue;
                if (InvokeRestore(item))
                {
                    restored++;
                    remaining.Remove(target);
                }
            }
        }
        catch (Exception)
        {
            // シェルにアクセスできなければ復元なしで返す
        }
        return restored;
    }

    /// <summary>「拡張子を表示しない」設定でも一致できるよう、拡張子なしの名前も許容して照合する。</summary>
    private static string? MatchTarget(HashSet<string> remaining, string origDir, string name)
    {
        foreach (var t in remaining)
        {
            if (!string.Equals(Path.GetDirectoryName(t.TrimEnd('\\')), origDir, StringComparison.OrdinalIgnoreCase))
                continue;
            var fileName = Path.GetFileName(t.TrimEnd('\\'));
            if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(fileName), name, StringComparison.OrdinalIgnoreCase))
                return t;
        }
        return null;
    }

    private static bool InvokeRestore(dynamic item)
    {
        try
        {
            foreach (var verb in item.Verbs())
            {
                string label = ((string)verb.Name).Replace("&", "");
                if (label.StartsWith("元に戻す", StringComparison.Ordinal)
                    || label.StartsWith("Restore", StringComparison.OrdinalIgnoreCase))
                {
                    verb.DoIt();
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // この項目は復元できなかった
        }
        return false;
    }
}
