using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace FocusListener.App;

internal sealed class TrayController : IDisposable
{
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _icon;

    public TrayController(MainWindow window)
    {
        _window = window;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(ProductText.Choose("显示 Focus Listener", "Show Focus Listener"), null,
            (_, _) => _window.ShowFromTray());
        menu.Items.Add(ProductText.Choose("结束当前课堂", "End current session"), null,
            (_, _) => _window.EndSessionFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(ProductText.Choose("退出", "Quit"), null,
            (_, _) => _window.Close());
        _icon = new Forms.NotifyIcon
        {
            Text = "Focus Listener",
            Icon = SystemIcons.Information,
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => _window.ShowFromTray();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
