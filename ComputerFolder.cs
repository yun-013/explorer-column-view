using System.IO;
using System.Runtime.InteropServices;

namespace ColumnView;

/// <summary>
/// シェル名前空間の「PC」直下を走査し、<c>DriveInfo.GetDrives()</c> では見えない項目を拾う。
/// エクスプローラーの「PC」に並ぶのにホーム列に出ない、という食い違いを埋めるのが役目:
///  - ポータブルデバイス (iPhone・Android・カメラ等の MTP/PTP)。ドライブ文字を持たない
///  - 切断中のネットワークドライブ。ドライブ文字は記憶されているが未接続のため
///    <c>GetLogicalDrives()</c> に載らず、DriveInfo からは存在ごと見えない
/// </summary>
public static class ComputerFolder
{
    /// <summary>「PC」直下の 1 項目。</summary>
    /// <param name="Name">シェルの表示名 (例: "Apple iPhone" / "nas (\\192.168.11.25) (Z:)")。</param>
    /// <param name="Path">ポータブルデバイスなら "::{20D04FE0-...}\\?\usb#..." 形式の
    /// シェル解析名 (System.IO では開けない → 開くときはエクスプローラーに渡す)。
    /// ドライブなら "Z:\" のような実パス。</param>
    /// <param name="IsPortableDevice">true ならシェル解析名、false なら実パス。</param>
    /// <param name="Capacity">総容量。取れなければ 0。</param>
    public sealed record Entry(string Name, string Path, bool IsPortableDevice, long Capacity, long FreeSpace);

    /// <summary>シェル名前空間の走査は最長でもこの時間で打ち切る。
    /// 応答しない機器やシェル拡張にホーム列の読み込みを道連れにさせない。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>容量合算で見るストレージの上限 (SD カード付きの機器でも数個)。</summary>
    private const int MaxStorages = 8;

