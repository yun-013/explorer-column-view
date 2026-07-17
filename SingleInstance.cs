using System.IO;
using System.IO.Pipes;
using System.Text;

namespace ColumnView;

/// <summary>単一インスタンス連携。フォルダーの既定アプリとして起動されるたびに
/// 新しいプロセスが立つので、既に起動中のインスタンスへ名前付きパイプで
/// 「このフォルダーを開いて」と転送し、自分はすぐ終了する。</summary>
internal static class SingleInstance
{
    /// <summary>先着判定用ミューテックス名 (Local\ = 同一ログオンセッション内)。</summary>
    public static string MutexName => @"Local\ColumnView.SingleInstance";

    // ユーザー名とセッション ID を含めて他ユーザー/他セッションと衝突させない
    private static string PipeName =>
        $"ColumnView.OpenFolder.{Environment.UserName}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    /// <summary>既存インスタンスへフォルダーパスを送る。届けば true (自分は終了してよい)。</summary>
    public static bool TrySendToExisting(string folder)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(folder);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            // 相手が死んでいる/応答しない: 自分が通常起動すればよい
            return false;
        }
    }

    /// <summary>受信サーバーを開始する (最初のインスタンスのみ)。
    /// 受け取ったパスは検証してから onFolder へ渡す。</summary>
    public static void StartServer(Action<string> onFolder)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var folder = (await reader.ReadToEndAsync()).Trim();
                    if (folder.Length > 0 && Directory.Exists(folder))
                        onFolder(folder);
                }
                catch
                {
                    // パイプの異常は次の接続待ちで復帰する (プロセスは巻き込まない)
                }
            }
        });
    }
}
