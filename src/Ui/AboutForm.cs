using System.Reflection;

namespace SysWidge.Ui;

/// <summary>
/// Small About dialog: mascot, version, and the build timestamp (when this exe was
/// published — handy for telling which build is running).
/// </summary>
internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        string version = $"{v.Major}.{v.Minor}.{v.Build}";
        DateTime built = BuildDate();

        Text = "About SysWidge";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(372, 176);
        BackColor = Color.FromArgb(24, 24, 28);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9f);

        if (LoadAppIcon(72) is { } big)
        {
            Controls.Add(new PictureBox
            {
                Image = big.ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Location = new Point(22, 28),
                Size = new Size(80, 80),
                BackColor = Color.Transparent,
            });
        }
        if (LoadAppIcon(32) is { } small)
            Icon = small;

        const int x = 122;
        Controls.Add(Label("SysWidge", x, 28, 15f, FontStyle.Bold, Color.White));
        Controls.Add(Label("Low-key taskbar system widget", x, 58, 9f, FontStyle.Regular, Color.FromArgb(150, 150, 156)));
        Controls.Add(Label($"Version {version}", x, 88, 9.5f, FontStyle.Regular, Color.White));
        Controls.Add(Label($"Built {built:MMM d, yyyy  h:mm tt}", x, 110, 9.5f, FontStyle.Regular, Color.FromArgb(190, 190, 196)));

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(82, 28),
            Location = new Point(ClientSize.Width - 102, ClientSize.Height - 42),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(38, 38, 44),
        };
        ok.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 78);
        Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = ok;
    }

    private static Label Label(string text, int x, int y, float size, FontStyle style, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        Location = new Point(x, y),
        Font = new Font("Segoe UI", size, style),
        ForeColor = color,
        BackColor = Color.Transparent,
    };

    private static Icon? LoadAppIcon(int size)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SysWidge.app.ico");
            return s is null ? null : new Icon(s, size, size);
        }
        catch
        {
            return null;
        }
    }

    private static DateTime BuildDate()
    {
        try { return File.GetLastWriteTime(Environment.ProcessPath!); }
        catch { return DateTime.Now; }
    }
}
