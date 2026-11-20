using System.IO;

namespace ColumnView;

/// <summary>
/// ショートカットや保存された検索の行き先として、選んだだけで (明示操作なしに) 読みに行ってよい
/// ネットワーク上の場所か。見知らぬサーバーへ SMB で繋ぎに行くと、Windows がサインイン情報
/// (NTLM のハッシュ) を相手に送ってしまう。細工した .lnk / .search-ms を選ばせるだけで
/// それが起きないよう、信頼できない相手には Enter / ダブルクリックまで繋がない。
/// 信頼するのは:
///  - ネットワークドライブ (Z: 等) — 利用者が自分で割り当てたもの
///  - 割り当て済みドライブの接続先・お気に入り・グループにあるサーバー
///  - 設定の TrustedNetworkHosts に書いたサーバー
/// </summary>
public static class NetworkTrust
{
    /// <summary>ネットワークに触れずに判定する (設定とレジストリだけを見る)。
    /// 判別できない書き方 (空・相対・デバイス名前空間など) は信頼しない側に倒す。</summary>
    public static bool IsTrusted(string? path)
    {
        if (Normalize(path) is not { } normalized)
            return false;
        // ドライブ文字: ローカル、またはネットワークドライブ (割り当てたのは利用者自身で、接続済みでもある)
        if (IsDriveLetterPath(normalized))
            return true;
        return normalized.StartsWith(@"\\", StringComparison.Ordinal)
            && HostOf(normalized) is { } host
            && TrustedHosts().Contains(host);
    }

    /// <summary>ネットワークドライブでないドライブ文字のパスか (触れても外へ繋ぎに行かない場所)。</summary>
    public static bool IsLocalPath(string? path)
    {
        if (Normalize(path) is not { } normalized || !IsDriveLetterPath(normalized))
            return false;
        try
        {
            // GetDriveType はドライブ文字の種類を見るだけで、接続先には問い合わせない
            return new DriveInfo(normalized[..1]).DriveType != DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>UNC パスのホスト部分 ("\\?\UNC\host\..." や "//host/..." の書き方も含む)。UNC でなければ null。</summary>
    public static string? HostOf(string? path)
    {
        if (Normalize(path) is not { } normalized || !normalized.StartsWith(@"\\", StringComparison.Ordinal))
            return null;
        var host = normalized[2..].Split('\\', 2)[0];
        return host.Length > 0 ? host : null;
    }

    /// <summary>同じ場所の別の書き方をそろえる: "/" → "\"、"\\?\UNC\host" / "\\.\UNC\host" → "\\host"、
    /// "\\?\C:\" → "C:\"。それ以外のデバイス名前空間 ("\\?\GLOBALROOT\..." 等) や空は null (= 信頼しない)。</summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var p = path.Trim().Replace('/', '\\');
        foreach (var prefix in new[] { @"\\?\UNC\", @"\\.\UNC\", @"\??\UNC\" })
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return @"\\" + p[prefix.Length..];
        foreach (var prefix in new[] { @"\\?\", @"\\.\", @"\??\" })
            if (p.StartsWith(prefix, StringComparison.Ordinal))
                return IsDriveLetterPath(p[prefix.Length..]) ? p[prefix.Length..] : null;
        return p;
    }

    /// <summary>"C:\..." の形 (ドライブ相対の "C:foo" は含めない)。</summary>
    private static bool IsDriveLetterPath(string p)
        => p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';

    private static HashSet<string> TrustedHosts()
    {
        var settings = AppSettings.Current;
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            hosts.UnionWith(settings.TrustedNetworkHosts.ToList());
        }
        catch (InvalidOperationException)
        {
        }

        void Add(string? path)
        {
            if (HostOf(path) is { } host)
                hosts.Add(host);
        }

        try
        {
            // 裏スレッドから読むので、ちょうど UI 側で編集中だと列挙が失敗し得る。
            // そのときは手動の一覧と割り当てドライブだけで判定する (信頼しない側に倒れる)
            foreach (var favorite in settings.Favorites.ToList())
                Add(favorite);
            foreach (var path in GroupPaths(settings.Groups.ToList()).ToList())
                Add(path);
        }
        catch (InvalidOperationException)
        {
        }
        foreach (var remote in MappedDriveTargets())
            Add(remote);
        return hosts;
    }

    private static IEnumerable<string> GroupPaths(IEnumerable<FavoriteGroup> groups)
    {
        foreach (var group in groups)
        {
            foreach (var path in group.Paths)
                yield return path;
            foreach (var path in GroupPaths(group.Subgroups))
                yield return path;
        }
    }

    /// <summary>ネットワークドライブの接続先。記憶されたマッピング (HKCU\Network\&lt;文字&gt;\RemotePath) と、
    /// いま実際に繋がっている先の両方 (同じ NAS を LAN と VPN の別アドレスで繋ぎ替えることがあるため)。
    /// どちらもこの PC 内の情報で、接続先には問い合わせない。</summary>
    private static IEnumerable<string> MappedDriveTargets()
    {
        var result = new List<string>();
        try
        {
            using var network = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Network");
            if (network is not null)
            {
                foreach (var name in network.GetSubKeyNames())
                {
                    using var drive = network.OpenSubKey(name);
                    if (drive?.GetValue("RemotePath") is string remote)
                        result.Add(remote);
                }
            }
        }
        catch
        {
            // 読めなければ自動の信頼先が減るだけ (選んだだけでは開かなくなる側に倒れる)
        }

        try
        {
            foreach (var letter in Directory.GetLogicalDrives())
            {
                // GetDriveType はドライブ文字の種類を見るだけ (接続先に問い合わせない)
                if (new DriveInfo(letter).DriveType != DriveType.Network)
                    continue;
                var buffer = new System.Text.StringBuilder(512);
                var length = buffer.Capacity;
                if (WNetGetConnection(letter[..2], buffer, ref length) == 0 && buffer.Length > 0)
                    result.Add(buffer.ToString());
            }
        }
        catch
        {
        }
        return result;
    }

    [System.Runtime.InteropServices.DllImport("mpr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName, System.Text.StringBuilder remoteName, ref int length);
}
