using System.Text.Json;

namespace ClipSync;

// Application en zone de notification : icône + menu + vraie fenêtre.
// 1er lancement -> demande la phrase de couplage. Surveillance presse-papiers (envoi)
// et réception (écriture + notification). Chiffrement de bout en bout via RelayClient.
public sealed class TrayApp : ApplicationContext
{
    private sealed record DeviceInfo(string Name, string Platform, bool Online);

    private readonly Config _cfg;
    private readonly NotifyIcon _tray;
    private readonly ClipboardWatcher _watcher;
    private readonly Control _marshal = new();

    private RelayClient? _client;
    private MainForm? _mainForm;
    private bool _settingsOpen;

    private bool _connected;
    private List<DeviceInfo> _devices = new();
    private DateTime _suppressUntil = DateTime.MinValue;

    public TrayApp()
    {
        _ = _marshal.Handle; // handle UI pour BeginInvoke
        _cfg = Config.Load();

        var startupItem = new ToolStripMenuItem("Lancer au démarrage")
        {
            Checked = SafeIsStartupEnabled(),
            CheckOnClick = true,
        };
        startupItem.Click += (_, _) => { try { StartupManager.SetEnabled(startupItem.Checked); } catch { } };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Ouvrir ClipSync", null, (_, _) => ShowMain()));
        menu.Items.Add(new ToolStripMenuItem("Réglages…", null, (_, _) => ShowSettings()));
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quitter", null, (_, _) => Quit()));

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "ClipSync",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMain();

        _watcher = new ClipboardWatcher();
        _watcher.ClipboardChanged += OnLocalClipboardChanged;

        if (_cfg.IsConfigured)
            StartClient();
        else
            Post(ShowSettings); // 1er lancement : demander la phrase une fois la boucle démarrée

        UpdateTray();
    }

    private static bool SafeIsStartupEnabled()
    {
        try { return StartupManager.IsEnabled(); } catch { return false; }
    }

    // (Re)crée le client réseau avec la config courante.
    private void StartClient()
    {
        _client?.Dispose();
        _connected = false;
        _devices = new();
        _client = new RelayClient(_cfg);
        _client.ConnectionChanged += c => Post(() => { _connected = c; UpdateTray(); _mainForm?.SetConnected(c); });
        _client.DevicesUpdated += d => Post(() => { ParseDevices(d); UpdateTray(); PushDevices(); });
        _client.ClipReceived += clip => Post(() => OnRemoteClip(clip));
        _client.Log += msg => Console.WriteLine("[ClipSync] " + msg);
        _client.Start();
    }

    private void ShowSettings()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            using var form = new SetupForm(_cfg.Phrase, _cfg.DeviceName);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _cfg.Phrase = form.Phrase;
                _cfg.DeviceName = form.DeviceNameValue;
                _cfg.Save();
                StartClient(); // reconnexion avec la nouvelle phrase
                UpdateTray();
                _mainForm?.SetConnected(false);
            }
        }
        finally { _settingsOpen = false; }
    }

    private void ShowMain()
    {
        if (_mainForm is null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm(_cfg.DeviceName);
            _mainForm.SettingsRequested += ShowSettings;
        }
        _mainForm.SetConnected(_connected);
        PushDevices();
        _mainForm.Show();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
    }

    private void PushDevices()
    {
        _mainForm?.SetDevices(_devices.Select(x => (x.Name, x.Platform, x.Online)));
    }

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
        if (!_cfg.IsConfigured) { _tray.Text = "ClipSync — à configurer"; return; }
        var online = _devices.Count(d => d.Online);
        _tray.Text = _connected
            ? $"ClipSync — connecté · {online} en ligne"
            : "ClipSync — hors ligne";
    }

    // ---- Envoi : presse-papiers Windows modifié -----------------------------
    private void OnLocalClipboardChanged()
    {
        if (_client is null) return;
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
        if (_client is null) return;
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

    private void Quit()
    {
        _tray.Visible = false;
        _watcher.Dispose();
        _client?.Dispose();
        _mainForm?.Dispose();
        ExitThread();
    }
}
