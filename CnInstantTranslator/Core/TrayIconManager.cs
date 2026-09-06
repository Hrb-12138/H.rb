using System.Drawing;
using System.Windows.Forms;

namespace CnInstantTranslator.Core;

public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public TrayIconManager(Action toggleOverlay, Action<bool> togglePause, bool paused, Action exit)
    {
        var menu = new ContextMenuStrip();

        var showHide = new ToolStripMenuItem("显示/隐藏翻译框");
        showHide.Click += (_, _) => toggleOverlay();

        var pauseItem = new ToolStripMenuItem("暂停翻译")
        {
            CheckOnClick = true,
            Checked = paused
        };
        pauseItem.Click += (_, _) => togglePause(pauseItem.Checked);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => exit();

        menu.Items.Add(showHide);
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "中文即时翻译",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => toggleOverlay();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
