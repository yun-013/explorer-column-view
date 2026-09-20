using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ColumnView;

/// <summary>
/// アニメーション GIF の再生。WPF の Image は 1 コマ目しか描かないので、コマ・表示時間・
/// 各コマの後始末 (Disposal) を自分で読み、1 枚の画布に重ねながら見せる。
/// 読み込み (Load) はワーカースレッドで、描画 (Advance) は UI スレッドで行う。
/// </summary>
public sealed class GifAnimation
{
    /// <summary>コマを貯め込みすぎないための上限 (これを超える GIF は静止画として扱う)。</summary>
    private const int MaxFrames = 600;
    private const long MaxBytes = 128L * 1024 * 1024;

    private sealed record Frame(byte[] Pixels, Int32Rect Rect, TimeSpan Delay, int Disposal);

    private readonly Frame[] _frames;
    private readonly byte[] _canvas;
    private byte[]? _backup;
    private int _index;
    private int _pendingDisposal;
    private Int32Rect _pendingRect;

    public int Width { get; }
    public int Height { get; }

    private WriteableBitmap? _bitmap;

    /// <summary>画面に貼る描画先。WriteableBitmap は作ったスレッドのものになるので、
    /// 読み込み (ワーカースレッド) ではなく、初めて触る UI スレッドで作る。</summary>
    public WriteableBitmap Bitmap =>
        _bitmap ??= new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);

    private GifAnimation(Frame[] frames, int width, int height)
    {
        _frames = frames;
        Width = width;
        Height = height;
        _canvas = new byte[width * height * 4];
    }

    /// <summary>GIF を読み込む。1 コマだけ・大きすぎる・読めない場合は null (従来の静止画表示に任せる)。</summary>
    public static GifAnimation? Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count < 2 || decoder.Frames.Count > MaxFrames)
            return null;

        // 論理画面 (全コマを載せる画布) の大きさ
        var screen = decoder.Metadata as BitmapMetadata;
        int width = Query(screen, "/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth;
        int height = Query(screen, "/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight;
        if (width <= 0 || height <= 0)
            return null;
        if ((long)width * height * 4 * decoder.Frames.Count > MaxBytes)
            return null;

        var frames = new Frame[decoder.Frames.Count];
        for (int i = 0; i < frames.Length; i++)
        {
            var source = decoder.Frames[i];
            var meta = source.Metadata as BitmapMetadata;
            int left = Query(meta, "/imgdesc/Left") ?? 0;
            int top = Query(meta, "/imgdesc/Top") ?? 0;
            int w = Math.Min(source.PixelWidth, width - left);
            int h = Math.Min(source.PixelHeight, height - top);
            if (w <= 0 || h <= 0)
                return null;

            // GIF のコマはパレット画像なので、透明を保ったまま Bgra32 に揃える
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);

            // 表示時間は 1/100 秒単位。0 (や 1) は「できるだけ速く」の意味なので、ブラウザーと同じく 100ms とみなす
            int centiseconds = Query(meta, "/grctlext/Delay") ?? 0;
            var delay = TimeSpan.FromMilliseconds(centiseconds <= 1 ? 100 : centiseconds * 10);
            int disposal = Query(meta, "/grctlext/Disposal") ?? 0;

            frames[i] = new Frame(pixels, new Int32Rect(left, top, w, h), delay, disposal);
        }
        return new GifAnimation(frames, width, height);
    }

    /// <summary>メタデータの数値を読む (形式が持っていなければ null)。</summary>
    private static int? Query(BitmapMetadata? meta, string query)
    {
        try
        {
            return meta?.GetQuery(query) switch
            {
                ushort u => u,
                byte b => b,
                int i => i,
                _ => null,
            };
        }
        catch
        {
            return null; // このコマが持っていないクエリ
        }
    }

    /// <summary>次のコマを描き、そのコマを出しておく時間を返す。</summary>
    public TimeSpan Advance()
    {
        // 前のコマの後始末: 2 = その場所を透明に戻す / 3 = 描く前の状態に戻す。
        // 2 は仕様上は「背景色で塗る」だが、ブラウザーと同じく透明に戻す方に合わせる
        // (背景色を塗る実装は、透過 GIF が意図しない色で埋まって見える)
        if (_pendingDisposal == 2)
            ClearRect(_pendingRect);
        else if (_pendingDisposal == 3 && _backup is not null)
            Array.Copy(_backup, _canvas, _canvas.Length);

        var frame = _frames[_index];
        if (frame.Disposal == 3)
            _backup = (byte[])_canvas.Clone();
        Blend(frame);
        _pendingDisposal = frame.Disposal;
        _pendingRect = frame.Rect;

        Bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), _canvas, Width * 4, 0);
        _index = (_index + 1) % _frames.Length;
        return frame.Delay;
    }

    /// <summary>コマを画布に重ねる。GIF の透明部分 (α=0) は下のコマを残す。</summary>
    private void Blend(Frame frame)
    {
        var rect = frame.Rect;
        for (int y = 0; y < rect.Height; y++)
        {
            int src = y * rect.Width * 4;
            int dst = ((rect.Y + y) * Width + rect.X) * 4;
            for (int x = 0; x < rect.Width; x++, src += 4, dst += 4)
            {
                if (frame.Pixels[src + 3] == 0)
                    continue;
                Buffer.BlockCopy(frame.Pixels, src, _canvas, dst, 4);
            }
        }
    }

    private void ClearRect(Int32Rect rect)
    {
        for (int y = 0; y < rect.Height; y++)
            Array.Clear(_canvas, ((rect.Y + y) * Width + rect.X) * 4, rect.Width * 4);
    }
}
