using System.Runtime.InteropServices;

namespace ColumnView;

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
