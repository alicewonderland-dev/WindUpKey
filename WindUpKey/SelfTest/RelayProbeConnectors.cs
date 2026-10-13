#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindUpKey.Services;

namespace WindUpKey.SelfTest;

/// <summary>
/// Probe sockets as real <see cref="ClientWebSocket"/>s, set up like the client's own (<see cref="RelaySocketSetup"/>)
/// but with the HTTP response details kept, so a refused upgrade's 401 can be read (04 Architecture). No Dalamud
/// dependency. The token is held only to send it; never log it. A socket that isn't handed back is disposed on every
/// exit path, including a cancel and an unexpected exception (04-D12).
/// </summary>
public sealed class ClientWebSocketProbeConnector(string? token) : IProbeConnector
{
    public async Task<ProbeConnection> ConnectAsync(Uri uri, bool withToken, TimeSpan timeout, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        var handedBack = false;
        try
        {
            RelaySocketSetup.Configure(socket.Options, withToken ? token : null, collectHttpResponseDetails: true);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(timeout);
            await socket.ConnectAsync(uri, attempt.Token).ConfigureAwait(false);
            var connection = new ProbeConnection(socket, (int)socket.HttpStatusCode, null);
            handedBack = true;
            return connection;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeConnection(null, null, "timeout");
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or System.IO.IOException)
        {
            var status = (int)socket.HttpStatusCode;
            return new ProbeConnection(null, status == 0 ? null : status, ex.GetType().Name);
        }
        finally
        {
            if (!handedBack)
                socket.Dispose();
        }
    }
}

/// <summary>Reads <c>/stats</c> with <see cref="HttpClient"/>: the status, the <c>Date</c> header and <c>accounts</c>.</summary>
public sealed class HttpStatsClient(string? token, HttpMessageHandler? handler = null) : IStatsClient, IDisposable
{
    private readonly HttpClient _http = handler is null ? new HttpClient() : new HttpClient(handler);

    public async Task<StatsOutcome> GetAsync(Uri uri, bool withToken, TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (withToken && !string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(timeout);
        try
        {
            using var response = await _http.SendAsync(request, attempt.Token).ConfigureAwait(false);
            long? accounts = null;
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false));
                    if (doc.RootElement.TryGetProperty("accounts", out var value) && value.TryGetInt64(out var count))
                        accounts = count;
                }
                catch (JsonException)
                {
                    // An older relay or another server: no count.
                }
            }

            return new StatsOutcome((int)response.StatusCode, response.Headers.Date, accounts, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new StatsOutcome(null, null, null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new StatsOutcome(null, null, null, ex.GetType().Name);
        }
    }

    public void Dispose() => _http.Dispose();
}
#endif
