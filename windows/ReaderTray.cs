using System;
using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

sealed class ReaderTray : IDisposable
{
    readonly Window window;
    readonly Application application;
    readonly Action savePosition;
    readonly Icon icon;
    readonly Forms.ContextMenuStrip menu;
    readonly Forms.NotifyIcon notification;
    readonly RegisteredWaitHandle restoreWait;
    bool exiting;
    bool disposed;

    public ReaderTray(Window window, Action savePosition, EventWaitHandle wake)
    {
        this.window = window;
        this.savePosition = savePosition;
        application = Application.Current;
        icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 KomaSnap", null, (s, e) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 KomaSnap", null, (s, e) =>
            window.Dispatcher.BeginInvoke(new Action(Exit)));
        notification = new Forms.NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = "KomaSnap 漫画阅读器",
            ContextMenuStrip = menu,
            Visible = true
        };
        notification.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) ShowWindow(); };
        window.Closing += HideInsteadOfClose;
        application.SessionEnding += SessionEnding;
        if (wake != null)
            restoreWait = ThreadPool.RegisterWaitForSingleObject(wake, (s, timedOut) =>
                window.Dispatcher.BeginInvoke(new Action(ShowWindow)), null, Timeout.Infinite, false);
    }

    void HideInsteadOfClose(object sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        savePosition();
        window.Hide();
    }

    void ShowWindow()
    {
        if (exiting || disposed) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    void Exit()
    {
        if (exiting || disposed) return;
        exiting = true;
        notification.Visible = false;
        window.Close();
    }

    void SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        exiting = true;
        notification.Visible = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (restoreWait != null) restoreWait.Unregister(null);
        application.SessionEnding -= SessionEnding;
        window.Closing -= HideInsteadOfClose;
        notification.Dispose();
        menu.Dispose();
        if (icon != null) icon.Dispose();
    }
}
