using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DiscordVoiceTranslator.Core.Timing;
using static DiscordVoiceTranslator.Core.Discord.DiscordRpcClient;
using static DiscordVoiceTranslator.Core.Localization.UiText;

namespace DiscordVoiceTranslator.Core.Discord;

/// <summary>Une personne vue dans un salon vocal : pseudo affiché et avatar.</summary>
public sealed record VoiceMember(string Id, string DisplayName, string AvatarUrl);

/// <summary>
/// Suit, via le RPC local de ton client Discord (aucun bot), le salon vocal où tu es, ses
/// membres et qui parle. Se reconnecte tout seul ; sans RPC, l'appli marche sans pseudos.
/// </summary>
public sealed class DiscordVoiceService : IAsyncDisposable
{
    private static readonly string[] ChannelEvents =
        ["VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP"];

    private readonly DiscordAppCredentials _app;
    private readonly SpeakerTracker _tracker = new();
    // Toutes les personnes croisées pendant la session : quelqu'un qui quitte le salon juste
    // après avoir parlé doit garder son pseudo sur sa dernière phrase.
    private readonly ConcurrentDictionary<string, VoiceMember> _known = new();
    private readonly ConcurrentDictionary<string, byte> _inChannel = new();
    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _supervisor;
    private string? _channelId, _channelName;
    private string _lastStatus = "";

    public event Action<string>? StatusChanged;

    /// <summary>Décalage toléré entre les événements Discord et l'audio capté.</summary>
    public TimeSpan Margin { get; set; } = TimeSpan.FromMilliseconds(300);

    public DiscordVoiceService(DiscordAppCredentials app) => _app = app;

    public void Start() => _supervisor = Task.Run(SuperviseAsync);

    /// <summary>Pseudos des personnes présentes dans le salon vocal (toi compris).</summary>
    public IReadOnlyList<string> ChannelMemberNames =>
        _inChannel.Keys.Select(id => _known.TryGetValue(id, out var member) ? member.DisplayName : null).OfType<string>().ToList();

    /// <summary>Qui parlait entre ces deux instants (vide si inconnu ou RPC déconnecté).</summary>
    public IReadOnlyList<VoiceMember> IdentifySpeakers(long startQpc, long endQpc) =>
        _tracker.Attribute(startQpc, endQpc, Margin.Ticks, QpcClock.Now100ns)
            .Select(id => _known.TryGetValue(id, out var member) ? member : null)
            .OfType<VoiceMember>()
            .ToList();

