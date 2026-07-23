using System.Text.Json;

namespace ClipSync;

// Application en zone de notification : icône + menu, connexion au relais,
// surveillance du presse-papiers (envoi) et réception (écriture + notification).
public sealed class TrayApp : ApplicationContext
{
    private readonly Config _cfg;
    private readonly NotifyIcon _tray;
    private readonly RelayClient _client;
    private readonly ClipboardWatcher _watcher;
    private readonly Control _marshal = new();

    private bool _connected;
    private int _deviceCount;
    private string? _suppressNext; // évite de renvoyer un texte qu'on vient d'écrire

    public TrayApp()
    {
        _ = _marshal.Handle; // force la création du handle sur le thread UI (pour BeginInvoke)
        _cfg = Config.Load();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Ouvrir ClipSync", null, (_, _) => ShowStatus()));
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
        _client.DevicesUpdated += d => Post(() => { _deviceCount = d.GetArrayLength(); UpdateTray(); });
        _client.ClipReceived += clip => Post(() => OnRemoteClip(clip));
        _client.Log += msg => Console.WriteLine("[ClipSync] " + msg);
        _client.Start();

        UpdateTray();
    }

    // Marshalle une action vers le thread UI.
    private void Post(Action action)
    {
        try { if (_marshal.IsHandleCreated) _marshal.BeginInvoke(action); }
        catch { /* fermeture en cours */ }
    }

    private void UpdateTray()
    {
        _tray.Text = _connected
            ? $"ClipSync — connecté · {_deviceCount} appareil(s)"
            : "ClipSync — hors ligne";
    }

    // Presse-papiers Windows modifié → on envoie (texte pour l'instant).
    private void OnLocalClipboardChanged()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text)) return;
            if (text == _suppressNext) { _suppressNext = null; return; } // écho d'un clip reçu
            _ = _client.SendClipText(text);
        }
        catch { /* presse-papiers verrouillé par une autre app : on ignore */ }
    }

    // Clip reçu du relais → on écrit dans le presse-papiers + notification.
    private void OnRemoteClip(JsonElement clip)
    {
        try
        {
            var type = clip.GetProperty("contentType").GetString();
            var from = clip.TryGetProperty("from", out var f) && f.TryGetProperty("name", out var n)
                ? n.GetString() : "un appareil";

            if (type == "text" && clip.TryGetProperty("text", out var t))
            {
                var text = t.GetString() ?? "";
                if (string.IsNullOrEmpty(text)) return;
                _suppressNext = text;
                Clipboard.SetText(text);
                _tray.ShowBalloonTip(4000, "ClipSync",
                    $"Texte reçu de {from} — prêt à coller (Ctrl+V)", ToolTipIcon.Info);
            }
            // TODO images : télécharger fileId via HTTP puis Clipboard.SetImage.
        }
        catch { }
    }

    private void ShowStatus()
    {
        MessageBox.Show(
            $"Serveur : {_cfg.ServerUrl}\n" +
            $"Appareil : {_cfg.DeviceName} ({_cfg.DeviceId})\n" +
            $"État : {(_connected ? "connecté" : "hors ligne")}\n" +
            $"Autres appareils : {_deviceCount}",
            "ClipSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Quit()
    {
        _tray.Visible = false;
        _watcher.Dispose();
        _client.Dispose();
        ExitThread();
    }
}
