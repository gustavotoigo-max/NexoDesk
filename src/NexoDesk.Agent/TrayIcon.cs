using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace NexoDesk.Agent;

/// <summary>Ícone na área de notificação com o estado do Agent e o menu Abrir / Encerrar acesso / Sair.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _endItem;
    private readonly Icon _idleIcon;
    private readonly Icon _activeIcon;
    private bool _disposed;

    public TrayIcon(Action open, Action endSession, Action exit)
    {
        _idleIcon = DrawIcon(active: false);
        _activeIcon = DrawIcon(active: true);

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Abrir NexoDesk Agent", null, (_, _) => open());
        _endItem = new WinForms.ToolStripMenuItem("Encerrar acesso remoto", null, (_, _) => endSession()) { Enabled = false };
        menu.Items.Add(_endItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => exit());

        _icon = new WinForms.NotifyIcon
        {
            Icon = _idleIcon,
            Text = "NexoDesk Agent",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public void Update(AgentState state, string? admin)
    {
        if (_disposed)
            return;
        bool active = state == AgentState.InSession;
        _icon.Icon = active ? _activeIcon : _idleIcon;
        _endItem.Enabled = active;
        string text = state switch
        {
            AgentState.InSession => $"NexoDesk — acesso remoto ativo ({admin})",
            AgentState.Listening => "NexoDesk Agent — aguardando conexões",
            AgentState.Error => "NexoDesk Agent — não iniciado (abra para ver o motivo)",
            _ => "NexoDesk Agent — parado",
        };
        _icon.Text = text.Length > 63 ? text[..63] : text; // limite do Windows
    }

    public void Notify(string title, string text)
    {
        if (!_disposed)
            _icon.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        DestroyIcon(_idleIcon.Handle);
        DestroyIcon(_activeIcon.Handle);
        _idleIcon.Dispose();
        _activeIcon.Dispose();
    }

    /// <summary>Quadrado arredondado em degradê azul → ciano com um monitor; ponto vermelho quando há sessão.</summary>
    private static Icon DrawIcon(bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var path = RoundedRect(new RectangleF(1, 1, 30, 30), 8);
            using var fill = new LinearGradientBrush(new PointF(0, 16), new PointF(32, 16), Color.FromArgb(0x25, 0x63, 0xEB), Color.FromArgb(0x22, 0xD3, 0xEE));
            g.FillPath(fill, path);

            using var pen = new Pen(Color.White, 2.2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawRectangle(pen, 7, 8, 18, 12);
            g.DrawLine(pen, 13, 25, 19, 25);
            g.DrawLine(pen, 16, 20, 16, 25);

            if (active)
            {
                using var red = new SolidBrush(Color.FromArgb(0xEF, 0x44, 0x44));
                using var ring = new Pen(Color.White, 2f);
                g.FillEllipse(red, 19, 0, 12, 12);
                g.DrawEllipse(ring, 19, 0, 12, 12);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
