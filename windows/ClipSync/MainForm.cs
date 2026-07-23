using System.Drawing;

namespace ClipSync;

// Vraie fenêtre : état de connexion + liste d'appareils (live) + bouton Réglages.
// La fermeture cache la fenêtre (l'app continue dans la zone de notification).
public sealed class MainForm : Form
{
    private readonly Label _status = new();
    private readonly ListView _list = new();

    public event Action? SettingsRequested;

    public MainForm(string deviceName)
    {
        Text = "ClipSync";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(464, 384);
        MinimumSize = new Size(420, 340);
        Font = new Font("Segoe UI", 9f);

        _status.Location = new Point(16, 14);
        _status.Size = new Size(432, 24);
        _status.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);

        var lblThis = new Label
        {
            Text = "Cet appareil : " + deviceName,
            Location = new Point(16, 42),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        var lblDevices = new Label { Text = "Appareils associés", Location = new Point(16, 74), AutoSize = true };

        _list.Location = new Point(16, 96);
        _list.Size = new Size(432, 232);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _list.Columns.Add("Appareil", 200);
        _list.Columns.Add("Plateforme", 120);
        _list.Columns.Add("État", 100);

        var settings = new Button
        {
            Text = "Réglages", Location = new Point(16, 340), Size = new Size(120, 34),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        settings.Click += (_, _) => SettingsRequested?.Invoke();

        var close = new Button
        {
            Text = "Fermer", Location = new Point(328, 340), Size = new Size(120, 34),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        close.Click += (_, _) => Hide();

        Controls.AddRange(new Control[] { _status, lblThis, lblDevices, _list, settings, close });

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
    }

    public void SetConnected(bool connected)
    {
        _status.Text = connected ? "● Connecté" : "○ Hors ligne";
        _status.ForeColor = connected ? Color.FromArgb(34, 197, 94) : Color.Gray;
    }

    public void SetDevices(IEnumerable<(string Name, string Platform, bool Online)> devices)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        var any = false;
        foreach (var d in devices)
        {
            any = true;
            var item = new ListViewItem(d.Name);
            item.SubItems.Add(d.Platform);
            item.SubItems.Add(d.Online ? "en ligne" : "hors ligne");
            item.ForeColor = d.Online ? SystemColors.ControlText : Color.Gray;
            _list.Items.Add(item);
        }
        if (!any)
            _list.Items.Add(new ListViewItem("(aucun autre appareil — configure la même phrase ailleurs)") { ForeColor = Color.Gray });
        _list.EndUpdate();
    }
}
