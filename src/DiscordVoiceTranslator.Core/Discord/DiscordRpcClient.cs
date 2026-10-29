using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DiscordVoiceTranslator.Core.Timing;
using static DiscordVoiceTranslator.Core.Discord.DiscordIpcConnection;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.Core.Discord;

/// <summary>Erreur renvoyée par Discord (ou par son serveur OAuth2), avec son message d'origine.</summary>
public sealed class DiscordRpcException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}

/// <summary>
/// Commandes et événements du RPC sur une connexion IPC. Une commande porte un « nonce » que
/// Discord recopie dans sa réponse ; les événements arrivent avec cmd = DISPATCH.
/// </summary>
internal sealed class DiscordRpcClient : IAsyncDisposable
{
    private readonly DiscordIpcConnection _connection;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;

    /// <summary>
    /// Événement Discord : nom, données, heure de réception sur <see cref="QpcClock"/>.
    /// Appelé sur le fil de lecture : ne jamais y attendre la réponse d'une commande.
    /// </summary>
    public event Action<string, JsonNode?, long>? EventReceived;

    /// <summary>Se termine (en erreur) quand la connexion à Discord est perdue.</summary>
    public Task Completion => _readLoop ?? Task.CompletedTask;

    public DiscordRpcClient(DiscordIpcConnection connection) => _connection = connection;

    /// <summary>Présente l'application à Discord. Renvoie les données de READY (dont l'utilisateur connecté).</summary>
    public async Task<JsonNode?> HandshakeAsync(string clientId, CancellationToken cancellationToken)
    {
        await _connection.WriteAsync(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, cancellationToken);
        var (opcode, payload) = await _connection.ReadAsync(cancellationToken);
        if (opcode == OpClose)
            throw new DiscordRpcException(Text(payload?["message"]) ?? T("Discord refused the connection.", "Discord a refusé la connexion."), Number(payload?["code"]));
        if (Text(payload?["evt"]) != "READY")
            throw new DiscordRpcException(T("Unexpected answer from Discord.", "Réponse inattendue de Discord à la connexion."));

        _readLoop = Task.Run(ReadLoop);
        return payload!["data"];
    }

    public async Task<JsonNode?> SendAsync(string command, JsonObject? args = null, string? evt = null, TimeSpan? timeout = null)
    {
        string nonce = Guid.NewGuid().ToString();
        var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = reply;
        var message = new JsonObject { ["cmd"] = command, ["args"] = args ?? new JsonObject(), ["nonce"] = nonce };
        if (evt is not null)
            message["evt"] = evt;
        try
        {
            await _connection.WriteAsync(OpFrame, message, _cts.Token);
            return await reply.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), _cts.Token);
        }
        catch (TimeoutException)
        {
            string what = command + (evt is null ? "" : " " + evt);
            throw new DiscordRpcException(T($"Discord did not answer {what}.", $"Discord n'a pas répondu à {what}."));
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (true)
            {
                var (opcode, payload) = await _connection.ReadAsync(_cts.Token);
                // Horodaté dès la lecture : c'est ce qui sert à aligner « qui parle » avec l'audio.
                long receivedAt = QpcClock.Now100ns;
                switch (opcode)
                {
                    case OpPing:
                        await _connection.WriteAsync(OpPong, payload ?? new JsonObject(), _cts.Token);
                        break;
                    case OpClose:
                        throw new DiscordRpcException(Text(payload?["message"]) ?? T("Discord closed the connection.", "Discord a fermé la connexion."), Number(payload?["code"]));
                    case OpFrame:
                        Dispatch(payload, receivedAt);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            var reason = ex as DiscordRpcException ?? new DiscordRpcException(T("Connection to Discord lost.", "Connexion à Discord perdue."));
            foreach (var pending in _pending.Values)
                pending.TrySetException(reason);
            throw reason;
        }
    }

    private void Dispatch(JsonNode? payload, long receivedAt)
    {
        string? evt = Text(payload?["evt"]);
        if (Text(payload?["nonce"]) is string nonce && _pending.TryRemove(nonce, out var reply))
        {
            if (evt == "ERROR")
                reply.TrySetException(new DiscordRpcException(Text(payload!["data"]?["message"]) ?? T("Discord error.", "Erreur Discord."), Number(payload["data"]?["code"])));
            else
                reply.TrySetResult(payload!["data"]);
        }
        else if (Text(payload?["cmd"]) == "DISPATCH" && evt is not null)
        {
            EventReceived?.Invoke(evt, payload!["data"], receivedAt);
        }
    }

    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    private static int? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out int n) ? n : null;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Completion; } catch { /* la fin de la connexion est l'effet recherché */ }
        _cts.Dispose();
    }
}