    private async Task SuperviseAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var connection = await DiscordIpcConnection.TryConnectAsync(token);
                if (connection is null)
                {
                    Report(T("Names: Discord not found, retrying in 5 s", "Pseudos : Discord introuvable, nouvel essai dans 5 s"));
                }
                else
                {
                    await using var client = new DiscordRpcClient(connection, token);
                    await RunSessionAsync(client, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Report(T("Names: ", "Pseudos : ") + Describe(ex));
            }
            finally
            {
                _channelId = _channelName = null;
                _inChannel.Clear();
                _tracker.Clear();
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunSessionAsync(DiscordRpcClient client, CancellationToken token)
    {
        var ready = await client.HandshakeAsync(_app.ClientId, token);
        _tracker.IgnoredUserId = Text(ready?["user"]?["id"]);
        client.EventReceived += (evt, data, receivedAt) => OnEvent(client, evt, data, receivedAt);

        await AuthenticateAsync(client, token);
        await client.SendAsync("SUBSCRIBE", evt: "VOICE_CHANNEL_SELECT");
        await RefreshChannelAsync(client);
        await client.Completion.WaitAsync(token); // jusqu'à ce que Discord coupe la connexion, ou qu'on quitte
    }

    private async Task AuthenticateAsync(DiscordRpcClient client, CancellationToken token)
    {
        // Jeton déjà obtenu : aucune fenêtre côté Discord, on le rafraîchit s'il expire bientôt.
        if (DiscordToken.Load() is { } saved)
        {
            try
            {
                if (saved.ExpiresAt < DateTimeOffset.UtcNow.AddDays(1))
                {
                    saved = await DiscordOAuth.RefreshAsync(_app, saved.RefreshToken, token);
                    saved.Save();
                }
                await client.SendAsync("AUTHENTICATE", new JsonObject { ["access_token"] = saved.AccessToken });
                return;
            }
            catch (DiscordRpcException)
            {
                // Révoqué ou périmé : on l'oublie pour redemander l'autorisation proprement.
                DiscordToken.Forget();
                throw;
            }
        }

        Report(T("Names: accept the authorization request that just opened in Discord",
                 "Pseudos : accepte la demande d'autorisation qui vient de s'ouvrir dans Discord"));
        var authorization = await client.SendAsync("AUTHORIZE", new JsonObject
        {
            ["client_id"] = _app.ClientId,
            ["scopes"] = new JsonArray("rpc", "rpc.voice.read"),
        }, timeout: TimeSpan.FromMinutes(3));
        string code = Text(authorization?["code"]) ?? throw new DiscordRpcException(T("Discord returned no authorization code.", "Discord n'a pas renvoyé de code d'autorisation."));

        var fresh = await DiscordOAuth.ExchangeCodeAsync(_app, code, token);
        fresh.Save();
        await client.SendAsync("AUTHENTICATE", new JsonObject { ["access_token"] = fresh.AccessToken });
    }

    /// <summary>Relit le salon sélectionné et déplace les abonnements s'il a changé.</summary>
    private async Task RefreshChannelAsync(DiscordRpcClient client)
    {
        await _channelLock.WaitAsync();
        try
        {
            var channel = await client.SendAsync("GET_SELECTED_VOICE_CHANNEL");
            string? newId = Text(channel?["id"]);

            if (newId != _channelId)
            {
                if (_channelId is not null)
                {
                    foreach (string evt in ChannelEvents)
                    {
                        try { await client.SendAsync("UNSUBSCRIBE", new JsonObject { ["channel_id"] = _channelId }, evt); }
                        catch (DiscordRpcException) { /* salon déjà disparu : rien à désabonner */ }
                    }
                }
                _inChannel.Clear();
                _tracker.Clear();
                if (newId is not null)
                {
                    foreach (string evt in ChannelEvents)
                        await client.SendAsync("SUBSCRIBE", new JsonObject { ["channel_id"] = newId }, evt);
                }
                _channelId = newId;
            }

            _channelName = Text(channel?["name"]);
            foreach (var voiceState in channel?["voice_states"]?.AsArray() ?? new JsonArray())
                AddMember(voiceState);
            ReportChannel();
        }
        finally
        {
            _channelLock.Release();
        }
    }

    private void OnEvent(DiscordRpcClient client, string evt, JsonNode? data, long receivedAt)
    {
        switch (evt)
        {
            case "SPEAKING_START" when Text(data?["user_id"]) is string userId:
                _tracker.Started(userId, receivedAt);
                break;
            case "SPEAKING_STOP" when Text(data?["user_id"]) is string userId:
                _tracker.Stopped(userId, receivedAt);
                break;
            case "VOICE_STATE_CREATE" or "VOICE_STATE_UPDATE":
                AddMember(data);
                ReportChannel();
                break;
            case "VOICE_STATE_DELETE" when Text(data?["user"]?["id"]) is string userId:
                // Partie en pleine phrase : Discord n'enverra peut-être jamais son SPEAKING_STOP.
                _tracker.Stopped(userId, receivedAt);
                _inChannel.TryRemove(userId, out _);
                ReportChannel();
                break;
            case "VOICE_CHANNEL_SELECT":
                // Pas d'attente ici : la réponse arrive sur ce même fil de lecture.
                _ = Task.Run(async () =>
                {
                    try { await RefreshChannelAsync(client); }
                    catch (DiscordRpcException ex) { Report(T("Names: ", "Pseudos : ") + ex.Message); }
                });
                break;
        }
    }

    private void AddMember(JsonNode? voiceState)
    {
        var user = voiceState?["user"];
        if (Text(user?["id"]) is not string id)
            return;

        // Surnom sur le serveur, sinon nom d'affichage, sinon nom d'utilisateur.
        string name = new[] { Text(voiceState!["nick"]), Text(user!["global_name"]), Text(user["username"]) }
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? id;
        string avatarUrl = Text(user["avatar"]) is string hash
            ? $"https://cdn.discordapp.com/avatars/{id}/{hash}.png?size=64"
            // Sans avatar, Discord affiche l'un de ses 6 avatars par défaut, choisi d'après l'ID.
            : $"https://cdn.discordapp.com/embed/avatars/{(ulong.Parse(id) >> 22) % 6}.png";

        _known[id] = new VoiceMember(id, name, avatarUrl);
        _inChannel[id] = 0;
    }

    private void ReportChannel() =>
        Report(_channelId is null
            ? T("Names: connected, but you are not in a voice channel", "Pseudos : connecté, mais tu n'es dans aucun salon vocal")
            : T($"Names: channel \"{_channelName}\", {_inChannel.Count} member(s)", $"Pseudos : salon « {_channelName} », {_inChannel.Count} personne(s)"));

    private void Report(string status)
    {
        if (status == _lastStatus) return;
        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }

    private static string Describe(Exception ex) => ex switch
    {
        DiscordRpcException { Code: int code } rpc => $"{rpc.Message} (code {code})",
        DiscordRpcException rpc => rpc.Message,
        HttpRequestException => T("discord.com unreachable for authorization", "discord.com injoignable pour l'autorisation"),
        _ => ex.Message,
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_supervisor is not null)
        {
            try { await _supervisor; } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
