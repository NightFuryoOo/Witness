using System.Drawing.Imaging;
using System.Reflection;

namespace TheWitness;

internal sealed class SessionIndicatorOverlay : Form
{
    private const int BadgeSize = 40;
    private const int SideMargin = 24;
    private const int BottomMargin = 72;
    private const double BadgeOpacity = 0.45;
    private const byte AlphaThreshold = 128;

    private static readonly Color KeyColor = Color.FromArgb(255, 255, 0, 255);

    private static SessionIndicatorOverlay? instance;
    private static volatile bool userWantsVisible = true;

    internal static event Action<bool>? UserVisibilityChanged;

    private SessionIndicatorOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(BadgeSize, BadgeSize);
        Location = BottomLeftLocation();
        BackColor = KeyColor;
        TransparencyKey = KeyColor;
        Opacity = BadgeOpacity;

        PictureBox iconBox = new()
        {
            Image = LoadIcon(),
            SizeMode = PictureBoxSizeMode.CenterImage,
            BackColor = KeyColor,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill
        };
        Controls.Add(iconBox);
    }

    internal static void Initialize()
    {
        instance = new SessionIndicatorOverlay();
        instance.Show();
    }

    internal static void SetUserVisible(bool visible)
    {
        userWantsVisible = visible;
        instance?.ApplyVisible(visible);
        UserVisibilityChanged?.Invoke(visible);
    }

    internal static void OnGameExited()
    {
        userWantsVisible = true;
        instance?.ApplyVisible(true);
        UserVisibilityChanged?.Invoke(true);
    }

    internal static void Shutdown()
    {
        instance?.Dispose();
        instance = null;
    }

    private void ApplyVisible(bool visible)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            TryBeginInvoke(() => ApplyVisible(visible));
            return;
        }

        Visible = visible;
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

    private static Point BottomLeftLocation()
    {
        Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        return new Point(bounds.Left + SideMargin, bounds.Bottom - BadgeSize - BottomMargin);
    }

    private static Image? LoadIcon()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TheWitness.AppIcon.ico");
        if (stream is null)
        {
            return null;
        }

        using Icon sourceIcon = new(stream);
        using Icon sizedIcon = new(sourceIcon, BadgeSize, BadgeSize);
        using Bitmap source = sizedIcon.ToBitmap();
        return BuildKeyedBitmap(source);
    }

    private static Bitmap BuildKeyedBitmap(Bitmap source)
    {
        Bitmap result = new(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Color pixel = source.GetPixel(x, y);
                result.SetPixel(x, y, pixel.A < AlphaThreshold
                    ? KeyColor
                    : Color.FromArgb(255, pixel.R, pixel.G, pixel.B));
            }
        }

        return result;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExNoActivate = 0x08000000;
            const int WsExToolWindow = 0x00000080;
            const int WsExTransparent = 0x00000020;
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTransparent;
            return cp;
        }
    }
}
