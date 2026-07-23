using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ClipSync;

// Client WebSocket vers le serveur relais + HTTP images.
// Chiffrement de bout en bout : le contenu est chiffré/déchiffré ici, le serveur
// ne reçoit que du chiffré et le jeton d'auth dérivé (jamais la clé de chiffrement).
public sealed class RelayClient : IDisposable
{
    private readonly Config _cfg;
    private readonly string _authToken;
    private readonly byte[] _encKey;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly HttpClient _http = new();

    public event Action<string>? Log;
    public event Action<bool>? ConnectionChanged;      // true = connecté
    public event Action<JsonElement>? DevicesUpdated;  // tableau d'appareils
    public event Action<JsonElement>? ClipReceived;    // message clip complet (chiffré)

    public RelayClient(Config cfg)
    {
        _cfg = cfg;
        _authToken = Crypto.AuthToken(cfg.Secret);
        _encKey = Crypto.EncKey(cfg.Secret);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = RunLoop(_cts.Token);
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnce(ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log?.Invoke("WS erreur : " + ex.Message); }

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
            token = _authToken,
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
            try { await SendJson(new { type = "heartbeat" }, ct); } catch { return; }
            try { await Task.Delay(30_000, ct); } catch { return; }
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
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
        catch (Exception ex) { Log?.Invoke("parse : " + ex.Message); }
    }

    // ---- Envoi (chiffré) ----------------------------------------------------

    public Task SendClipText(string text, CancellationToken ct = default) =>
        SendJson(new
        {
            type = "clip", messageId = Guid.NewGuid().ToString(),
            contentType = "text", text = Crypto.EncryptText(_encKey, text), enc = "v1", targets = "all",
        }, ct);

    public async Task SendImage(byte[] png, int width, int height, CancellationToken ct = default)
    {
        var blob = Crypto.Encrypt(_encKey, png); // on chiffre AVANT l'upload
        var fileId = await UploadAsync(blob);
        if (fileId is null) { Log?.Invoke("upload image échoué"); return; }
        await SendJson(new
        {
            type = "clip", messageId = Guid.NewGuid().ToString(),
            contentType = "image", fileId, fileType = "image/png", enc = "v1",
            meta = new { width, height }, targets = "all",
        }, ct);
    }

    // ---- Réception (déchiffrement) ------------------------------------------

    public string? DecryptText(string base64) => Crypto.DecryptText(_encKey, base64);

    public async Task<byte[]?> DownloadImage(string fileId)
    {
        var blob = await DownloadAsync(fileId);
        return blob is null ? null : Crypto.Decrypt(_encKey, blob);
    }

    // ---- HTTP brut (contenu déjà chiffré) -----------------------------------

    private async Task<string?> UploadAsync(byte[] data)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.HttpUrl.TrimEnd('/') + "/files");
            req.Headers.Add("x-account-id", _cfg.AccountId);
            req.Headers.Add("x-token", _authToken);
            req.Headers.Add("x-file-type", "application/octet-stream");
            req.Content = new ByteArrayContent(data);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("fileId").GetString();
        }
        catch (Exception ex) { Log?.Invoke("upload : " + ex.Message); return null; }
    }

    private async Task<byte[]?> DownloadAsync(string fileId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _cfg.HttpUrl.TrimEnd('/') + "/files/" + fileId);
            req.Headers.Add("x-account-id", _cfg.AccountId);
            req.Headers.Add("x-token", _authToken);
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
