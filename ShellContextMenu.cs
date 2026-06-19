using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ColumnView;

public enum ShellMenuResult
{
    None,
    ShellInvoked,   // シェルのコマンドを実行した (フォルダーを再読み込みすべき)
    Rename,         // 「名前の変更」が選ばれた (アプリ側で処理)
    AddFavorite,    // 独自項目: お気に入り
    CopyPath,       // 独自項目: パスをコピー
}

/// <summary>
/// Windows のシェル (エクスプローラー) と同じネイティブのコンテキストメニューを表示する。
/// IShellFolder::GetUIObjectOf から IContextMenu を取得し、TrackPopupMenuEx で表示する。
/// </summary>
public static class ShellContextMenu
{
    // 独自に追加するメニュー項目 (シェルの ID 範囲 1..0x7FFF と衝突しない値)
    private const uint IdFavorite = 0x9001;
    private const uint IdCopyPath = 0x9002;

    public static ShellMenuResult Show(IntPtr hwnd, string path, int screenX, int screenY)
    {
        var iidShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
        var iidContextMenu = new Guid("000214E4-0000-0000-C000-000000000046");

        if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            return ShellMenuResult.None;

        IntPtr folderPtr = IntPtr.Zero;
        IntPtr menuPtr = IntPtr.Zero;
        IntPtr hMenu = IntPtr.Zero;
        HwndSource? source = null;
        HwndSourceHook? hook = null;

        try
        {
            if (SHBindToParent(pidl, ref iidShellFolder, out folderPtr, out var childPidl) != 0 || folderPtr == IntPtr.Zero)
                return ShellMenuResult.None;

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            IntPtr[] apidl = { childPidl };
            if (folder.GetUIObjectOf(hwnd, 1, apidl, ref iidContextMenu, IntPtr.Zero, out menuPtr) != 0 || menuPtr == IntPtr.Zero)
                return ShellMenuResult.None;

            var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
            var menu2 = menu as IContextMenu2;
            var menu3 = menu as IContextMenu3;

            hMenu = CreatePopupMenu();
            const uint idFirst = 1, idLast = 0x7FFF;
            const uint CMF_EXPLORE = 0x04, CMF_CANRENAME = 0x10;
            menu.QueryContextMenu(hMenu, 0, idFirst, idLast, CMF_EXPLORE | CMF_CANRENAME);

            // 独自項目を末尾に追加
            AppendMenu(hMenu, 0x800 /*MF_SEPARATOR*/, IntPtr.Zero, null);
            AppendMenu(hMenu, 0 /*MF_STRING*/, (IntPtr)IdFavorite, "お気に入りに追加 / 削除");
            AppendMenu(hMenu, 0, (IntPtr)IdCopyPath, "パスをコピー");

            // サブメニュー (送る・新規作成など) の描画にはメニューメッセージの転送が必要
            source = HwndSource.FromHwnd(hwnd);
            if (source is not null && (menu3 is not null || menu2 is not null))
            {
                hook = (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    switch (msg)
                    {
                        case 0x0117: // WM_INITMENUPOPUP
                        case 0x002B: // WM_DRAWITEM
                        case 0x002C: // WM_MEASUREITEM
                        case 0x0120: // WM_MENUCHAR
                            if (menu3 is not null)
                            {
                                menu3.HandleMenuMsg2((uint)msg, w, l, out _);
                                handled = true;
                            }
                            else
                            {
                                menu2!.HandleMenuMsg((uint)msg, w, l);
                                handled = true;
                            }
                            break;
                    }
                    return IntPtr.Zero;
                };
                source.AddHook(hook);
            }

            const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
            uint cmd;
            try
            {
                cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, hwnd, IntPtr.Zero);
            }
            finally
            {
                if (hook is not null)
                    source?.RemoveHook(hook);
            }

            if (cmd == 0)
                return ShellMenuResult.None;
            if (cmd == IdFavorite)
                return ShellMenuResult.AddFavorite;
            if (cmd == IdCopyPath)
                return ShellMenuResult.CopyPath;

            // 「名前の変更」はホストにビューが無いとシェル側で機能しないのでアプリで処理
            var verb = GetVerb(menu, cmd - idFirst);
            if (string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
                return ShellMenuResult.Rename;

            var info = new CMINVOKECOMMANDINFO
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                hwnd = hwnd,
                lpVerb = (IntPtr)(cmd - idFirst),
                nShow = 1, // SW_SHOWNORMAL
            };
            menu.InvokeCommand(ref info);
            return ShellMenuResult.ShellInvoked;
        }
        catch
        {
            return ShellMenuResult.None;
        }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static string? GetVerb(IContextMenu menu, uint idCmd)
    {
        const uint GCS_VERBW = 0x00000004;
        var buffer = new byte[520];
        if (menu.GetCommandString((UIntPtr)idCmd, GCS_VERBW, IntPtr.Zero, buffer, 260) != 0)
            return null;
        var s = System.Text.Encoding.Unicode.GetString(buffer);
        var nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    // ---- P/Invoke ----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, [Out] byte[] pszName, uint cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, [Out] byte[] pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, [Out] byte[] pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }
}
