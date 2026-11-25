using System;
using System.Drawing;
using System.IO;

namespace WipeKit
{
    /// <summary>
    /// Mitteilung unten rechts (Windows zeigt sie als Benachrichtigung im Info-Center).
    /// Über das Infobereich-Symbol – kommt ohne Registrierung und Zusatzpakete aus.
    /// </summary>
    public static class Notifier
    {
        static System.Windows.Forms.NotifyIcon _icon;

        public static void Show(string title, string text, bool warning)
        {
            try
            {
                if (_icon == null)
                {
                    _icon = new System.Windows.Forms.NotifyIcon { Text = "WipeKit", Visible = true };
                    try
                    {
                        string exe = Environment.ProcessPath;
                        _icon.Icon = exe != null && File.Exists(exe) ? Icon.ExtractAssociatedIcon(exe) : SystemIcons.Information;
                    }
                    catch { _icon.Icon = SystemIcons.Information; }
                    _icon.BalloonTipClicked += (s, e) => Activate();
                    _icon.Click += (s, e) => Activate();
                }
                _icon.ShowBalloonTip(10000, title, text.Length > 250 ? text.Substring(0, 250) + " …" : text,
                    warning ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
            }
            catch { }
        }

        static void Activate()
        {
            var w = System.Windows.Application.Current?.MainWindow;
            if (w == null) return;
            if (w.WindowState == System.Windows.WindowState.Minimized) w.WindowState = System.Windows.WindowState.Normal;
            w.Activate();
        }

        public static void Dispose()
        {
            try { _icon?.Dispose(); } catch { }
            _icon = null;
        }
    }
}
