using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using WindUpKey.Protocol;

var builder = WebApplication.CreateBuilder(args);
var relayToken = builder.Configuration["Relay:Token"] ?? string.Empty;
var urls = builder.Configuration["Relay:Urls"] ?? "http://127.0.0.1:8787";
builder.WebHost.UseUrls(urls);

var app = builder.Build();
var log = app.Logger;

if (string.IsNullOrWhiteSpace(relayToken))
{
    if (app.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "Relay:Token is required in Production. Set it in appsettings.Production.json or env Relay__Token.");
    }

    log.LogWarning("Relay:Token is empty — OK for local dev only. Set a token before tunneling.");
}
else if (relayToken is "CHANGE_ME_TO_A_LONG_RANDOM_SECRET")
{
    throw new InvalidOperationException(
        "Replace Relay:Token in appsettings.Production.json with a long random secret before hosting.");
}

var sessions = new SessionHub(log);

app.UseWebSockets();
app.MapGet("/", () => Results.Text("WindUpKey relay. Connect plugins to /ws"));
app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!string.IsNullOrEmpty(relayToken))
    {
        var provided = context.Request.Query["token"].FirstOrDefault()
                       ?? context.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(provided, relayToken, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await sessions.HandleClientAsync(socket, context.RequestAborted);
});

log.LogInformation("WindUpKey relay listening on {Urls}", urls);
app.Run();

internal sealed class SessionHub(ILogger log)
{
    private readonly ConcurrentDictionary<string, ClientSession> _byIdentity =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task HandleClientAsync(WebSocket socket, CancellationToken ct)
    {
        var session = new ClientSession(socket);
        try
        {
            var buffer = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                while (!result.EndOfMessage)
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    json += Encoding.UTF8.GetString(buffer, 0, result.Count);
                }

                await ProcessMessageAsync(session, json, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shut down
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "WebSocket session ended with error");
        }
        finally
        {
            if (session.Identity is { } id)
            {
                if (_byIdentity.TryGetValue(id, out var current) && ReferenceEquals(current, session))
                    _byIdentity.TryRemove(id, out _);
                log.LogInformation("Unregistered {Identity}", id);
            }

            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private async Task ProcessMessageAsync(ClientSession session, string json, CancellationToken ct)
    {
        var envelope = Envelope.TryParse(json);
        if (envelope is null)
            return;

        switch (envelope.Type)
        {
            case MessageTypes.Register:
                await HandleRegisterAsync(session, envelope, ct);
                break;
            case MessageTypes.Wind:
                await HandleWindAsync(session, envelope, ct);
                break;
            case MessageTypes.WindResult:
            case MessageTypes.Error:
                await RouteBackAsync(session, envelope, ct);
                break;
            case MessageTypes.Ping:
                await session.SendAsync(Envelope.Create(MessageTypes.Pong, new { }), ct);
                break;
            case MessageTypes.Pong:
                break;
            default:
                // Forward-compatible: ignore unknown types.
                break;
        }
    }

    private async Task HandleRegisterAsync(ClientSession session, Envelope envelope, CancellationToken ct)
    {
        var payload = envelope.GetPayload<RegisterPayload>();
        if (payload is null || string.IsNullOrWhiteSpace(payload.Identity))
        {
            await session.SendAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
            {
                Code = ErrorCodes.BadRequest,
                Message = "register requires identity",
            }), ct);
            return;
        }

        var identity = PlayerIdentity.Normalize(payload.Identity);
        if (session.Identity is { } old && !string.Equals(old, identity, StringComparison.OrdinalIgnoreCase))
        {
            _byIdentity.TryRemove(old, out _);
        }

        session.Identity = identity;
        _byIdentity[identity] = session;
        log.LogInformation("Registered {Identity}", identity);
    }

    private async Task HandleWindAsync(ClientSession session, Envelope envelope, CancellationToken ct)
    {
        if (session.Identity is null)
        {
            await session.SendAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
            {
                Code = ErrorCodes.NotRegistered,
                Message = "register before sending wind",
            }), ct);
            return;
        }

        var payload = envelope.GetPayload<WindPayload>();
        if (payload is null || string.IsNullOrWhiteSpace(payload.To) || payload.Hours <= 0)
        {
            await session.SendAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
            {
                RequestId = payload?.RequestId,
                Code = ErrorCodes.BadRequest,
                Message = "invalid wind payload",
            }), ct);
            return;
        }

        payload.From = session.Identity;
        payload.To = PlayerIdentity.Normalize(payload.To);

        if (!_byIdentity.TryGetValue(payload.To, out var target) || !target.IsOpen)
        {
            await session.SendAsync(Envelope.Create(MessageTypes.Error, new ErrorPayload
            {
                RequestId = payload.RequestId,
                Code = ErrorCodes.TargetOffline,
                Message = $"{payload.To} is not connected to the relay",
            }), ct);
            return;
        }

        await target.SendAsync(Envelope.Create(MessageTypes.Wind, payload), ct);
    }

    private async Task RouteBackAsync(ClientSession session, Envelope envelope, CancellationToken ct)
    {
        string? to = envelope.Type switch
        {
            MessageTypes.WindResult => envelope.GetPayload<WindResultPayload>()?.To,
            MessageTypes.Error => envelope.GetPayload<ErrorPayload>()?.To,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(to))
            return;

        if (!_byIdentity.TryGetValue(to, out var dest) || !dest.IsOpen)
            return;

        await dest.SendAsync(envelope, ct);
    }
}

internal sealed class ClientSession(WebSocket socket)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string? Identity { get; set; }
    public bool IsOpen => socket.State == WebSocketState.Open;

    public async Task SendAsync(Envelope envelope, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(envelope.Serialize());
        await _sendLock.WaitAsync(ct);
        try
        {
            if (socket.State != WebSocketState.Open)
                return;
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
