using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace ColumnView;

/// <summary>
/// ドロップされたファイル / フォルダーの移動・コピー。
/// Windows 標準の進捗ダイアログと上書き確認 UI (UIOption.AllDialogs) を使うため、
/// エクスプローラーと同じ操作感になる。
/// </summary>
public static class FileOps
{
    /// <summary>
    /// <paramref name="sources"/> を <paramref name="targetDir"/> へ移動 / コピーする。
    /// 戻り値は再読み込みすべきフォルダー (移動元の親と移動先)。
    /// </summary>
    public static IReadOnlyCollection<string> Transfer(
        IEnumerable<string> sources, string targetDir, bool copy, out string? error)
    {
        error = null;
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { targetDir };

        foreach (var src in sources)
        {
            try
            {
                var trimmed = src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(trimmed);
                if (string.IsNullOrEmpty(name))
                    continue;

                var isDir = Directory.Exists(src);
                var parent = Path.GetDirectoryName(trimmed);

                // 自分自身や子孫フォルダーへの移動・コピーは不正
                if (isDir && IsSameOrDescendant(targetDir, trimmed))
                {
                    error = "フォルダーを自分自身の中へは移動できません";
                    continue;
                }
                // 同じフォルダー内への移動は何もしない (コピーは複製として許可)
                if (!copy && string.Equals(parent, targetDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                var dest = Path.Combine(targetDir, name);

                if (copy)
                {
                    if (isDir) FileSystem.CopyDirectory(src, dest, UIOption.AllDialogs);
                    else FileSystem.CopyFile(src, dest, UIOption.AllDialogs);
                }
                else
                {
                    if (isDir) FileSystem.MoveDirectory(src, dest, UIOption.AllDialogs);
                    else FileSystem.MoveFile(src, dest, UIOption.AllDialogs);
                    if (parent is not null)
                        affected.Add(parent);
                }
            }
            catch (OperationCanceledException)
            {
                // ユーザーがダイアログでキャンセルした
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        return affected;
    }

    private static bool IsSameOrDescendant(string target, string source)
    {
        var t = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        var s = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(t, s, StringComparison.OrdinalIgnoreCase)
            || t.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
