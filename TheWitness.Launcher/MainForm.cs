using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace TheWitness.Launcher;

internal sealed class MainForm : Form
{
    private const int WindowWidth = 360;
    private const int WindowHeight = 240;
    private const int CornerBracketLength = 14;
    private const int IconSize = 64;

    private static readonly Color BackgroundColor = Color.FromArgb(255, 10, 10, 12);
    private static readonly Color BoneColor = Color.FromArgb(255, 214, 205, 183);
    private static readonly Color AccentColor = Color.FromArgb(255, 224, 155, 58);
    private static readonly Color ButtonHoverColor = Color.FromArgb(255, 30, 28, 24);

    private readonly Label statusLabel;
    private readonly Button actionButton;
    private double progressPercent;

    private LatestReleaseInfo? pendingUpdate;

    internal MainForm()
    {
        Text = "The Witness";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Size = new Size(WindowWidth, WindowHeight);
        BackColor = BackgroundColor;
        Location = CenteredLocation();
        Icon = LoadAppIcon();

        PictureBox iconBox = new()
        {
            Image = LoadAppIconBitmap(),
            SizeMode = PictureBoxSizeMode.CenterImage,
            BackColor = Color.Transparent,
            Location = new Point((WindowWidth - IconSize) / 2, 20),
            Size = new Size(IconSize, IconSize)
        };

        statusLabel = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = BoneColor,
            BackColor = Color.Transparent,
            Font = ResolveThemedFont(11f, FontStyle.Regular),
            Location = new Point(20, 92),
            Size = new Size(WindowWidth - 40, 40),
            Text = "Проверка обновлений..."
        };

        actionButton = new Button
        {
            Location = new Point((WindowWidth - 160) / 2, 172),
            Size = new Size(160, 34),
            Text = "Запустить",
            Visible = false,
            FlatStyle = FlatStyle.Flat,
            BackColor = BackgroundColor,
            ForeColor = BoneColor,
            Font = ResolveThemedFont(11f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        actionButton.FlatAppearance.BorderColor = BoneColor;
        actionButton.FlatAppearance.BorderSize = 1;
        actionButton.FlatAppearance.MouseOverBackColor = ButtonHoverColor;
        actionButton.FlatAppearance.MouseDownBackColor = AccentColor;
        actionButton.Click += OnActionButtonClick;

        Controls.Add(iconBox);
        Controls.Add(statusLabel);
        Controls.Add(actionButton);

        Shown += async (_, _) => await RunInitialCheckAsync();
    }

    private async Task RunInitialCheckAsync()
    {
        LatestReleaseInfo? latest = await UpdateChecker.CheckAsync(CancellationToken.None);
        string? installedHash = InstalledWitness.TryComputeInstalledSha256();

        bool updateAvailable = latest is not null &&
            !string.Equals(installedHash, latest.Sha256Hex, StringComparison.OrdinalIgnoreCase);

        if (updateAvailable)
        {
            ShowUpdateAvailable(latest!);
        }
        else
        {
            ShowReadyToStart();
        }
    }

    private void ShowReadyToStart()
    {
        pendingUpdate = null;
        statusLabel.Text = "Готово к запуску";
        SetProgress(0);
        actionButton.Text = "Запустить";
        actionButton.Enabled = true;
        actionButton.Visible = true;
    }

    private void ShowUpdateAvailable(LatestReleaseInfo latest)
    {
        pendingUpdate = latest;
        statusLabel.Text = $"Доступно обновление: версия {latest.Version}";
        SetProgress(0);
        actionButton.Text = "Обновить";
        actionButton.Enabled = true;
        actionButton.Visible = true;
    }

    private void ShowUpdateFailed(string message)
    {
        statusLabel.Text = message;
        SetProgress(0);
        actionButton.Text = "Повторить";
        actionButton.Enabled = true;
        actionButton.Visible = true;
    }

    private void SetProgress(double percent)
    {
        progressPercent = Math.Clamp(percent, 0, 100);
        Invalidate();
    }

    private async void OnActionButtonClick(object? sender, EventArgs e)
    {
        if (pendingUpdate is not null)
        {
            await RunUpdateAsync(pendingUpdate);
            return;
        }

        LaunchWitnessAndExit();
    }

    private async Task RunUpdateAsync(LatestReleaseInfo update)
    {
        actionButton.Visible = false;
        actionButton.Enabled = false;
        statusLabel.Text = "Скачивание обновления...";
        SetProgress(0);

        Progress<double> progress = new(SetProgress);

        try
        {
            await UpdateInstaller.ApplyAsync(update, progress, CancellationToken.None);
            ShowReadyToStart();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            ShowUpdateFailed("Не удалось обновиться. Проверьте подключение и попробуйте снова.");
        }
    }

    private void LaunchWitnessAndExit()
    {
        try
        {
            Process.Start(new ProcessStartInfo(InstalledWitness.ExePath)
            {
                WorkingDirectory = InstalledWitness.InstallDirectory,
                UseShellExecute = true
            });
            Close();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            statusLabel.Text = "Не удалось запустить TheWitness.exe.";
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

        Rectangle track = new(28, 140, Width - 56, 16);
        using Pen trackPen = new(BoneColor, 1.5f);
        g.DrawRectangle(trackPen, track);

        int fillWidth = (int)((track.Width - 4) * (progressPercent / 100.0));
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

    private static Point CenteredLocation()
    {
        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return new Point(
            workingArea.Left + (workingArea.Width - WindowWidth) / 2,
            workingArea.Top + (workingArea.Height - WindowHeight) / 2);
    }

    private static Image? LoadAppIconBitmap()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TheWitness.Launcher.AppIcon.ico");
        if (stream is null)
        {
            return null;
        }

        using Icon sourceIcon = new(stream);
        using Icon sizedIcon = new(sourceIcon, IconSize, IconSize);
        return sizedIcon.ToBitmap();
    }

    private static Icon? LoadAppIcon()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TheWitness.Launcher.AppIcon.ico");
        return stream is null ? null : new Icon(stream);
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
}
