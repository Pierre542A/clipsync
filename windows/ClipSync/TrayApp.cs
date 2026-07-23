using System.Text;
using System.Text.Json;

namespace ClipSync;

// Application en zone de notification : icône + menu, connexion au relais,
// surveillance du presse-papiers (envoi texte + image) et réception (écriture + notification).
public sealed class TrayApp : ApplicationContext
{
    private sealed record DeviceInfo(string Name, string Platform, bool Online);

    private readonly Config _cfg;
    private readonly NotifyIcon _tray;
    private readonly RelayClient _client;
    private readonly ClipboardWatcher _watcher;
    private readonly Control _marshal = new();

    private bool _connected;
    private List<DeviceInfo> _devices = new();

    // Fenêtre pendant laquelle un changement de presse-papiers est ignoré
    // (car provoqué par NOTRE écriture d'un clip reçu) → évite la boucle d'écho.
    private DateTime _suppressUntil = DateTime.MinValue;

    public TrayApp()
    {
        _ = _marshal.Handle; // force la création du handle sur le thread UI (pour BeginInvoke)
        _cfg = Config.Load();

        var startupItem = new ToolStripMenuItem("Lancer au démarrage")
        {
            Checked = SafeIsStartupEnabled(),
            CheckOnClick = true,
        };
        startupItem.Click += (_, _) => { try { StartupManager.SetEnabled(startupItem.Checked); } catch { } };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Ouvrir ClipSync", null, (_, _) => ShowStatus()));
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quitter", null, (_, _) => Quit()));

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "ClipSync — connexion…",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowStatus();

        _watcher = new ClipboardWatcher();
        _watcher.ClipboardChanged += OnLocalClipboardChanged;

        _client = new RelayClient(_cfg);
        _client.ConnectionChanged += c => Post(() => { _connected = c; UpdateTray(); });
        _client.DevicesUpdated += d => Post(() => { ParseDevices(d); UpdateTray(); });
        _client.ClipReceived += clip => Post(() => OnRemoteClip(clip));
        _client.Log += msg => Console.WriteLine("[ClipSync] " + msg);
        _client.Start();

        UpdateTray();
    }

    private static bool SafeIsStartupEnabled()
    {
        try { return StartupManager.IsEnabled(); } catch { return false; }
    }

    // Marshalle une action vers le thread UI.
    private void Post(Action action)
    {
        try { if (_marshal.IsHandleCreated) _marshal.BeginInvoke(action); }
        catch { /* fermeture en cours */ }
    }

    private void ParseDevices(JsonElement array)
    {
        var list = new List<DeviceInfo>();
        foreach (var e in array.EnumerateArray())
        {
            var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
            var platform = e.TryGetProperty("platform", out var p) ? p.GetString() ?? "" : "";
            var online = e.TryGetProperty("online", out var o) && o.GetBoolean();
            list.Add(new DeviceInfo(name, platform, online));
        }
        _devices = list;
    }

    private void UpdateTray()
    {
        var online = _devices.Count(d => d.Online);
        _tray.Text = _connected
            ? $"ClipSync — connecté · {online} appareil(s) en ligne"
            : "ClipSync — hors ligne";
    }

    // ---- Envoi : presse-papiers Windows modifié -----------------------------
    private void OnLocalClipboardChanged()
    {
        if (DateTime.UtcNow < _suppressUntil) return; // écho de notre propre écriture
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text)) _ = _client.SendClipText(text);
                return;
            }

            if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                if (img is null) return;
                using var ms = new MemoryStream();
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                var png = ms.ToArray();
                _ = _client.SendImage(png, img.Width, img.Height); // chiffré dans RelayClient
            }
        }
        catch { /* presse-papiers verrouillé par une autre app : on ignore */ }
    }

    // ---- Réception : clip reçu du relais ------------------------------------
    private void OnRemoteClip(JsonElement clip)
    {
        try
        {
            var type = clip.GetProperty("contentType").GetString();
            var from = clip.TryGetProperty("from", out var f) && f.TryGetProperty("name", out var n)
                ? n.GetString() ?? "un appareil" : "un appareil";

            if (type == "text" && clip.TryGetProperty("text", out var t))
            {
                var text = _client.DecryptText(t.GetString() ?? "");
                if (string.IsNullOrEmpty(text)) return;
                _suppressUntil = DateTime.UtcNow.AddMilliseconds(800);
                Clipboard.SetText(text);
                _tray.ShowBalloonTip(4000, "ClipSync",
                    $"Texte reçu de {from} — prêt à coller (Ctrl+V)", ToolTipIcon.Info);
            }
            else if (type == "image" && clip.TryGetProperty("fileId", out var fid))
            {
                var fileId = fid.GetString();
                if (string.IsNullOrEmpty(fileId)) return;
                _ = Task.Run(async () =>
                {
                    var png = await _client.DownloadImage(fileId); // téléchargé + déchiffré
                    if (png is null) return;
                    Post(() => ApplyImage(png, from));
                });
            }
        }
        catch { }
    }

    private void ApplyImage(byte[] bytes, string from)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            _suppressUntil = DateTime.UtcNow.AddMilliseconds(800);
            Clipboard.SetImage(img);
            _tray.ShowBalloonTip(4000, "ClipSync",
                $"Image reçue de {from} — prête à coller (Ctrl+V)", ToolTipIcon.Info);
        }
        catch { }
    }

    private void ShowStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Serveur : {_cfg.ServerUrl}");
        sb.AppendLine($"Cet appareil : {_cfg.DeviceName}");
        sb.AppendLine($"État : {(_connected ? "connecté" : "hors ligne")}");
        sb.AppendLine();
        sb.AppendLine("Appareils associés :");
        if (_devices.Count == 0)
            sb.AppendLine("   (aucun autre appareil)");
        else
            foreach (var d in _devices)
                sb.AppendLine($"   {(d.Online ? "●" : "○")} {d.Name} ({d.Platform}) — {(d.Online ? "en ligne" : "hors ligne")}");

        MessageBox.Show(sb.ToString(), "ClipSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Quit()
    {
        _tray.Visible = false;
        _watcher.Dispose();
        _client.Dispose();
        ExitThread();
    }
}
