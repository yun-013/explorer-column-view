using System.Runtime.InteropServices;

namespace ColumnView;

public enum LinkTargetKind { Folder, SavedSearch }

/// <summary>列で開けるショートカットの行き先。Folder なら Path がフォルダ、
/// SavedSearch なら Path が .search-ms で Search がその中身。</summary>
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
            // リンク先が見つからないときに検索ダイアログを出させない (UI を持たないプレビューのため)
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
    /// リンク先やネットワークに触れることがあるので、裏スレッドから呼ぶこと。</summary>
    public static LinkTarget? Classify(string path)
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

        if (target.EndsWith(".search-ms", StringComparison.OrdinalIgnoreCase))
            return SavedSearch.Load(target) is { } search
                ? new LinkTarget(LinkTargetKind.SavedSearch, target, search)
                : null;

        // 届かない共有の存在確認は数十秒返ってこない。.lnk に記録されたリンク先の属性が
        // あればそれで決め (届かなければ列側が「届きません」と出す)、無ければ短く確かめる
        if (Reachability.IsNetworkPath(target))
        {
            if (attributes != 0)
                return (attributes & System.IO.FileAttributes.Directory) != 0
                    ? new LinkTarget(LinkTargetKind.Folder, target, null)
                    : null;
            if (!Reachability.IsReachableAsync(target).GetAwaiter().GetResult())
                return null;
        }
        return System.IO.Directory.Exists(target) ? new LinkTarget(LinkTargetKind.Folder, target, null) : null;
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

    /// <summary>シェルの解析名 ("::{CLSID}\storage" など) を実パスに直す。
    /// ファイルシステム上に無い場所なら null。</summary>
    public static string? PathFromParsingName(string parsingName)
    {
        if (SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            return null;
        try
        {
            return PathFromIDList(pidl);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
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
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

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
