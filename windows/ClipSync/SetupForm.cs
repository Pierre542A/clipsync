using System.Drawing;

namespace ClipSync;

// Fenêtre de configuration (1er lancement + Réglages) : une seule « phrase de couplage ».
public sealed class SetupForm : Form
{
    private readonly TextBox _phrase = new();
    private readonly TextBox _name = new();
    private readonly CheckBox _show = new();

    public string Phrase => _phrase.Text.Trim();
    public string DeviceNameValue =>
        string.IsNullOrWhiteSpace(_name.Text) ? Environment.MachineName : _name.Text.Trim();

    public SetupForm(string phrase, string deviceName)
    {
        Text = "ClipSync — Configuration";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(444, 306);
        Font = new Font("Segoe UI", 9f);

        var info = new Label
        {
            Text = "Choisis une phrase de couplage secrète.\r\n" +
                   "Mets EXACTEMENT la même sur ton iPhone (et tes autres PC).\r\n" +
                   "Elle relie tes appareils et chiffre tout — garde-la privée.",
            Location = new Point(16, 14),
            Size = new Size(412, 62),
        };

        var lblPhrase = new Label { Text = "Phrase de couplage", Location = new Point(16, 84), AutoSize = true };
        _phrase.Location = new Point(16, 106);
        _phrase.Size = new Size(412, 25);
        _phrase.Text = phrase;
        _phrase.UseSystemPasswordChar = true;

        _show.Text = "Afficher la phrase";
        _show.Location = new Point(16, 138);
        _show.AutoSize = true;
        _show.CheckedChanged += (_, _) => _phrase.UseSystemPasswordChar = !_show.Checked;

        var lblName = new Label { Text = "Nom de cet appareil", Location = new Point(16, 170), AutoSize = true };
        _name.Location = new Point(16, 192);
        _name.Size = new Size(412, 25);
        _name.Text = deviceName;

        var ok = new Button { Text = "Enregistrer", Location = new Point(230, 254), Size = new Size(96, 34), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Annuler", Location = new Point(332, 254), Size = new Size(96, 34), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (Phrase.Length == 0)
            {
                MessageBox.Show(this, "Entre une phrase de couplage.", "ClipSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None; // empêche la fermeture
            }
        };

        Controls.AddRange(new Control[] { info, lblPhrase, _phrase, _show, lblName, _name, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
