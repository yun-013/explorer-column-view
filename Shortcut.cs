using System.Runtime.InteropServices;

namespace ColumnView;

public enum LinkTargetKind
{
    Folder,
    SavedSearch,
    /// <summary>信頼していないネットワーク上の場所を指す (<see cref="NetworkTrust"/>)。
    /// 選んだだけでは繋がず、Enter / ダブルクリックのときに改めて調べて開く。</summary>
    NeedsConfirmation,
}

/// <summary>列で開けるショートカットの行き先。Folder なら Path がフォルダ、
/// SavedSearch なら Path が .search-ms で Search がその中身、
/// NeedsConfirmation なら Path が信頼していない場所 (案内表示用)。</summary>
public sealed record LinkTarget(LinkTargetKind Kind, string Path, SavedSearch? Search);

/// <summary>
/// ショートカット (.lnk) の作成。Alt / Ctrl+Shift ドラッグの受け皿。
/// シェルの IShellLink を直接叩く (外部ライブラリなし・エクスプローラーと同じ形式)。
/// </summary>
public static class Shortcut
{
    /// <summary><paramref name="target"/> を指す .lnk を <paramref name="linkPath"/> に作る。</summary>
    public static void Create(string target, string linkPath)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        // 作業フォルダを親に合わせる。これが無いと相対パスで動くアプリの起動に失敗する
        var dir = System.IO.Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            link.SetWorkingDirectory(dir);
        ((IPersistFile)link).Save(linkPath, true);
        Marshal.FinalReleaseComObject(link);
    }

    /// <summary>ショートカットの中身 (プレビュー表示用)。</summary>
    public readonly record struct LinkInfo(string Target, string Arguments, string WorkingDirectory, string Description);

    /// <summary>.lnk の指す先などを読む。読めなければ null。</summary>
    public static LinkInfo? Read(string linkPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(linkPath, 0);
            // リンク先が見つからないときに検索ダイアログを出させない (UI を持たないプレビューのため)。
            // Resolve はリンク先を確かめに行くので、信頼していないサーバーを指すときはしない
            // (選択に追従するプレビューだけで、そのサーバーに繋ぎに行かせないため)
            if (NetworkTrust.IsTrusted(Get(b => link.GetPath(b, b.Capacity, IntPtr.Zero, 0))))
                link.Resolve(IntPtr.Zero, SLR_NO_UI | SLR_NOSEARCH | SLR_NOTRACK | SLR_NOUPDATE);
            return new LinkInfo(Get(b => link.GetPath(b, b.Capacity, IntPtr.Zero, 0)),
                                Get(b => link.GetArguments(b, b.Capacity)),
                                Get(b => link.GetWorkingDirectory(b, b.Capacity)),
                                Get(b => link.GetDescription(b, b.Capacity)));
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }

        static string Get(Action<System.Text.StringBuilder> read)
        {
            var buffer = new System.Text.StringBuilder(1024);
            try { read(buffer); } catch { return ""; }
            return buffer.ToString();
        }
    }

    /// <summary>ショートカット (.lnk) や保存された検索 (.search-ms) を列で開けるか調べる。
    /// フォルダを指すならその中身、保存された検索なら検索結果を次の列に出す。
    /// それ以外 (アプリやファイルを指す・読めない) は null で、従来どおり開く対象になる。
    /// リンク先やネットワークに触れることがあるので、裏スレッドから呼ぶこと。
    /// 信頼していないサーバー (<see cref="NetworkTrust"/>) には触れずに NeedsConfirmation を返す。
    /// <paramref name="allowUntrusted"/>=true (Enter / ダブルクリックで開くとき) ならそのまま調べる。</summary>
    public static LinkTarget? Classify(string path, bool allowUntrusted = false)
    {
        string target;
        var attributes = (System.IO.FileAttributes)0;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            if (ResolveTarget(path) is not { } resolved)
                return null;
            (target, attributes) = resolved;
        }
        else
        {
            target = path;
        }

        bool Trusted(string location) => allowUntrusted || NetworkTrust.IsTrusted(location);

        if (target.EndsWith(".search-ms", StringComparison.OrdinalIgnoreCase))
        {
            // .search-ms 自体も、その検索範囲も、信頼していない場所なら読みに行かない
            if (!Trusted(target))
                return new LinkTarget(LinkTargetKind.NeedsConfirmation, target, null);
            if (SavedSearch.Load(target) is not { } search)
                return null;
            if (search.Includes.FirstOrDefault(s => !Trusted(s.Path)) is { Path: { } untrusted })
                return new LinkTarget(LinkTargetKind.NeedsConfirmation, untrusted, null);
            return new LinkTarget(LinkTargetKind.SavedSearch, target, search);
        }

        // ファイルを指すもの (属性で分かる) は従来どおり「開く」対象。列には出さない
        if (attributes != 0 && (attributes & System.IO.FileAttributes.Directory) == 0)
            return null;
        // 書き方 ("\\?\UNC\..." や "//host/..." 等) によらず、信頼できない場所には触れない
        if (!Trusted(target))
            return new LinkTarget(LinkTargetKind.NeedsConfirmation, target, null);

        // 届かない共有の存在確認は数十秒返ってこない。.lnk に記録されたリンク先の属性が
        // あればそれで決め (届かなければ列側が「届きません」と出す)、無ければ短く確かめる
        if (!NetworkTrust.IsLocalPath(target))
        {
            if (attributes != 0)
                return new LinkTarget(LinkTargetKind.Folder, target, null);
            if (!Reachability.IsReachableAsync(target).GetAwaiter().GetResult())
                return null;
        }
        return System.IO.Directory.Exists(target) ? new LinkTarget(LinkTargetKind.Folder, target, null) : null;
    }

    /// <summary>このファイルのアイコン・サムネイルをシェルに作らせてよいか。.lnk / .url 等はシェルが
    /// アイコンの置き場所 (無ければリンク先) を読みに行くので、どちらかが信頼していないサーバーに
    /// あると、フォルダを表示しただけでそこへ繋ぎに行ってしまう。
    /// .lnk / .url は中身を読んで、安全と確かめられたときだけ true (分からなければ汎用アイコン)。
    /// アイコンの置き場所を中身に書ける既知の形式は常に false。それ以外の形式は true。</summary>
    public static bool IsSafeForShell(string path) => IsSafeForShell(path, depth: 0);

    /// <summary>中身にアイコン / 画像の置き場所を書けるが、ここでは読み解かない形式。
    /// シェルに任せず汎用アイコンで済ませる (どれも滅多に見ないので見た目の損は小さい)。</summary>
    private static readonly HashSet<string> OpaqueIconCarriers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".scf", ".library-ms", ".searchconnector-ms", ".appref-ms", ".website", ".pif",
        ".theme", ".themepack", ".deskthemepack",
    };

    /// <summary>アイコンの置き場所を自前で持たない .lnk のとき、リンク先として許す形式。
    /// シェルはリンク先の種類のアイコン (この PC の登録情報) か、実行ファイル自身の
    /// アイコン (ローカルのファイルの中身) を使うだけで、中身に書かれた別の場所は読まない。
    /// ここに無い形式を指すリンクは汎用アイコンにする (知らない形式には寄せない)。</summary>
    private static readonly HashSet<string> InertTargetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".msc", ".cpl", ".dll", ".ico",
        ".txt", ".md", ".log", ".csv", ".json", ".xml", ".html", ".htm", ".pdf",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic",
        ".mp3", ".m4a", ".wav", ".flac", ".mp4", ".mov", ".mkv", ".avi", ".zip", ".7z",
    };

    /// <summary>シェルが実際に開くファイル名に近づけた拡張子: ":stream" を除き、
    /// 末尾の "." と空白を落とす (Windows はそれらを無視して開くため)。</summary>
    private static string CleanExtension(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        var colon = name.IndexOf(':');
        if (colon >= 0)
            name = name[..colon];
        return System.IO.Path.GetExtension(name.TrimEnd('.', ' '));
    }

    private static bool IsSafeForShell(string path, int depth)
    {
        var extension = CleanExtension(path);
        if (OpaqueIconCarriers.Contains(extension))
            return false;
        if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return IsSafeInternetShortcut(path);
        if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return true;
        if (depth >= 2 || ResolveTarget(path) is not { } target || !NetworkTrust.IsTrusted(target.Path))
            return false;

        string iconLocation;
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var buffer = new System.Text.StringBuilder(1024);
            link.GetIconLocation(buffer, buffer.Capacity, out _);
            iconLocation = Environment.ExpandEnvironmentVariables(buffer.ToString());
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }

        // アイコンの置き場所を自前で持つなら、シェルはそこだけを読む (リンク先には聞かない)。
        // 置き場所自体がアイコンの置き場所を書ける形式なら許さない
        if (iconLocation.Length > 0)
        {
            var iconExtension = CleanExtension(iconLocation);
            return NetworkTrust.IsTrusted(iconLocation)
                && !OpaqueIconCarriers.Contains(iconExtension)
                && !iconExtension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                && !iconExtension.Equals(".url", StringComparison.OrdinalIgnoreCase);
        }

        // 置き場所が無ければリンク先のアイコンになる
        var targetExtension = CleanExtension(target.Path);
        if (targetExtension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || targetExtension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return IsSafeForShell(target.Path, depth + 1);
        // .lnk に記録された属性は書き換えられるので使わず、実物で確かめる
        // (リンク先は信頼できる場所だと確認済みなので、触れてよい)。
        // 「x.exe」という名前のフォルダもあり得るので、フォルダかどうかを先に見る
        if (System.IO.Directory.Exists(target.Path))
            return IsSafeFolderIcon(target.Path);
        return System.IO.File.Exists(target.Path) && InertTargetExtensions.Contains(targetExtension);
    }

    /// <summary>フォルダの desktop.ini に書かれたアイコンの置き場所 (IconResource / IconFile) が
    /// 信頼できるか。desktop.ini が無ければ true、読めなければ false。</summary>
    private static bool IsSafeFolderIcon(string folder)
    {
        var ini = System.IO.Path.Combine(folder, "desktop.ini");
        try
        {
            if (!System.IO.File.Exists(ini))
                return true;
            foreach (var raw in System.IO.File.ReadLines(ini))
            {
                var line = raw.Trim();
                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = line[..eq].Trim();
                // Logo はフォルダの縮小表示に使う画像の置き場所
                if (!key.Equals("IconResource", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("IconFile", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("Logo", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsSafeFolderIconValue(folder, line[(eq + 1)..]))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>desktop.ini の IconResource / IconFile の値 ("場所,番号") が信頼できるか。
    /// Windows と同じく前後の空白と引用符を外してから判断し、それでも読み解けない書き方
    /// (引用符が残る・途中に ":" がある等) は信頼しない。相対の名前はフォルダの中に
    /// 収まるときだけ許す。</summary>
    private static bool IsSafeFolderIconValue(string folder, string rawValue)
    {
        static string Unquote(string s) => s.Trim().Trim('"').Trim();

        var value = Environment.ExpandEnvironmentVariables(Unquote(Unquote(rawValue).Split(',')[0]));
        if (value.Length == 0)
            return true;
        if (value.Contains('"'))
            return false;
        // 絶対パス ("C:\..." / UNC / デバイス名前空間) は信頼の判定にそのまま任せる
        if (value.StartsWith(@"\", StringComparison.Ordinal) || value.StartsWith("/", StringComparison.Ordinal)
            || System.IO.Path.IsPathFullyQualified(value))
            return NetworkTrust.IsTrusted(value);
        // ここに来るのは相対の名前だけ。ドライブ相対 ("C:x") や ":" 入りは読み解かない
        if (value.Contains(':'))
            return false;
        try
        {
            var root = System.IO.Path.GetFullPath(folder).TrimEnd('\\') + @"\";
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, value));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && NetworkTrust.IsTrusted(full);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>インターネット ショートカット (.url) をシェルに読ませてよいか。シェルを通さずテキストとして読み、
    /// URL は http / https / mailto だけ、IconFile は信頼できる場所だけを許す。
    /// 別の文字コードで値を書き直せる [InternetShortcut.W] / [InternetShortcut.A] 節があれば許さない。</summary>
    private static bool IsSafeInternetShortcut(string path)
    {
        try
        {
            foreach (var raw in System.IO.File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("[InternetShortcut.", StringComparison.OrdinalIgnoreCase))
                    return false;
                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = line[..eq].Trim();
                var value = Environment.ExpandEnvironmentVariables(line[(eq + 1)..].Trim());
                if (value.Length == 0)
                    continue;
                if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase) && !NetworkTrust.IsTrusted(value))
                    return false;
                if (key.Equals("URL", StringComparison.OrdinalIgnoreCase)
                    && !(Uri.TryCreate(value, UriKind.Absolute, out var uri)
                         && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto)))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>.lnk に記録されたリンク先 (実パス) とその属性。リンク先を確かめに行かない
    /// (Resolve しない) ので、届かない共有を指していても止まらない。読めなければ null。</summary>
    private static (string Path, System.IO.FileAttributes Attributes)? ResolveTarget(string linkPath)
    {
        var link = (IShellLinkW)new ShellLink();
        // WIN32_FIND_DATAW (592 バイト)。先頭の DWORD がリンク先の属性
        var findData = Marshal.AllocHGlobal(600);
        try
        {
            ((IPersistFile)link).Load(linkPath, 0);
            var buffer = new System.Text.StringBuilder(1024);
            Marshal.WriteInt32(findData, 0);
            link.GetPath(buffer, buffer.Capacity, findData, 0);
            var attributes = (System.IO.FileAttributes)Marshal.ReadInt32(findData);
            if (buffer.Length > 0)
                return (Environment.ExpandEnvironmentVariables(buffer.ToString()), attributes);

            // ID リストだけを持つリンク (シェル名前空間の中を指すもの) は、実パスに直せれば使う
            link.GetIDList(out var pidl);
            if (pidl == IntPtr.Zero)
                return null;
            try
            {
                return PathFromIDList(pidl) is { } path ? (path, (System.IO.FileAttributes)0) : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(findData);
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static string? PathFromIDList(IntPtr pidl)
    {
        var buffer = new System.Text.StringBuilder(32768);
        return SHGetPathFromIDListEx(pidl, buffer, buffer.Capacity, 0) && buffer.Length > 0
            ? buffer.ToString()
            : null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListEx(IntPtr pidl, System.Text.StringBuilder path, int cchPath, int options);

    private const int SLR_NO_UI = 0x1;
    private const int SLR_NOUPDATE = 0x8;
    private const int SLR_NOSEARCH = 0x10;
    private const int SLR_NOTRACK = 0x20;

    /// <summary>衝突しない .lnk のパスを返す (「◯◯ - ショートカット.lnk」、既にあれば連番)。</summary>
    /// <param name="isDirectory">フォルダなら名前をそのまま使う。"my.folder" のような名前から
    /// 拡張子と誤認して ".folder" を落とさないため (エクスプローラーも落とさない)。</param>
    public static string UniqueLinkPath(string targetDir, string sourceName, bool isDirectory)
    {
        var stem = isDirectory ? sourceName : System.IO.Path.GetFileNameWithoutExtension(sourceName);
        var baseName = stem + " - ショートカット";
        var path = System.IO.Path.Combine(targetDir, baseName + ".lnk");
        for (int n = 2; System.IO.File.Exists(path); n++)
            path = System.IO.Path.Combine(targetDir, $"{baseName} ({n}).lnk");
        return path;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file,
                     int maxPath, IntPtr findData, int flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxArgs);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder icon,
                             int maxIcon, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName,
                  [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
