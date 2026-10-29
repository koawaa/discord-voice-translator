using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace DiscordVoiceTranslator.Core.Discord;

/// <summary>
/// Transport du RPC local de Discord : un named pipe ouvert par ton propre client Discord.
/// Chaque trame = opcode (int32) + longueur (int32), petit-boutiste, puis le JSON.
/// </summary>
internal sealed class DiscordIpcConnection : IAsyncDisposable
{
    public const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _header = new byte[8];

    private DiscordIpcConnection(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>
    /// Discord écoute sur le premier nom libre entre discord-ipc-0 et discord-ipc-9 (il peut y
    /// avoir Discord, PTB et Canary en même temps). Null si aucun ne répond.
    /// </summary>
    public static async Task<DiscordIpcConnection?> TryConnectAsync(CancellationToken cancellationToken)
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(200, cancellationToken);
                return new DiscordIpcConnection(pipe);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync();
            }
        }
        return null;
    }

    public async Task WriteAsync(int opcode, JsonNode payload, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        byte[] frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);

        // Lecture et écriture se font sur des fils différents : une trame ne doit jamais être entrelacée.
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _pipe.WriteAsync(frame, cancellationToken);
            await _pipe.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<(int Opcode, JsonNode? Payload)> ReadAsync(CancellationToken cancellationToken)
    {
        await _pipe.ReadExactlyAsync(_header, cancellationToken);
        int opcode = BinaryPrimitives.ReadInt32LittleEndian(_header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(4));
        byte[] body = new byte[length];
        await _pipe.ReadExactlyAsync(body, cancellationToken);
        return (opcode, JsonNode.Parse(body));
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync();
        _writeLock.Dispose();
    }
}