    /// <summary>DriveInfo では拾えない「PC」直下の項目を列挙する。取得できなければ空。</summary>
    public static List<Entry> EnumerateMissing()
    {
        // 既に DriveInfo 側で見えているドライブ文字。ここに載っていないドライブだけを足す
        // (空の DVD ドライブのように「文字はあるが未挿入」のものは従来どおり出さない)。
        HashSet<string> knownDrives;
        try
        {
            knownDrives = new HashSet<string>(Directory.GetLogicalDrives(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            knownDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        List<Entry> result = new();

        // シェル名前空間拡張 (WPD) は STA 前提のものが多いため、スレッドプール (MTA)
        // ではなく使い捨ての STA スレッドで触る。
        var thread = new Thread(() =>
        {
            try
            {
                result = EnumerateCore(knownDrives);
            }
            catch
            {
                // 機器が抜かれた・シェル拡張が失敗した等は「該当なし」として扱う
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(Timeout) ? result : new List<Entry>();
    }

    /// <summary>シェル解析名で示される項目をエクスプローラーで開く。
    /// "::{...}" 形式は ShellExecute では開けないので PIDL 経由で開く。</summary>
    public static bool OpenInExplorer(string parsingPath)
    {
        if (SHParseDisplayName(parsingPath, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            return false;
        try
        {
            // cidl=0 は「そのフォルダ自体を開く」
            return SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0) == 0;
        }
        finally
        {
            ILFree(pidl);
        }
    }

    private static List<Entry> EnumerateCore(HashSet<string> knownDrives)
    {
        var entries = new List<Entry>();

        SHGetKnownFolderItem(FOLDERID_ComputerFolder, 0, IntPtr.Zero, IID_IShellItem, out var computer);
        if (computer is null)
            return entries;

        try
        {
            if (computer.BindToHandler(IntPtr.Zero, BHID_EnumItems, IID_IEnumShellItems, out var handler) != 0
                || handler is not IEnumShellItems items)
                return entries;

            try
            {
                while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1 && item is not null)
                {
                    try
                    {
                        if (item.GetAttributes(SFGAO_FOLDER | SFGAO_FILESYSTEM, out var attributes) < 0
                            || (attributes & SFGAO_FOLDER) == 0)
                            continue;

                        if (DisplayName(item, SIGDN_NORMALDISPLAY) is not { Length: > 0 } name
                            || DisplayName(item, SIGDN_DESKTOPABSOLUTEPARSING) is not { Length: > 0 } path)
                            continue;

                        // ファイルシステム上に実体がある項目 = ドライブか「ドキュメント」等の
                        // ユーザーフォルダ。ドライブのルートだけを見て、DriveInfo が知らない
                        // 文字 (= 切断中のネットワークドライブ) なら足す。
                        if ((attributes & SFGAO_FILESYSTEM) != 0)
                        {
                            if (IsDriveRoot(path) && !knownDrives.Contains(path))
                                entries.Add(new Entry(name, path, IsPortableDevice: false, 0, 0));
                            continue;
                        }

                        // 実体が無いフォルダ = MTP/PTP デバイス
                        var (capacity, free) = ReadCapacity(item);
                        if (capacity == 0)
                            (capacity, free) = SumStorageCapacity(item);
                        entries.Add(new Entry(name, path, IsPortableDevice: true, capacity, free));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(items);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(computer);
        }

        return entries;
    }

    /// <summary>"Z:\" のようなドライブのルートか。</summary>
    private static bool IsDriveRoot(string path)
        => path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    /// <summary>記憶されているのに未接続のネットワークドライブを、保存済みの資格情報で
    /// 静かに繋ぎ直す。1 つでも繋がったら true。
    /// 資格情報ダイアログは出さない — 裏で勝手に出ると邪魔なので、
    /// 出すのは明示操作 (<see cref="ReconnectDrive"/> の interactive) のときだけ。</summary>
    public static bool ReconnectRememberedDrives()
    {
        // 起動直後やネットワーク復帰の通知が固まって飛んでくるので、同時実行を 1 本に絞る
        if (Interlocked.Exchange(ref _reconnecting, 1) == 1)
            return false;
        try
        {
            HashSet<string> mounted;
            try
            {
                mounted = new HashSet<string>(Directory.GetLogicalDrives(), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }

            var connected = false;
            foreach (var root in RememberedDrives())
            {
                if (mounted.Contains(root) || !DueForRetry(root))
                    continue;
                if (ReconnectDrive(root, interactive: false) is null)
                    connected = true;
            }
            return connected;
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    /// <summary>記憶されたマッピングのドライブルート ("Z:\" 等)。</summary>
    private static IEnumerable<string> RememberedDrives()
    {
        Microsoft.Win32.RegistryKey? network = null;
        try
        {
            network = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Network");
            if (network is null)
                return Array.Empty<string>();
            return network.GetSubKeyNames()
                .Where(n => n.Length == 1 && char.IsAsciiLetter(n[0]))
                .Select(n => char.ToUpperInvariant(n[0]) + @":\")
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
        finally
        {
            network?.Dispose();
        }
    }

    /// <summary>繋がらない相手に何度も待たされないよう、失敗後はしばらく間を空ける。</summary>
    private static bool DueForRetry(string root)
    {
        lock (_retryLock)
        {
            if (_lastAttempt.TryGetValue(root, out var last) && DateTime.UtcNow - last < RetryInterval)
                return false;
            _lastAttempt[root] = DateTime.UtcNow;
            return true;
        }
    }

    private static int _reconnecting;
    private static readonly object _retryLock = new();
    private static readonly Dictionary<string, DateTime> _lastAttempt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);

    /// <summary>切断中のネットワークドライブに繋ぎ直す。成功なら null、失敗なら理由。
    /// 未接続の間はドライブ文字が存在しないので、System.IO でアクセスしても
    /// Windows は繋ぎ直してくれない (エクスプローラーは開くときに明示的に繋ぎ直している)。
    /// 接続先は記憶されたマッピング (HKCU\Network\&lt;文字&gt;) から取る。
    /// <paramref name="interactive"/>=true なら、保存済みの資格情報で足りないときに
    /// Windows 自身の資格情報ダイアログが出る。</summary>
    public static string? ReconnectDrive(string root, bool interactive = true)
    {
        if (!IsDriveRoot(root))
            return "ネットワークドライブではありません";

        string? remote;
        try
        {
            remote = Microsoft.Win32.Registry.GetValue(
                $@"HKEY_CURRENT_USER\Network\{root[0]}", "RemotePath", null) as string;
        }
        catch
        {
            remote = null;
        }
        if (string.IsNullOrEmpty(remote))
            return "接続先が記録されていません";

        var resource = new NETRESOURCE
        {
            dwType = RESOURCETYPE_DISK,
            lpLocalName = root[..2], // "Z:"
            lpRemoteName = remote,
        };
        // 資格情報は Windows 任せ — 保存済みがあればそれを使う。interactive のときは
        // 足りなければ Windows 自身の資格情報ダイアログが出る
        // (パスワードをこのアプリが受け取ることはない)。
        // CONNECT_UPDATE_PROFILE で「記憶する」設定を維持する。
        var flags = CONNECT_UPDATE_PROFILE | (interactive ? CONNECT_INTERACTIVE : 0);
        var error = WNetAddConnection2(ref resource, null, null, flags);
        return error == 0 ? null : new System.ComponentModel.Win32Exception((int)error).Message;
    }

    /// <summary>機器本体には容量が載っていないことが多い (iPhone なら直下の
    /// 「Internal Storage」が持つ) ので、直下のストレージを合算する。</summary>
    private static (long Capacity, long Free) SumStorageCapacity(IShellItem device)
    {
        if (device.BindToHandler(IntPtr.Zero, BHID_EnumItems, IID_IEnumShellItems, out var handler) != 0
            || handler is not IEnumShellItems storages)
            return (0, 0);

        long capacity = 0, free = 0;
        try
        {
            var seen = 0;
            while (seen++ < MaxStorages
                   && storages.Next(1, out var storage, out var fetched) == 0 && fetched == 1 && storage is not null)
            {
                try
                {
                    var (storageCapacity, storageFree) = ReadCapacity(storage);
                    capacity += storageCapacity;
                    free += storageFree;
                }
                finally
                {
                    Marshal.ReleaseComObject(storage);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(storages);
        }

        return (capacity, free);
    }

    private static string? DisplayName(IShellItem item, uint kind)
    {
        if (item.GetDisplayName(kind, out var ptr) != 0 || ptr == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    /// <summary>項目の総容量 / 空き容量。取れなければ (0, 0)。
    /// ロック中の iPhone のように中身を見せない機器では取れないことがある。</summary>
    private static (long Capacity, long Free) ReadCapacity(IShellItem item)
    {
        if (item is not IShellItem2 item2)
            return (0, 0);
        var capacityKey = PKEY_Capacity;
        var freeKey = PKEY_FreeSpace;
        if (item2.GetUInt64(ref capacityKey, out var capacity) != 0 || capacity == 0
            || item2.GetUInt64(ref freeKey, out var free) != 0 || free > capacity)
            return (0, 0);
        return ((long)capacity, (long)free);
    }

    // ---- P/Invoke ----

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetKnownFolderItem(
        in Guid rfid, uint flags, IntPtr hToken, in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr apidl, uint dwFlags);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
    private static extern uint WNetAddConnection2(ref NETRESOURCE netResource, string? password, string? username, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public uint dwScope;
        public uint dwType;
        public uint dwDisplayType;
        public uint dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    private const uint RESOURCETYPE_DISK = 0x00000001;
    private const uint CONNECT_UPDATE_PROFILE = 0x00000001;
    private const uint CONNECT_INTERACTIVE = 0x00000008;

    private static readonly Guid FOLDERID_ComputerFolder = new("0AC0837C-BBF8-452A-850D-79D08E667CA7");
    private static readonly Guid BHID_EnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");
    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid IID_IEnumShellItems = new("70629033-E363-4A28-A567-0DB78006E6D7");

    private const uint SIGDN_NORMALDISPLAY = 0x00000000;
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private const uint SFGAO_FOLDER = 0x20000000;
    private const uint SFGAO_FILESYSTEM = 0x40000000;

    // PKEY_Capacity / PKEY_FreeSpace = {9B174B35-40FF-11D2-A27E-00C04FC30871}, 3 / 2
    private static readonly PROPERTYKEY PKEY_Capacity = new()
    {
        fmtid = new Guid("9B174B35-40FF-11D2-A27E-00C04FC30871"),
        pid = 3,
    };

    private static readonly PROPERTYKEY PKEY_FreeSpace = new()
    {
        fmtid = new Guid("9B174B35-40FF-11D2-A27E-00C04FC30871"),
        pid = 2,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object? ppv);
        [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem? ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-E363-4A28-A567-0DB78006E6D7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem? rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumShellItems? ppenum);
    }

    /// <summary>容量の読み出しにだけ使う。IShellItem からの QI で取得するので、
    /// IShellItem 側のメソッドも vtable 順に並べておく必要がある。</summary>
    [ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // IShellItem
        [PreserveSig] int BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
        // IShellItem2 (使うのは GetUInt64 だけだが、vtable を合わせるため全て並べる)
        [PreserveSig] int GetPropertyStore(uint flags, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreWithCreateObject(uint flags, IntPtr punkCreateObject, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, uint flags, in Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, in Guid riid, out IntPtr ppv);
        [PreserveSig] int Update(IntPtr pbc);
        [PreserveSig] int GetProperty(ref PROPERTYKEY key, IntPtr ppropvar);
        [PreserveSig] int GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
        [PreserveSig] int GetFileTime(ref PROPERTYKEY key, out long pft);
        [PreserveSig] int GetInt32(ref PROPERTYKEY key, out int pi);
        [PreserveSig] int GetString(ref PROPERTYKEY key, out IntPtr ppsz);
        [PreserveSig] int GetUInt32(ref PROPERTYKEY key, out uint pui);
        [PreserveSig] int GetUInt64(ref PROPERTYKEY key, out ulong pull);
        [PreserveSig] int GetBool(ref PROPERTYKEY key, out int pf);
    }
}
