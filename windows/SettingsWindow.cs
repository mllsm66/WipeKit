using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WipeKit
{
    /// <summary>Einstellungen (Strg+,) – Allgemein, Löschen, Berichte, Benachrichtigungen, Updates</summary>
    public sealed class SettingsWindow : Window
    {
        readonly AppSettings _s = AppSettings.Current;
        readonly Func<bool> _jobsActive;

        public SettingsWindow(Window owner, Func<bool> jobsActive, int tab = 0)
        {
            _jobsActive = jobsActive;
            Title = "Einstellungen";
            Width = 640;
            Height = 640;
            MinWidth = 560;
            MinHeight = 480;
            Owner = owner;
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            var tabs = new TabControl { Margin = new Thickness(12) };
            tabs.Items.Add(Tab("Allgemein", General()));
            tabs.Items.Add(Tab("Löschen", Wipe()));
            tabs.Items.Add(Tab("Berichte", ReportsPane()));
            tabs.Items.Add(Tab("Benachrichtigungen", Notifications()));
            tabs.Items.Add(Tab("Updates", Updates()));
            tabs.SelectedIndex = tab;
            Content = tabs;
            Closed += (s, e) => _s.Save();
        }

        // --- Bausteine ---------------------------------------------------------------------------------

        static TabItem Tab(string header, UIElement content) => new TabItem
        {
            Header = header,
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4, 12, 12, 12) },
        };

        static StackPanel Panel() => new StackPanel { Margin = new Thickness(8, 0, 8, 8) };

        static TextBlock Heading(string text) => new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };

        static TextBlock Note(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 8) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            return t;
        }

        CheckBox Check(string text, bool value, Action<bool> set)
        {
            var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
            c.Checked += (s, e) => { set(true); _s.Save(); };
            c.Unchecked += (s, e) => { set(false); _s.Save(); };
            return c;
        }

        TextBox Field(StackPanel p, string label, string value, Action<string> set, string hint = null)
        {
            p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            var t = new TextBox { Text = value ?? "" };
            if (hint != null) t.ToolTip = hint;
            t.TextChanged += (s, e) => set(t.Text);
            t.LostFocus += (s, e) => _s.Save();
            p.Children.Add(t);
            return t;
        }

        PasswordBox Secret(StackPanel p, string label, string value, Action<string> set)
        {
            p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            var t = new PasswordBox { Password = value ?? "" };
            t.PasswordChanged += (s, e) => set(t.Password);
            t.LostFocus += (s, e) => _s.Save();
            p.Children.Add(t);
            return t;
        }

        static ComboBox Choice(StackPanel p, string label, (string Key, string Title)[] items, string selected, Action<string> set)
        {
            p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            var c = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (k, t) in items) c.Items.Add(new ComboBoxItem { Content = t, Tag = k });
            c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? c.Items[0];
            c.SelectionChanged += (s, e) => set((string)((ComboBoxItem)c.SelectedItem).Tag);
            p.Children.Add(c);
            return c;
        }

        static Button Btn(string text, bool accent = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 8, 0), HorizontalAlignment = HorizontalAlignment.Left };
            if (accent) b.SetResourceReference(StyleProperty, "AccentButtonStyle");
            return b;
        }

        // --- Seiten -----------------------------------------------------------------------------------

        UIElement General()
        {
            var p = Panel();
            p.Children.Add(Heading("Mitteilungen"));
            p.Children.Add(Check("Mitteilung anzeigen, wenn ein Vorgang endet", _s.NotifyLocal, v => _s.NotifyLocal = v));
            p.Children.Add(Check("Ton abspielen, wenn ein Vorgang endet", _s.PlaySound, v => _s.PlaySound = v));
            p.Children.Add(Note("Die Mitteilung erscheint unten rechts. Während eines Vorgangs geht der PC nicht in den Energiesparmodus."));
            p.Children.Add(Heading("Zurücksetzen"));
            var reset = Btn("Alle Einstellungen zurücksetzen …");
            reset.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Auch gespeicherte Zugangsdaten für Benachrichtigungen werden gelöscht. Berichte bleiben erhalten.",
                        "Alle Einstellungen zurücksetzen?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                {
                    _s.Reset();
                    Close();
                }
            };
            p.Children.Add(reset);
            return p;
        }

        UIElement Wipe()
        {
            var p = Panel();
            p.Children.Add(Heading("Löschen"));
            var modes = WipeMode.All.Where(m => !m.ReadOnly).Select(m => (m.Key, m.Title)).ToArray();
            Choice(p, "Vorausgewählte Methode", modes, _s.DefaultMode, v => { _s.DefaultMode = v; _s.Save(); });
            p.Children.Add(Note("Bei SSDs wählt WipeKit automatisch die Methode »SSD« vor."));
            return p;
        }

        UIElement ReportsPane()
        {
            var p = Panel();
            p.Children.Add(Heading("Speicherort"));
            var path = new TextBlock { Text = _s.ReportsFolder, TextWrapping = TextWrapping.Wrap };
            p.Children.Add(path);
            var change = Btn("Ändern …");
            change.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Ordner für Berichte", InitialDirectory = _s.ReportsFolder };
                if (dlg.ShowDialog(this) == true)
                {
                    _s.ReportsPath = dlg.FolderName;
                    path.Text = _s.ReportsFolder;
                    _s.Save();
                }
            };
            p.Children.Add(change);
            p.Children.Add(Note("In den Unterordnern »PDF« und »Text«."));
            p.Children.Add(Check("Ausführliches Löschprotokoll zusätzlich erstellen", _s.DetailedReport, v => _s.DetailedReport = v));
            p.Children.Add(Check("Seriennummer im Dateinamen", _s.SerialInFileName, v => _s.SerialInFileName = v));
            p.Children.Add(Heading("Angaben im Protokoll"));
            Field(p, "Durchgeführt von", _s.OperatorName, v => _s.OperatorName = v);
            Field(p, "Firma / Organisation (optional)", _s.Organization, v => _s.Organization = v);
            Field(p, "Notiz (z. B. Auftragsnummer)", _s.ReportNote, v => _s.ReportNote = v);
            return p;
        }

        UIElement Notifications()
        {
            var p = Panel();
            var c = _s.Push;
            p.Children.Add(Heading("Benachrichtigung aufs Handy"));
            p.Children.Add(Note("Schickt eine Nachricht, wenn ein Vorgang endet."));
            var detail = new StackPanel();
            var events = new StackPanel();
            var result = Note("");
            Choice(p, "Dienst", new[] { ("off", "Aus"), ("hub", "WipeKit Hub (selbst gehostet)"), ("ntfy", "ntfy"), ("discord", "Discord") },
                c.Service, v => { c.Service = v; _s.Save(); Fill(); });
            p.Children.Add(detail);
            p.Children.Add(events);

            void Fill()
            {
                detail.Children.Clear();
                events.Children.Clear();
                switch (c.Service)
                {
                    case "hub":
                        detail.Children.Add(Heading("WipeKit Hub"));
                        var code = Field(detail, "Kopplungscode (WKHUB1:…)", c.HubCode, v => c.HubCode = v.Trim());
                        code.FontFamily = new FontFamily("Consolas");
                        var hubInfo = Note("");
                        detail.Children.Add(hubInfo);
                        void Show() => hubInfo.Text = Push.ParseHubCode(c.HubCode) is HubCode h ? "Hub: " + h.Url
                            : (c.HubCode == "" ? "Den Code bekommst du im Hub unter »Geräte«." : "Der Code ist ungültig.");
                        code.TextChanged += (s, e) => Show();
                        Show();
                        detail.Children.Add(Check("Protokolle (PDF) mitsenden", c.HubFiles, v => c.HubFiles = v));
                        detail.Children.Add(Note("Protokolle und Laufwerksdaten werden auf diesem PC verschlüsselt. Nur dein Browser kann sie mit deinem Hub-Passwort öffnen – der Server selbst nicht. Lesbar ist für ihn nur die kurze Mitteilung."));
                        break;
                    case "ntfy":
                        detail.Children.Add(Heading("ntfy"));
                        Field(detail, "Server", c.NtfyServer, v => c.NtfyServer = v.Trim());
                        Field(detail, "Thema (langer, zufälliger Name)", c.NtfyTopic, v => c.NtfyTopic = v.Trim());
                        var authPanel = new StackPanel();
                        Choice(detail, "Anmeldung", new[] { ("none", "Keine"), ("token", "Zugriffstoken"), ("password", "Benutzer und Passwort") },
                            c.NtfyAuth, v => { c.NtfyAuth = v; _s.Save(); Auth(); });
                        detail.Children.Add(authPanel);
                        void Auth()
                        {
                            authPanel.Children.Clear();
                            if (c.NtfyAuth == "password") Field(authPanel, "Benutzer", c.NtfyUser, v => c.NtfyUser = v);
                            if (c.NtfyAuth != "none") Secret(authPanel, c.NtfyAuth == "token" ? "Token" : "Passwort", c.NtfySecret, v => c.NtfySecret = v);
                        }
                        Auth();
                        detail.Children.Add(Note("Gesendet wird nur die kurze Mitteilung, keine Protokolle. Auf dem öffentlichen Server ntfy.sh kann jeder mitlesen, der das Thema kennt – wähle einen langen, zufälligen Namen oder einen eigenen Server mit Anmeldung. Auch ein eigener Server leitet für das iPhone einen Weckruf (ohne Inhalt) über ntfy.sh."));
                        break;
                    case "discord":
                        detail.Children.Add(Heading("Discord"));
                        Secret(detail, "Webhook-Adresse (https://discord.com/api/webhooks/…)", c.DiscordWebhook, v => c.DiscordWebhook = v.Trim());
                        detail.Children.Add(Note("Gesendet wird nur die kurze Mitteilung, keine Protokolle – Discord-Nachrichten sind nicht Ende-zu-Ende-verschlüsselt. Den Webhook legst du in Discord unter Kanal bearbeiten › Integrationen an."));
                        break;
                }
                if (c.Service == "off") return;
                events.Children.Add(Heading("Benachrichtigen bei"));
                events.Children.Add(Check("Erfolgreich beendet", c.OnDone, v => c.OnDone = v));
                events.Children.Add(Check("Fehlgeschlagen", c.OnFailed, v => c.OnFailed = v));
                events.Children.Add(Check("Abgebrochen", c.OnCancelled, v => c.OnCancelled = v));
                events.Children.Add(Check("Gesundheitswarnung", c.OnWarning, v => c.OnWarning = v));
                var test = Btn("Testnachricht senden");
                test.Click += async (s, e) =>
                {
                    _s.Save();
                    if (!c.IsConfigured) { result.Text = "Bitte zuerst alle Angaben ausfüllen."; return; }
                    test.IsEnabled = false;
                    result.Text = "Wird gesendet …";
                    var ev = new PushEvent
                    {
                        Kind = "test", Notice = $"Testnachricht von WipeKit auf »{Environment.MachineName}« – alles eingerichtet.",
                        Meta = { ["host"] = Environment.MachineName, ["platform"] = "Windows", ["app"] = ShellWindow.AppVersion },
                    };
                    string r = await Task.Run(() => Push.Send(c, ev, force: true));
                    result.Text = r ?? "Gesendet.";
                    test.IsEnabled = true;
                };
                events.Children.Add(test);
                events.Children.Add(result);
            }
            Fill();
            return p;
        }

        UIElement Updates()
        {
            var p = Panel();
            p.Children.Add(Heading("Updates"));
            p.Children.Add(new TextBlock { Text = "Installierte Version: " + ShellWindow.AppVersion });
            var status = Note(Updater.Status);
            p.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var check = Btn("Jetzt suchen");
            var install = Btn("Installieren", accent: true);
            var page = Btn("Auf GitHub ansehen");
            install.Visibility = page.Visibility = Visibility.Collapsed;
            ReleaseInfo found = null;
            check.Click += async (s, e) =>
            {
                check.IsEnabled = false;
                status.Text = "Suche nach Updates …";
                found = await Updater.Check();
                status.Text = Updater.Status;
                install.Visibility = Updater.CanInstall(found) ? Visibility.Visible : Visibility.Collapsed;
                page.Visibility = found != null ? Visibility.Visible : Visibility.Collapsed;
                if (found != null) install.Content = $"Version {found.Version} installieren";
                check.IsEnabled = true;
            };
            install.Click += async (s, e) =>
            {
                if (_jobsActive()) { status.Text = "Erst nach dem laufenden Vorgang möglich."; return; }
                install.IsEnabled = false;
                status.Text = $"Lade Version {found.Version} …";
                string err = await Updater.Install(found);
                if (err == null) { Application.Current.Shutdown(); return; }
                status.Text = err;
                install.IsEnabled = true;
            };
            page.Click += (s, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(found.PageUrl) { UseShellExecute = true }); } catch { } };
            buttons.Children.Add(check);
            buttons.Children.Add(install);
            buttons.Children.Add(page);
            p.Children.Add(buttons);
            p.Children.Add(Heading("Automatisch"));
            var auto = Check("Beim Start automatisch nach Updates suchen", _s.AutoCheck, v => _s.AutoCheck = v);
            var autoInst = Check("Updates automatisch laden und installieren", _s.AutoInstall, v => _s.AutoInstall = v);
            autoInst.IsEnabled = _s.AutoCheck;
            auto.Checked += (s, e) => autoInst.IsEnabled = true;
            auto.Unchecked += (s, e) => autoInst.IsEnabled = false;
            p.Children.Add(auto);
            p.Children.Add(autoInst);
            p.Children.Add(Note("Updates kommen aus den Releases auf GitHub und werden vor der Installation anhand ihrer digitalen Signatur geprüft. Während ein Vorgang läuft, wird nichts installiert. Die portable Fassung aktualisierst du von Hand."));
            return p;
        }
    }
}
