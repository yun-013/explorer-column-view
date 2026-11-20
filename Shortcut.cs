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
    /// あると、フォルダを表示しただけでそこへ繋ぎに行ってしまう。判別できないもの
    /// (パスを持たないリンク・中身を読み解かない形式) も避ける。それ以外の形式は true。</summary>
    public static bool IsSafeForShell(string linkPath) => IsSafeForShell(linkPath, depth: 0);

    /// <summary>中身にアイコンの置き場所を書けるが、ここでは読み解かない形式。
    /// シェルに任せず汎用アイコンで済ませる (どれも滅多に見ないので見た目の損は小さい)。</summary>
    private static readonly HashSet<string> OpaqueIconCarriers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".scf", ".library-ms", ".searchconnector-ms", ".appref-ms", ".website",
    };

    private static bool IsSafeForShell(string path, int depth)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (OpaqueIconCarriers.Contains(extension))
            return false;
        if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return IsSafeInternetShortcut(path);
        if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ResolveTarget(path) is not { } target || !NetworkTrust.IsTrusted(target.Path))
            return false;
        // リンク先が .url や別の .lnk 等なら、シェルはそちらのアイコンの置き場所まで読みに行く
        var targetExtension = System.IO.Path.GetExtension(target.Path);
        var targetCarriesIcon = targetExtension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || targetExtension.Equals(".url", StringComparison.OrdinalIgnoreCase)
            || OpaqueIconCarriers.Contains(targetExtension);
        if (targetCarriesIcon && (depth >= 2 || !IsSafeForShell(target.Path, depth + 1)))
            return false;

        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var buffer = new System.Text.StringBuilder(1024);
            link.GetIconLocation(buffer, buffer.Capacity, out _);
            return buffer.Length == 0
                || NetworkTrust.IsTrusted(Environment.ExpandEnvironmentVariables(buffer.ToString()));
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>インターネット ショートカット (.url) のアイコンの置き場所 (IconFile) と、
    /// file: の URL が指す先が信頼できるか。シェルを通さず、テキストとして読む。</summary>
    private static bool IsSafeInternetShortcut(string path)
    {
        try
        {
            foreach (var raw in System.IO.File.ReadLines(path))
            {
                var line = raw.Trim();
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
                    && value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    // file://host/share/x の LocalPath は "\\host\share\x" になる
                    && !(Uri.TryCreate(value, UriKind.Absolute, out var uri) && NetworkTrust.IsTrusted(uri.LocalPath)))
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
