using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using MediaPlayer = Windows.Media.Playback.MediaPlayer;

namespace ColumnView;

/// <summary>
/// WPF の MediaElement (Windows Media Player 系の古い再生基盤) が開けない形式
/// (ogg / opus / webm など) を、Media Foundation (Windows.Media.Playback) で再生する代替プレーヤー。
/// 映像は「フレームサーバー」モードで 1 コマずつ GPU の面に受け取り、WriteableBitmap に写して見せる。
/// イベントはすべて UI スレッドで発火する。
/// </summary>
public sealed class MfPlayback : IDisposable
{
    private readonly MediaPlayer _player;
    private readonly Dispatcher _ui;
    private volatile bool _disposed;

    // 映像フレームの受け渡し (ワーカー → UI)。_busy が立っている間は次のコマを捨てる
    private IDirect3DSurface? _surface;
    private byte[]? _pixels;
    private int _busy;

    public event Action? Opened;
    public event Action? Failed;
    public event Action? Ended;

    /// <summary>映像の描画先 (音声のみなら null)。MediaOpened 後に作られる。</summary>
    public WriteableBitmap? Video { get; private set; }

    public int VideoWidth => (int)_player.PlaybackSession.NaturalVideoWidth;
    public int VideoHeight => (int)_player.PlaybackSession.NaturalVideoHeight;

    public MfPlayback(string path, Dispatcher ui)
    {
        _ui = ui;
        _player = new MediaPlayer { AutoPlay = false, IsVideoFrameServerEnabled = true };
        _player.MediaOpened += (_, _) => _ui.BeginInvoke(OnOpened);
        _player.MediaFailed += (_, _) => _ui.BeginInvoke(() => { if (!_disposed) Failed?.Invoke(); });
        _player.MediaEnded += (_, _) => _ui.BeginInvoke(() => { if (!_disposed) Ended?.Invoke(); });
        _player.VideoFrameAvailable += OnVideoFrameAvailable;
        _player.Source = MediaSource.CreateFromUri(new Uri(path));
    }

    public TimeSpan? Duration
    {
        get
        {
            var d = _player.PlaybackSession.NaturalDuration;
            return d > TimeSpan.Zero ? d : null;
        }
    }

    public TimeSpan Position
    {
        get => _player.PlaybackSession.Position;
        set => _player.PlaybackSession.Position = value;
    }

    public void Play() => _player.Play();
    public void Pause() => _player.Pause();

    private void OnOpened()
    {
        if (_disposed)
            return;
        int w = VideoWidth, h = VideoHeight;
        if (w > 0 && h > 0)
            Video = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        Opened?.Invoke();
    }

    private async void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (_disposed || Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            int w = VideoWidth, h = VideoHeight;
            if (w <= 0 || h <= 0)
            {
                _busy = 0;
                return;
            }
            _surface ??= D3DSurface.Create(w, h);
            sender.CopyFrameToVideoSurface(_surface);
            using var frame = await SoftwareBitmap.CreateCopyFromSurfaceAsync(_surface, BitmapAlphaMode.Premultiplied);
            if (_disposed || frame.PixelWidth != w || frame.PixelHeight != h)
            {
                _busy = 0;
                return;
            }
            _pixels ??= new byte[w * h * 4];
            frame.CopyToBuffer(_pixels.AsBuffer());
            _ = _ui.BeginInvoke(() =>
            {
                try
                {
                    if (!_disposed && Video is { } bmp && bmp.PixelWidth == w && bmp.PixelHeight == h)
                        bmp.WritePixels(new Int32Rect(0, 0, w, h), _pixels, w * 4, 0);
                }
                finally { _busy = 0; }
            });
        }
        catch
        {
            // 取りこぼした 1 コマは捨てる (終了直後・デバイス喪失など)
            _busy = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _player.VideoFrameAvailable -= OnVideoFrameAvailable;
        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch { /* 既に閉じていれば無視 */ }
        _player.Dispose();
        // _surface は処理中のコマが使っている可能性があるので明示的には閉じず、GC に任せる
    }

    /// <summary>フレームサーバーの受け取り先になる D3D11 の面 (BGRA) を作る。</summary>
    private static class D3DSurface
    {
        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        private const uint D3D11_SDK_VERSION = 7;
        private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
        private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
        private const uint D3D11_BIND_RENDER_TARGET = 0x20;

        private static readonly Guid IID_IDXGISurface = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
        private static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize, Format;
            public uint SampleCount, SampleQuality;
            public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint numFeatureLevels, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr immediateContext);

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetMultithreadProtectedFn(IntPtr self, int protect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateTexture2DFn(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture);

        /// <summary>COM オブジェクトの vtable から index 番目のメソッドを取り出す。</summary>
        private static T VtableMethod<T>(IntPtr com, int index) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), index * IntPtr.Size));

        public static IDirect3DSurface Create(int width, int height)
        {
            IntPtr device = IntPtr.Zero, context = IntPtr.Zero, texture = IntPtr.Zero;
            IntPtr dxgi = IntPtr.Zero, mt = IntPtr.Zero, inspectable = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                    out device, out _, out context));

                // Media Foundation が別スレッドからこのデバイスに触れるので多重スレッド保護を有効にする
                var iidMt = IID_ID3D10Multithread;
                if (Marshal.QueryInterface(device, ref iidMt, out mt) == 0)
                {
                    // ID3D10Multithread::SetMultithreadProtected (vtable 5)
                    VtableMethod<SetMultithreadProtectedFn>(mt, 5)(mt, 1);
                }

                var desc = new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                    Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
                    BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE,
                };
                // ID3D11Device::CreateTexture2D (vtable 5)
                Marshal.ThrowExceptionForHR(
                    VtableMethod<CreateTexture2DFn>(device, 5)(device, ref desc, IntPtr.Zero, out texture));

                var iidDxgi = IID_IDXGISurface;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(texture, ref iidDxgi, out dxgi));
                Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgi, out inspectable));
                // 面が テクスチャ → デバイス の参照を保つので、ここで作った生ポインタは全部解放してよい
                return WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(inspectable);
            }
            finally
            {
                foreach (var p in new[] { inspectable, dxgi, texture, mt, context, device })
                    if (p != IntPtr.Zero)
                        Marshal.Release(p);
            }
        }
    }
}
