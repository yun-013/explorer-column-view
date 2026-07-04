using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ColumnView;

/// <summary>
/// クラウド同期プロバイダ (OneDrive / Google ドライブ / Nextcloud / Dropbox 等) の連携。
///
/// 状態判定は <see cref="FileSystemItem.GetCloudStatus"/> が Windows 標準のクラウド属性
/// (Cloud Files API が付ける PINNED / UNPINNED / RECALL 属性) を読むだけなので、
/// もともとプロバイダ非依存。ここではその上物として
///  - 同期ルートの列挙 (ホーム列に並べる / ツールチップにプロバイダ名を出す)
///  - ファイル / フォルダー単位のオフライン切替 (📌 保持 ⇄ ☁ オンラインのみ)
/// を提供する。切替は cfapi が監視するピン属性を書くだけなので、各クライアントの
/// 「常にこのデバイスに保持 / 空き容量を増やす」と同じ結果に同期される。
/// </summary>
public static class CloudSync
{
    public record SyncRoot(string Path, string Provider);

    private static List<SyncRoot>? _roots;

    /// <summary>登録済みの同期ルート (プロバイダ名付き)。初回に一度だけレジストリを読む。</summary>
    public static IReadOnlyList<SyncRoot> Roots => _roots ??= EnumerateRoots();

    /// <summary>指定パスが属するクラウドプロバイダ名。該当しなければ null。</summary>
    public static string? ProviderForPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var normalized = path.TrimEnd('\\');
        SyncRoot? best = null;
        foreach (var root in Roots)
        {
            if (IsUnderOrEqual(normalized, root.Path)
                && (best is null || root.Path.Length > best.Path.Length))
                best = root;
        }
        return best?.Provider;
    }

    /// <summary>path が root と同じか、その配下か (フォルダー境界を尊重して前方一致の誤爆を防ぐ)。</summary>
    private static bool IsUnderOrEqual(string path, string root)
    {
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Length == root.Length || path[root.Length] == '\\';
    }

    private static List<SyncRoot> EnumerateRoots()
    {
        var roots = new List<SyncRoot>();
        try
        {
            using var manager = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            if (manager is null)
                return roots;

            foreach (var syncRootId in manager.GetSubKeyNames())
            {
                try
                {
                    using var idKey = manager.OpenSubKey(syncRootId);
                    if (idKey is null)
                        continue;

                    var provider = ResolveProviderName(idKey, syncRootId);

                    using var userRoots = idKey.OpenSubKey("UserSyncRoots");
                    if (userRoots is null)
                        continue;
                    foreach (var name in userRoots.GetValueNames())
                    {
                        if (userRoots.GetValue(name) is string path && Directory.Exists(path))
                            roots.Add(new SyncRoot(path.TrimEnd('\\'), provider));
                    }
                }
                catch
                {
                    // 壊れた登録は飛ばす
                }
            }
        }
        catch
        {
            // レジストリが読めなくても致命的ではない
        }
        return roots;
    }

    /// <summary>SyncRootId の接頭辞や DisplayNameResource から人間可読なプロバイダ名を決める。</summary>
    private static string ResolveProviderName(RegistryKey idKey, string syncRootId)
    {
        // SyncRootId は "ProviderId!SID!AccountId" 形式。接頭辞から既知プロバイダを推定
        var prefix = syncRootId.Split('!')[0];
        var lower = prefix.ToLowerInvariant();
        if (lower.Contains("onedrive")) return "OneDrive";
        if (lower.Contains("google")) return "Google ドライブ";
        if (lower.Contains("nextcloud")) return "Nextcloud";
        if (lower.Contains("dropbox")) return "Dropbox";
        if (lower.Contains("mega")) return "MEGA";
        if (lower.Contains("box")) return "Box";
        if (lower.Contains("icloud")) return "iCloud";

        // 上記に当てはまらなければ DisplayNameResource (間接文字列) を試す
        if (idKey.GetValue("DisplayNameResource") is string display)
        {
            if (display.StartsWith("@", StringComparison.Ordinal))
            {
                var resolved = LoadIndirectString(display);
                if (!string.IsNullOrWhiteSpace(resolved))
                    return resolved!;
            }
            else if (!string.IsNullOrWhiteSpace(display))
            {
                return display;
            }
        }
        return prefix;
    }

    private static string? LoadIndirectString(string source)
    {
        var sb = new StringBuilder(1024);
        return SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    // ---- オフライン切替 (ピン属性) ----

    private const int FILE_ATTRIBUTE_PINNED = 0x00080000;
    private const int FILE_ATTRIBUTE_UNPINNED = 0x00100000;

    /// <summary>
    /// パスのオフライン保持状態を切り替える。
    /// pinned=true: 常にこのデバイスに保持 (📌 / ダウンロード)。
    /// pinned=false: オンラインのみ (☁ / 空き容量を確保)。
    /// フォルダーは配下を再帰的に処理する。cfapi がこのピン属性を監視して同期する。
    /// </summary>
    public static void SetOffline(string path, bool pinned, CancellationToken ct = default)
    {
        ApplyPin(path, pinned);
        if (!Directory.Exists(path))
            return;

        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string current = stack.Pop();
            DirectoryInfo dir;
            try
            {
                dir = new DirectoryInfo(current);
                foreach (var file in dir.EnumerateFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    ApplyPin(file.FullName, pinned);
                }
                foreach (var sub in dir.EnumerateDirectories())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue; // ジャンクション/リンクは辿らない
                    ApplyPin(sub.FullName, pinned);
                    stack.Push(sub.FullName);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* アクセスできない項目は飛ばす */ }
        }
    }

    private static void ApplyPin(string path, bool pinned)
    {
        try
        {
            var attrs = GetFileAttributes(path);
            if (attrs == INVALID_FILE_ATTRIBUTES)
                return;
            // 相反するピン属性は必ず落としてから目的の属性を立てる
            attrs &= ~(FILE_ATTRIBUTE_PINNED | FILE_ATTRIBUTE_UNPINNED);
            attrs |= pinned ? FILE_ATTRIBUTE_PINNED : FILE_ATTRIBUTE_UNPINNED;
            SetFileAttributes(path, attrs);
        }
        catch
        {
            // 切替できない項目 (通常ファイル等) は無視
        }
    }

    private const int INVALID_FILE_ATTRIBUTES = -1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFileAttributes(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileAttributes(string lpFileName, int dwFileAttributes);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);
}
