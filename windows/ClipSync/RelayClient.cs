using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ClipSync;

// Client WebSocket vers le serveur relais : connexion permanente + reconnexion auto,
// hello/heartbeat, envoi de clips, réception (devices / clip / error).
public sealed class RelayClient : IDisposable
{
    private readonly Config _cfg;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly HttpClient _http = new();

    public event Action<string>? Log;
    public event Action<bool>? ConnectionChanged;      // true = connecté
    public event Action<JsonElement>? DevicesUpdated;  // tableau d'appareils
    public event Action<JsonElement>? ClipReceived;    // message clip complet

    public RelayClient(Config cfg) => _cfg = cfg;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = RunLoop(_cts.Token);
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnce(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log?.Invoke("WS erreur : " + ex.Message);
            }

            ConnectionChanged?.Invoke(false);
            try { await Task.Delay(3000, ct); } catch { }
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        _ws = ws;
        await ws.ConnectAsync(new Uri(_cfg.ServerUrl), ct);
        ConnectionChanged?.Invoke(true);
        Log?.Invoke("Connecté au relais");

        await SendJson(new
        {
            type = "hello",
            accountId = _cfg.AccountId,
            token = _cfg.Secret,
            deviceId = _cfg.DeviceId,
            deviceName = _cfg.DeviceName,
            platform = "windows",
        }, ct);

        _ = HeartbeatLoop(ws, ct);

        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            HandleMessage(sb.ToString());
        }
    }

    private async Task HeartbeatLoop(ClientWebSocket ws, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try { await SendJson(new { type = "heartbeat" }, ct); }
            catch { return; }
            try { await Task.Delay(30_000, ct); }
            catch { return; }
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "welcome":
                case "devices":
                    if (root.TryGetProperty("devices", out var devices))
                        DevicesUpdated?.Invoke(devices.Clone());
                    break;
                case "clip":
                    ClipReceived?.Invoke(root.Clone());
                    break;
                case "error":
                    Log?.Invoke("Serveur : " + (root.TryGetProperty("message", out var m) ? m.GetString() : "?"));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("parse : " + ex.Message);
        }
    }

    public Task SendClipText(string text, CancellationToken ct = default) =>
        SendJson(new
        {
            type = "clip",
            messageId = Guid.NewGuid().ToString(),
            contentType = "text",
            text,
            targets = "all",
        }, ct);

    public Task SendClipImage(string fileId, string fileType, int width, int height, CancellationToken ct = default) =>
        SendJson(new
        {
            type = "clip",
            messageId = Guid.NewGuid().ToString(),
            contentType = "image",
            fileId,
            fileType,
            meta = new { width, height },
            targets = "all",
        }, ct);

    // Upload d'une image vers le stockage temporaire du serveur → renvoie le fileId.
    public async Task<string?> UploadImageAsync(byte[] data, string contentType)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.HttpUrl.TrimEnd('/') + "/files");
            req.Headers.Add("x-account-id", _cfg.AccountId);
            req.Headers.Add("x-token", _cfg.Secret);
            req.Headers.Add("x-file-type", contentType);
            req.Content = new ByteArrayContent(data);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("fileId").GetString();
        }
        catch (Exception ex) { Log?.Invoke("upload : " + ex.Message); return null; }
    }

    // Téléchargement d'une image depuis le serveur.
    public async Task<byte[]?> DownloadFileAsync(string fileId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _cfg.HttpUrl.TrimEnd('/') + "/files/" + fileId);
            req.Headers.Add("x-account-id", _cfg.AccountId);
            req.Headers.Add("x-token", _cfg.Secret);
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex) { Log?.Invoke("download : " + ex.Message); return null; }
    }

    private async Task SendJson(object payload, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _ws?.Dispose(); } catch { }
        try { _http.Dispose(); } catch { }
    }
}
