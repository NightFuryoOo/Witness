using System.Drawing.Drawing2D;
using System.Reflection;

namespace TheWitness;

internal sealed class BaselineProgressToast : Form
{
    private const int ToastWidth = 340;
    private const int ToastHeight = 130;
    private const int CloseDelayMs = 700;
    private const int CornerBracketLength = 14;
    private const int IconSize = 40;

    private static readonly Color BackgroundColor = Color.FromArgb(255, 10, 10, 12);
    private static readonly Color BoneColor = Color.FromArgb(255, 214, 205, 183);
    private static readonly Color AccentColor = Color.FromArgb(255, 224, 155, 58);

    private readonly PictureBox iconBox;
    private readonly Label percentLabel;
    private readonly System.Windows.Forms.Timer closeTimer;
    private int progressPercent;
    private int lastPaintedPercent = -1;

    private BaselineProgressToast()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Size = new Size(ToastWidth, ToastHeight);
        BackColor = BackgroundColor;
        Location = CenteredLocation();

        iconBox = new PictureBox
        {
            Image = LoadTrayIcon(),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Location = new Point((ToastWidth - IconSize) / 2, 14),
            Size = new Size(IconSize, IconSize)
        };

        percentLabel = new Label
        {
            Text = "0%",
            ForeColor = BoneColor,
            BackColor = Color.Transparent,
            Font = ResolveThemedFont(20f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 54),
            Size = new Size(ToastWidth, 38)
        };

        Controls.Add(iconBox);
        Controls.Add(percentLabel);

        closeTimer = new System.Windows.Forms.Timer { Interval = CloseDelayMs };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            Close();
        };
    }

    internal static BaselineProgressToast Create()
    {
        BaselineProgressToast toast = new();
        toast.Show();
        return toast;
    }

    internal void ReportProgress(int completed, int total)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            TryBeginInvoke(() => ReportProgress(completed, total));
            return;
        }

        int percent = total > 0 ? (int)(completed * 100L / total) : 100;
        percent = Math.Clamp(percent, 0, 100);
        progressPercent = percent;
        percentLabel.Text = $"{percent}%";

        if (percent != lastPaintedPercent)
        {
            lastPaintedPercent = percent;
            Invalidate();
        }
    }

    internal void CompleteAndClose()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            TryBeginInvoke(CompleteAndClose);
            return;
        }

        progressPercent = 100;
        percentLabel.Text = "100%";
        Invalidate();
        closeTimer.Start();
    }

    private void TryBeginInvoke(Action action)
    {
        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using Pen outerPen = new(BoneColor, 2f);
        g.DrawRectangle(outerPen, 4, 4, Width - 9, Height - 9);

        using Pen innerPen = new(BoneColor, 1f);
        g.DrawRectangle(innerPen, 10, 10, Width - 21, Height - 21);

        DrawCornerBracket(g, outerPen, 4, 4, 1, 1);
        DrawCornerBracket(g, outerPen, Width - 5, 4, -1, 1);
        DrawCornerBracket(g, outerPen, 4, Height - 5, 1, -1);
        DrawCornerBracket(g, outerPen, Width - 5, Height - 5, -1, -1);

        Rectangle track = ProgressTrackBounds();
        using Pen trackPen = new(BoneColor, 1.5f);
        g.DrawRectangle(trackPen, track);

        int fillWidth = (int)((track.Width - 4) * (progressPercent / 100f));
        if (fillWidth > 0)
        {
            using SolidBrush fillBrush = new(AccentColor);
            g.FillRectangle(fillBrush, track.X + 2, track.Y + 2, fillWidth, track.Height - 4);
        }
    }

    private static void DrawCornerBracket(Graphics g, Pen pen, int x, int y, int dirX, int dirY)
    {
        g.DrawLine(pen, x, y, x + dirX * CornerBracketLength, y);
        g.DrawLine(pen, x, y, x, y + dirY * CornerBracketLength);
    }

    private Rectangle ProgressTrackBounds() => new(28, Height - 34, Width - 56, 16);

    private static Image? LoadTrayIcon()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TheWitness.AppIcon.ico");
        if (stream is null)
        {
            return null;
        }

        using Icon icon = new(stream);
        return icon.ToBitmap();
    }

    private static Font ResolveThemedFont(float size, FontStyle style)
    {
        string[] preferredFamilies = { "Perpetua", "Cambria", "Georgia", "Times New Roman" };
        foreach (string familyName in preferredFamilies)
        {
            if (FontFamily.Families.Any(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase)))
            {
                return new Font(familyName, size, style);
            }
        }

        return new Font(FontFamily.GenericSerif, size, style);
    }

    private static Point CenteredLocation()
    {
        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return new Point(
            workingArea.Left + (workingArea.Width - ToastWidth) / 2,
            workingArea.Top + (workingArea.Height - ToastHeight) / 2);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExNoActivate = 0x08000000;
            const int WsExToolWindow = 0x00000080;
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closeTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
