using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace ColumnView;

/// <summary>直前のファイル操作を取り消すためのグローバル履歴 (全ウィンドウ共有・最大 20 件)。</summary>
public static class UndoStack
{
    private const int Capacity = 20;
    private static readonly List<IUndoOp> _ops = new();

    public static void Push(IUndoOp op)
    {
        _ops.Add(op);
        if (_ops.Count > Capacity)
            _ops.RemoveAt(0);
    }

    public static IUndoOp? Pop()
    {
        if (_ops.Count == 0)
            return null;
        var op = _ops[^1];
        _ops.RemoveAt(_ops.Count - 1);
        return op;
    }
}

public interface IUndoOp
{
    string Description { get; }

    /// <summary>取り消しを実行し、再読込すべきフォルダーを返す。</summary>
    IReadOnlyCollection<string> Undo(out string? error);
}

/// <summary>名前の変更 → 元の名前へ戻す。</summary>
public sealed class RenameOp(string oldPath, string newPath) : IUndoOp
{
    public string Description => "名前の変更";

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

/// <summary>移動 (切り取り貼り付け / D&D) → 元の場所へ戻す。</summary>
public sealed class MoveOp(IReadOnlyList<(string Source, string Dest)> pairs) : IUndoOp
{
    public string Description => "移動";

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

/// <summary>コピー → 作られた複製をごみ箱へ。</summary>
public sealed class CopyOp(IReadOnlyList<string> createdPaths) : IUndoOp
{
    public string Description => "コピー";

    public IReadOnlyCollection<string> Undo(out string? error)
    {
        var existing = createdPaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0)
        {
            error = "コピーされた項目が見つかりません";
            return Array.Empty<string>();
        }
        return FileOps.Delete(existing, permanent: false, 0, out error);
    }
}

/// <summary>新しいフォルダー → ごみ箱へ。</summary>
public sealed class NewFolderOp(string path) : IUndoOp
{
    public string Description => "新しいフォルダー";

    public IReadOnlyCollection<string> Undo(out string? error)
    {
        if (!Directory.Exists(path))
        {
            error = "フォルダーが見つかりません";
            return Array.Empty<string>();
        }
        return FileOps.Delete(new[] { path }, permanent: false, 0, out error);
    }
}

/// <summary>ごみ箱への削除 → シェルの「元に戻す」で復元。</summary>
public sealed class DeleteOp(IReadOnlyList<string> originalPaths) : IUndoOp
{
    public string Description => "削除 (ごみ箱から復元)";

    public IReadOnlyCollection<string> Undo(out string? error)
    {
        error = null;
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in originalPaths)
            if (Path.GetDirectoryName(p.TrimEnd('\\')) is { } dir)
                affected.Add(dir);

        var restored = RecycleBin.Restore(originalPaths);
        if (restored < originalPaths.Count)
            error = restored == 0
                ? "ごみ箱から復元できませんでした"
                : $"一部のみ復元しました ({restored}/{originalPaths.Count})";
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
