using System.IO;
using System.Runtime.InteropServices;
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
    /// <paramref name="performed"/> に実際に処理できた (元, 先) を返す (Ctrl+Z 用)。
    /// </summary>
    public static IReadOnlyCollection<string> Transfer(
        IEnumerable<string> sources, string targetDir, bool copy,
        out string? error, out List<(string Source, string Dest)> performed)
    {
        error = null;
        performed = new List<(string, string)>();
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

                // 同じフォルダーへのコピーは「◯◯ - コピー」を作る (エクスプローラーと同じ)
                if (copy && string.Equals(Path.GetFullPath(dest), Path.GetFullPath(trimmed), StringComparison.OrdinalIgnoreCase))
                    dest = UniqueCopyName(targetDir, name, isDir);

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
                performed.Add((trimmed, dest));
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

    /// <summary>「名前 - コピー.ext」「名前 - コピー (2).ext」… の空き名を返す。</summary>
    private static string UniqueCopyName(string dir, string name, bool isDir)
    {
        var stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDir ? "" : Path.GetExtension(name);
        var candidate = Path.Combine(dir, $"{stem} - コピー{ext}");
        var n = 2;
        while (Directory.Exists(candidate) || File.Exists(candidate))
            candidate = Path.Combine(dir, $"{stem} - コピー ({n++}){ext}");
        return candidate;
    }

    // ---- アプリの退避ごみ箱 (ごみ箱が使えない NAS・ネットワークドライブ・リムーバブル用) ----

    private const string AppTrashName = ".ColumnViewTrash";
    private static readonly TimeSpan AppTrashRetention = TimeSpan.FromDays(30);

    /// <summary>この場所の削除が Windows のごみ箱に入るか。
    /// UNC・ネットワークドライブ・リムーバブルはごみ箱が無く、シェルに任せると完全削除になる。</summary>
    public static bool SupportsRecycleBin(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return false; // UNC (NAS 等)
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return false;
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ごみ箱の無いボリューム用の疑似ごみ箱。同じボリューム直下の隠しフォルダー
    /// .ColumnViewTrash\(日時) へ「移動」する (同一ボリューム内なのでデータ転送なしで一瞬)。
    /// 戻り値は移動できた (元, 先)。取り消しは元の場所へ移動し直すだけ。
    /// </summary>
    public static List<(string Source, string Dest)> MoveToAppTrash(IReadOnlyList<string> paths, out string? error)
    {
        error = null;
        var performed = new List<(string Source, string Dest)>();

        foreach (var group in paths.GroupBy(
            p => Path.GetPathRoot(Path.GetFullPath(p)) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0)
                continue;
            string opDir;
            var trashRoot = Path.Combine(group.Key, AppTrashName);
            try
            {
                opDir = Path.Combine(trashRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(opDir);
                try
                {
                    File.SetAttributes(trashRoot, File.GetAttributes(trashRoot) | FileAttributes.Hidden);
                }
                catch { /* 属性を付けられなくても続行 */ }
            }
            catch (Exception)
            {
                // 共有のルートに書き込めない等 → この場所では退避できない (完全削除はしない)
                error = "ごみ箱の無い場所で、退避フォルダーも作成できませんでした。完全に削除するには Shift+Del を使ってください";
                continue;
            }

            foreach (var p in group)
            {
                try
                {
                    var trimmed = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var dest = Path.Combine(opDir, Path.GetFileName(trimmed));
                    if (Directory.Exists(p))
                        Directory.Move(p, dest);
                    else if (File.Exists(p))
                        File.Move(p, dest);
                    else
                        continue;
                    performed.Add((trimmed, dest));
                }
                catch (Exception ex)
                {
                    error = "削除できませんでした: " + ex.Message;
                }
            }

            // アプリ外から手動復旧できるよう、元のパスの控えを残す
            try
            {
                var lines = performed.Select(x => $"{x.Dest}\t{x.Source}");
                File.WriteAllLines(Path.Combine(opDir, "manifest.txt"), lines);
            }
            catch { /* 控えが書けなくても本体の退避は成立している */ }

            // 古い退避 (30 日超) は背景で掃除する
            var rootToPurge = trashRoot;
            _ = Task.Run(() => PurgeOldTrash(rootToPurge));
        }
        return performed;
    }

    /// <summary>退避ごみ箱の古い世代を削除する (フォルダー名の日時プレフィックスで判定)。</summary>
    private static void PurgeOldTrash(string trashRoot)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(trashRoot))
            {
                var name = Path.GetFileName(dir);
                if (name.Length < 15
                    || !DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", null,
                        System.Globalization.DateTimeStyles.None, out var stamp))
                    continue;
                if (DateTime.Now - stamp > AppTrashRetention)
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch { /* 使用中などは次回に持ち越し */ }
                }
            }
        }
        catch { /* 掃除の失敗は無視 */ }
    }

    // ---- 削除 (SHFileOperation: 複数項目を 1 回のシェル操作で処理) ----

    private const int FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40;       // ごみ箱へ
    private const ushort FOF_NOCONFIRMATION = 0x10;  // 確認なし (ごみ箱行きは取り消せるため)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>
    /// ファイル / フォルダーを削除する。permanent=false はごみ箱へ。
    /// 確認・進捗ダイアログはエクスプローラーと同じシェル標準 UI。
    /// 戻り値は再読み込みすべき親フォルダー。
    /// </summary>
    public static IReadOnlyCollection<string> Delete(
        IReadOnlyList<string> paths, bool permanent, nint ownerHwnd, out string? error)
    {
        error = null;
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
            return affected;

        foreach (var p in paths)
            if (Path.GetDirectoryName(p.TrimEnd(Path.DirectorySeparatorChar)) is { } parent)
                affected.Add(parent);

        var op = new SHFILEOPSTRUCTW
        {
            hwnd = ownerHwnd,
            wFunc = FO_DELETE,
            // 複数パスは NUL 区切り + 二重 NUL 終端
            pFrom = string.Join('\0', paths) + "\0\0",
            // ごみ箱行きは確認なし (エクスプローラー既定と同じ)、完全削除はシェルの確認を出す
            fFlags = permanent ? (ushort)0 : (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION),
        };
        var result = SHFileOperationW(ref op);
        if (result != 0 && !op.fAnyOperationsAborted)
            error = $"削除できませんでした (コード {result})";
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
