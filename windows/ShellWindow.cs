using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WipeKit
{
    /// <summary>Gesundheitsdaten eines Laufwerks in der App.</summary>
    public sealed class HealthEntry
    {
        public Health Health;
        public string Json;
        public string DevType = "";
        public DateTime? ReadAt;
        public bool Loading;
        public string SelfTest;        // läuft gerade (Kurztest …)
        public string SelfTestMessage;
    }

    /// <summary>Hauptfenster: Seitenleiste mit Laufwerken und Vorgängen, rechts die Details (Löschen | Diagnose).</summary>
    public sealed class ShellWindow : Window
    {
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        static readonly Color Green = Color.FromRgb(0x0F, 0x7B, 0x0F);
        static readonly Color Red = Color.FromRgb(0xC4, 0x2B, 0x1C);
        static readonly Color Orange = Color.FromRgb(0xF7, 0x63, 0x0C);
        static readonly Color Blue = Color.FromRgb(0x00, 0x67, 0xC0);

        readonly bool _smoke;
        List<DiskInfo> _disks = new List<DiskInfo>();
        readonly List<WipeJob> _jobs = new List<WipeJob>();
        readonly Dictionary<Guid, JobState> _lastStates = new Dictionary<Guid, JobState>();
        readonly Dictionary<int, HealthEntry> _health = new Dictionary<int, HealthEntry>();
        string _selKind;     // "disk" | "job"
        string _selId;
        string _view = "wipe";   // "wipe" | "diag"
        WipeMode _mode = WipeMode.ByKey(AppSettings.Current.DefaultMode) ?? WipeMode.ByKey("secure");
        string _diskSignature = "";
        bool _updatingLists;
        bool _refreshing;
        bool _closingConfirmed;
        bool _healthWorker;

        readonly ListBox _diskList = new ListBox();
        readonly ListBox _intList = new ListBox();
        readonly ListBox _jobList = new ListBox();
        readonly TextBlock _noDisks;
        readonly TextBlock _intHeader;
        readonly TextBlock _jobsHeader;
        readonly TextBlock _sleepGlyph;
        readonly TextBlock _sleepText;
        readonly ContentControl _detailHost = new ContentControl();
        readonly Border _switchBar;
        readonly RadioButton _tabWipe, _tabDiag;
        readonly DispatcherTimer _timer;
        readonly DispatcherTimer _refreshDebounce;
        readonly Dictionary<Guid, (RingProgress Ring, TextBlock Glyph, TextBlock Status)> _jobRows =
            new Dictionary<Guid, (RingProgress, TextBlock, TextBlock)>();
        readonly Dictionary<int, RingProgress> _diskRings = new Dictionary<int, RingProgress>();
        Action _detailUpdater;
        Action _modeUpdater;

        public static string AppVersion => typeof(ShellWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        static string OsLabel => System.Runtime.InteropServices.RuntimeInformation.OSDescription;

        public ShellWindow(bool smokeTest)
        {
            _smoke = smokeTest;
            Title = "WipeKit";
            Width = 1120;
            Height = 800;
            MinWidth = 940;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/WipeKit.ico")); } catch { }

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // --- Seitenleiste ------------------------------------------------------------
            var side = new DockPanel { Margin = new Thickness(12, 12, 8, 8) };

            var header = new DockPanel { Margin = new Thickness(6, 0, 0, 12) };
            var refresh = IconButton("", "Laufwerke und Gesundheitsdaten neu einlesen (F5)");
            refresh.Click += (s, e) => { RefreshDisks(true); ReadAllHealth(true); };
            DockPanel.SetDock(refresh, Dock.Right);
            header.Children.Add(refresh);
            var info = IconButton("\uE946", "Über WipeKit und Lizenzen");
            info.Margin = new Thickness(4, 0, 0, 0);
            info.Click += (s, e) => ShowAbout();
            DockPanel.SetDock(info, Dock.Right);
            header.Children.Add(info);
            var gear = IconButton("\uE713", "Einstellungen (Strg+,)");
            gear.Margin = new Thickness(4, 0, 0, 0);
            gear.Click += (s, e) => ShowSettings();
            DockPanel.SetDock(gear, Dock.Right);
            header.Children.Add(gear);
            var folder = IconButton("", "Ordner mit Protokollen und Berichten öffnen (Strg+O)");
            folder.Margin = new Thickness(0, 0, 4, 0);
            folder.Click += (s, e) => OpenReportsFolder();
            DockPanel.SetDock(folder, Dock.Right);
            header.Children.Add(folder);
            var appIcon = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) };
            try { appIcon.Source = BitmapFrame.Create(new Uri("pack://application:,,,/WipeKit.ico")); } catch { }
            header.Children.Add(appIcon);
            header.Children.Add(Text("WipeKit", 20, FontWeights.SemiBold, verticalCenter: true));
            DockPanel.SetDock(header, Dock.Top);
            side.Children.Add(header);

            var sleepPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 0, 4) };
            _sleepGlyph = Glyph("", 14);
            _sleepGlyph.Margin = new Thickness(0, 0, 8, 0);
            _sleepText = Text("", 12, brushKey: "TextFillColorSecondaryBrush");
            sleepPanel.Children.Add(_sleepGlyph);
            sleepPanel.Children.Add(_sleepText);
            DockPanel.SetDock(sleepPanel, Dock.Bottom);
            side.Children.Add(sleepPanel);

            var sideStack = new StackPanel();
            sideStack.Children.Add(SectionHeader("Externe Laufwerke"));
            _noDisks = Text("Kein Laufwerk angeschlossen", 13, brushKey: "TextFillColorSecondaryBrush");
            _noDisks.Margin = new Thickness(10, 2, 0, 6);
            sideStack.Children.Add(_noDisks);
            sideStack.Children.Add(PrepareList(_diskList));
            _intHeader = SectionHeader("Interne Laufwerke");
            sideStack.Children.Add(_intHeader);
            sideStack.Children.Add(PrepareList(_intList));
            _jobsHeader = SectionHeader("Vorgänge");
            sideStack.Children.Add(_jobsHeader);
            sideStack.Children.Add(PrepareList(_jobList));
            side.Children.Add(new ScrollViewer
            {
                Content = sideStack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
            Grid.SetColumn(side, 0);
            root.Children.Add(side);

            // --- Inhalt: Umschalter »Löschen | Diagnose« ------------------------------
            var switchPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _tabWipe = ViewTab("", "Löschen", "Löschen (Strg+1)");
            _tabDiag = ViewTab("", "Diagnose", "Diagnose (Strg+2)");
            _tabWipe.IsChecked = true;
            _tabWipe.Checked += (s, e) => SetView("wipe");
            _tabDiag.Checked += (s, e) => SetView("diag");
            switchPanel.Children.Add(_tabWipe);
            switchPanel.Children.Add(_tabDiag);
            _switchBar = new Border { Padding = new Thickness(0, 10, 0, 8), Child = switchPanel, BorderThickness = new Thickness(0, 0, 0, 1) };
            _switchBar.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            var detailDock = new DockPanel();
            DockPanel.SetDock(_switchBar, Dock.Top);
            detailDock.Children.Add(_switchBar);
            detailDock.Children.Add(_detailHost);

            var content = new Border
            {
                Margin = new Thickness(0, 12, 12, 12),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Child = detailDock,
                ClipToBounds = true,
            };
            content.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
            content.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            Grid.SetColumn(content, 1);
            root.Children.Add(content);
            Content = root;

            _diskList.SelectionChanged += OnListSelection;
            _intList.SelectionChanged += OnListSelection;
            _jobList.SelectionChanged += OnListSelection;
            Closing += OnClosing;

            // Tastenkürzel
            AddShortcut(Key.D1, ModifierKeys.Control, () => SetView("wipe"));
            AddShortcut(Key.D2, ModifierKeys.Control, () => SetView("diag"));
            AddShortcut(Key.F5, ModifierKeys.None, () => { RefreshDisks(true); ReadAllHealth(true); });
            AddShortcut(Key.S, ModifierKeys.Control, () => { if (_selKind == "disk" && FindDisk(_selId) is DiskInfo d) SaveHealthReport(d); });
            AddShortcut(Key.O, ModifierKeys.Control, OpenReportsFolder);
            AddShortcut(Key.OemComma, ModifierKeys.Control, () => ShowSettings());

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
            _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _refreshDebounce.Tick += (s, e) => { _refreshDebounce.Stop(); RefreshDisks(false); };
            var periodic = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            periodic.Tick += (s, e) => { if (!_smoke) RefreshDisks(false); };
            periodic.Start();

            RebuildJobList();
            UpdateSleepStatus();
            ShowDetail();

            if (_smoke)
                Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(RunSmokeTest), DispatcherPriority.ApplicationIdle);
            else
            {
                RefreshDisks(true);
                Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(UpdatesOnLaunch), DispatcherPriority.ApplicationIdle);
            }
            Closed += (s, e) => Notifier.Dispose();
        }

        void ShowSettings(int tab = 0)
        {
            new SettingsWindow(this, () => _jobs.Any(j => j.IsActive), tab).ShowDialog();
        }

        /// <summary>Erster Start: fragen; danach ggf. automatisch suchen und installieren</summary>
        async void UpdatesOnLaunch()
        {
            var s = AppSettings.Current;
            if (!s.UpdatesAsked)
            {
                s.UpdatesAsked = true;
                var a = MessageBox.Show(this, "WipeKit kann bei jedem Start auf GitHub nach einer neuen Version suchen und sie installieren. " +
                    "Updates werden vorher anhand ihrer Signatur geprüft. Das lässt sich jederzeit in den Einstellungen ändern.\n\n" +
                    "Ja = automatisch suchen und installieren\nNein = nicht automatisch suchen",
                    "Automatisch nach Updates suchen?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                s.AutoCheck = s.AutoInstall = a == MessageBoxResult.Yes;
                s.Save();
            }
            if (!s.AutoCheck) return;
            var r = await Updater.Check();
            if (r == null) return;
            if (!s.AutoInstall || !Updater.CanInstall(r))
            {
                Notifier.Show("Update verfügbar", $"WipeKit {r.Version} ist verfügbar. Einstellungen › Updates.", false);
                return;
            }
            while (_jobs.Any(j => j.IsActive)) await System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(1));
            string err = await Updater.Install(r);
            if (err == null) Application.Current.Shutdown();
            else Notifier.Show("Update fehlgeschlagen", err, true);
        }

        void AddShortcut(Key key, ModifierKeys mods, Action action)
        {
            var cmd = new RoutedCommand();
            cmd.InputGestures.Add(new KeyGesture(key, mods));
            CommandBindings.Add(new CommandBinding(cmd, (s, e) => action()));
        }

        RadioButton ViewTab(string glyph, string text, string tooltip)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var g = Glyph(glyph, 14);
            g.Margin = new Thickness(0, 0, 8, 0);
            sp.Children.Add(g);
            sp.Children.Add(new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton { Content = sp, GroupName = "view", ToolTip = tooltip, MinWidth = 130, Margin = new Thickness(2, 0, 2, 0),
                Padding = new Thickness(16, 6, 16, 6), HorizontalContentAlignment = HorizontalAlignment.Center };
            rb.SetResourceReference(FrameworkElement.StyleProperty, typeof(ToggleButton));
            return rb;
        }

        void SetView(string view)
        {
            if (_view == view && ((view == "wipe") == (_tabWipe.IsChecked == true))) return;
            _view = view;
            _tabWipe.IsChecked = view == "wipe";
            _tabDiag.IsChecked = view == "diag";
            if (view == "diag" && _selKind == "disk" && FindDisk(_selId) is DiskInfo d) EnsureHealth(d);
            ShowDetail();
        }

        static string Resource(string name)
        {
            using var st = typeof(ShellWindow).Assembly.GetManifestResourceStream(name);
            if (st == null) return "";
            using var r = new StreamReader(st);
            return r.ReadToEnd();
        }

        void ShowAbout()
        {
            var text = new TextBox
            {
                Text = $"WipeKit {AppVersion} – Laufwerke sicher löschen und ihre Gesundheit prüfen.\r\n" +
                       "© 2026 mllsm66 · MIT-Lizenz · Ohne Gewähr.\r\n\r\n" + Resource("WipeKit.THIRD-PARTY-NOTICES.md").Replace("\n", "\r\n"),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                FontSize = 13,
            };
            var gpl = NormalButton("GPL-Lizenztext (smartmontools)");
            gpl.Click += (s, e) =>
            {
                try
                {
                    string p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WipeKit-GPL-2.0.txt");
                    File.WriteAllText(p, Resource("WipeKit.GPL-2.0.txt"));
                    OpenFile(p);
                }
                catch { }
            };
            var close = AccentButton("Schließen");
            close.Margin = new Thickness(8, 0, 0, 0);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(gpl);
            buttons.Children.Add(close);
            var dock = new DockPanel { Margin = new Thickness(20) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(buttons);
            dock.Children.Add(text);
            var w = new Window { Title = "Über WipeKit", Width = 720, Height = 560, Owner = this, Content = dock,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResizeWithGrip };
            close.Click += (s, e) => w.Close();
            w.ShowDialog();
        }

        static void OpenReportsFolder()
        {
            try
            {
                Directory.CreateDirectory(Reports.Folder);
                Process.Start(new ProcessStartInfo(Reports.Folder) { UseShellExecute = true });
            }
            catch { }
        }

        // --- Laufwerkswechsel erkennen (An-/Abstecken) ------------------------------------

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WndProc);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_DEVICECHANGE && !_smoke)
            {
                _refreshDebounce.Stop();
                _refreshDebounce.Start();
            }
            return IntPtr.Zero;
        }

        // --- Bausteine ------------------------------------------------------------------------

        static TextBlock Text(string text, double size = 14, FontWeight? weight = null, string brushKey = null,
            bool wrap = false, bool verticalCenter = false)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal };
            if (wrap) t.TextWrapping = TextWrapping.Wrap;
            else t.TextTrimming = TextTrimming.CharacterEllipsis;
            if (verticalCenter) t.VerticalAlignment = VerticalAlignment.Center;
            if (brushKey != null) t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }

        static TextBlock Glyph(string glyph, double size, Color? color = null)
        {
            var t = new TextBlock
            {
                Text = glyph,
                FontFamily = IconFont,
                FontSize = size,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            if (color.HasValue) t.Foreground = new SolidColorBrush(color.Value);
            return t;
        }

        static Button IconButton(string glyph, string tooltip)
        {
            return new Button
            {
                Content = Glyph(glyph, 14),
                ToolTip = tooltip,
                Padding = new Thickness(8, 6, 8, 6),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        static TextBlock SectionHeader(string text)
        {
            var t = Text(text, 12, FontWeights.SemiBold, "TextFillColorSecondaryBrush");
            t.Margin = new Thickness(10, 14, 0, 6);
            return t;
        }

        static TextBlock PageSection(string text)
        {
            var t = Text(text, 14, FontWeights.SemiBold);
            t.Margin = new Thickness(2, 22, 0, 8);
            return t;
        }

        static ListBox PrepareList(ListBox list)
        {
            list.BorderThickness = new Thickness(0);
            list.Background = Brushes.Transparent;
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.PreviewMouseWheel += (s, e) =>
            {
                // Mausrad an die äußere Scrollfläche weitergeben
                e.Handled = true;
                var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent };
                ((UIElement)((FrameworkElement)s).Parent).RaiseEvent(args);
            };
            return list;
        }

        static Border Card(UIElement child)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 12, 16, 12),
                Child = child,
            };
            b.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            return b;
        }

        static Border Divider()
        {
            var d = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
            d.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
            return d;
        }

        /// <summary>Zeile »Bezeichnung … Wert« in einer Karte.</summary>
        static Grid InfoRow(string label, UIElement value)
        {
            var g = new Grid { MinHeight = 24 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = Text(label, 14, brushKey: "TextFillColorSecondaryBrush", verticalCenter: true);
            g.Children.Add(l);
            if (value is FrameworkElement fe)
            {
                fe.VerticalAlignment = VerticalAlignment.Center;
                if (fe.HorizontalAlignment == HorizontalAlignment.Stretch) fe.HorizontalAlignment = HorizontalAlignment.Right;
            }
            Grid.SetColumn(value, 1);
            g.Children.Add(value);
            return g;
        }

        static StackPanel Rows(params UIElement[] rows)
        {
            var sp = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) sp.Children.Add(Divider());
                sp.Children.Add(rows[i]);
            }
            return sp;
        }

        static TextBlock ValueText(string text)
        {
            var t = Text(text, 14, wrap: true);
            t.TextAlignment = TextAlignment.Right;
            return t;
        }

        static Border Tile(string glyph, Color color, double size, double glyphSize)
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 4.5),
                Background = new LinearGradientBrush(Lighten(color, 0.18), color, 90),
                Child = Glyph(glyph, glyphSize, Colors.White),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        static Color Lighten(Color c, double f) => Color.FromRgb(
            (byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));

        static DockPanel PageWithBar(UIElement body, UIElement bar)
        {
            var dock = new DockPanel();
            if (bar != null)
            {
                var border = new Border
                {
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Padding = new Thickness(24, 12, 24, 12),
                    Child = bar,
                };
                border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
                border.SetResourceReference(Border.BackgroundProperty, "LayerOnMicaBaseAltFillColorDefaultBrush");
                DockPanel.SetDock(border, Dock.Bottom);
                dock.Children.Add(border);
            }
            dock.Children.Add(new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
            return dock;
        }

        static StackPanel PageBody()
        {
            return new StackPanel { Margin = new Thickness(32, 28, 32, 28), MaxWidth = 900 };
        }

        static Button DangerButton(string text)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(18, 7, 18, 7),
                MinWidth = 110,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Red),
                BorderBrush = new SolidColorBrush(Red),
            };
            return b;
        }

        static Button AccentButton(string text)
        {
            var b = new Button { Content = text, Padding = new Thickness(18, 7, 18, 7), MinWidth = 110 };
            b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
            return b;
        }

        static Button NormalButton(string text)
        {
            return new Button { Content = text, Padding = new Thickness(18, 7, 18, 7), MinWidth = 100 };
        }

        // --- Laufwerke ------------------------------------------------------------------------

        DiskInfo FindDisk(string id) => _disks.FirstOrDefault(d => d.Number.ToString() == id);
        WipeJob FindJob(string id) => _jobs.FirstOrDefault(j => j.Id.ToString() == id);
        WipeJob ActiveJobFor(DiskInfo d) => _jobs.FirstOrDefault(j => j.Disk.Number == d.Number && j.IsActive);
        HealthEntry HealthFor(DiskInfo d) => _health.TryGetValue(d.Number, out var e) ? e : null;

        async void RefreshDisks(bool force)
        {
            if (_refreshing) return;
            _refreshing = true;
            List<DiskInfo> disks;
            try
            {
                disks = await Task.Run(() => DiskService.ListDisks());
            }
            catch
            {
                disks = new List<DiskInfo>();
            }
            finally
            {
                _refreshing = false;
            }
            ApplyDisks(disks, force);
            ReadAllHealth(false);
        }

        void ApplyDisks(List<DiskInfo> disks, bool force)
        {
            string sig = string.Join(";", disks.Select(d => d.Signature));
            if (sig == _diskSignature && !force) return;
            _diskSignature = sig;
            _disks = disks;
            if (_selKind == "disk" && FindDisk(_selId) == null) { _selKind = null; _selId = null; }
            if (_selKind == null)
            {
                var first = _disks.FirstOrDefault(d => !d.IsInternal && d.CanWipe && ActiveJobFor(d) == null) ?? _disks.FirstOrDefault();
                if (first != null) Select("disk", first.Number.ToString());
            }
            RebuildDiskList();
            ShowDetail();
        }

        /// <summary>Gesundheitsdaten im Hintergrund lesen (nacheinander, damit sich smartctl-Aufrufe nicht stören).</summary>
        async void ReadAllHealth(bool force)
        {
            if (_smoke || _healthWorker) return;
            _healthWorker = true;
            try
            {
                foreach (var d in _disks.ToList())
                {
                    var e = HealthFor(d);
                    if (e != null && e.Health != null && !force) continue;
                    if (ActiveJobFor(d) != null) continue;
                    await ReadHealth(d);
                }
            }
            finally { _healthWorker = false; }
        }

        async Task ReadHealth(DiskInfo d)
        {
            if (!_health.TryGetValue(d.Number, out var e)) _health[d.Number] = e = new HealthEntry();
            if (e.Loading) return;
            e.Loading = true;
            if (_view == "diag" && _selKind == "disk" && _selId == d.Number.ToString()) ShowDetail();
            try
            {
                var r = await Task.Run(() => SmartService.Read(d));
                e.Health = r.Health;
                e.Json = r.Json;
                e.DevType = r.DevType;
                e.ReadAt = DateTime.Now;
            }
            catch (Exception ex)
            {
                e.Health = new Health { Reason = "Die Gesundheitsdaten konnten nicht gelesen werden: " + ex.Message };
                e.ReadAt = DateTime.Now;
            }
            finally { e.Loading = false; }
            RebuildDiskList();
            if (_selKind == "disk" && _selId == d.Number.ToString()) ShowDetail();
        }

        void EnsureHealth(DiskInfo d)
        {
            var e = HealthFor(d);
            if (e == null || (e.Health == null && !e.Loading)) _ = ReadHealth(d);
        }

        void RebuildDiskList()
        {
            _updatingLists = true;
            _diskList.Items.Clear();
            _intList.Items.Clear();
            _diskRings.Clear();
            _noDisks.Visibility = _disks.Any(d => !d.IsInternal) ? Visibility.Collapsed : Visibility.Visible;
            _intHeader.Visibility = _disks.Any(d => d.IsInternal) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var d in _disks)
            {
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = Glyph(d.Glyph, 18);
                icon.Margin = new Thickness(0, 0, 12, 0);
                g.Children.Add(icon);
                var texts = new StackPanel();
                texts.Children.Add(Text(d.Name, 14));
                texts.Children.Add(Text($"{Fmt.Bytes(d.Size)} · {d.Id}", 12, brushKey: "TextFillColorSecondaryBrush"));
                Grid.SetColumn(texts, 1);
                g.Children.Add(texts);
                var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var h = HealthFor(d)?.Health;
                var job = ActiveJobFor(d);
                if (job != null)
                {
                    var ring = new RingProgress { Width = 16, Height = 16, Thickness = 2.5, Value = job.Fraction, RingColor = Blue };
                    right.Children.Add(ring);
                    _diskRings[d.Number] = ring;
                }
                else
                {
                    if (h != null && h.Available)
                    {
                        var (r, gg, b) = HealthParser.RatingRgb(h.Rating);
                        right.Children.Add(new System.Windows.Shapes.Ellipse
                        {
                            Width = 9, Height = 9, Margin = new Thickness(6, 0, 0, 0),
                            Fill = new SolidColorBrush(Color.FromRgb((byte)(r * 255), (byte)(gg * 255), (byte)(b * 255))),
                            ToolTip = "Gesamtzustand: " + HealthParser.RatingText(h.Rating),
                        });
                    }
                    if (d.IsProtected)
                    {
                        var lockIcon = Glyph("", 13);
                        lockIcon.Margin = new Thickness(6, 0, 0, 0);
                        lockIcon.ToolTip = "Systemlaufwerk – geschützt";
                        right.Children.Add(lockIcon);
                    }
                }
                Grid.SetColumn(right, 2);
                g.Children.Add(right);
                var item = new ListBoxItem { Content = g, Tag = "disk:" + d.Number };
                var list = d.IsInternal ? _intList : _diskList;
                list.Items.Add(item);
                if (_selKind == "disk" && _selId == d.Number.ToString()) list.SelectedItem = item;
            }
            _updatingLists = false;
        }

        // --- Vorgänge in der Seitenleiste ---------------------------------------------------

        void RebuildJobList()
        {
            _updatingLists = true;
            _jobList.Items.Clear();
            _jobRows.Clear();
            _jobsHeader.Visibility = _jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var j in _jobs)
            {
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var ring = new RingProgress { Width = 18, Height = 18, Thickness = 2.5, RingColor = Blue, HorizontalAlignment = HorizontalAlignment.Left };
                var glyph = Glyph("", 16);
                glyph.HorizontalAlignment = HorizontalAlignment.Left;
                g.Children.Add(ring);
                g.Children.Add(glyph);
                var texts = new StackPanel();
                texts.Children.Add(Text(j.IsCheck ? $"{j.Title} · {j.Disk.Name}" : j.Disk.Name, 14));
                var status = Text("", 12, brushKey: "TextFillColorSecondaryBrush");
                texts.Children.Add(status);
                Grid.SetColumn(texts, 1);
                g.Children.Add(texts);
                var item = new ListBoxItem { Content = g, Tag = "job:" + j.Id };
                _jobList.Items.Add(item);
                _jobRows[j.Id] = (ring, glyph, status);
                if (_selKind == "job" && _selId == j.Id.ToString()) _jobList.SelectedItem = item;
            }
            UpdateJobRows();
            _updatingLists = false;
        }

        void UpdateJobRows()
        {
            foreach (var j in _jobs)
            {
                if (!_jobRows.TryGetValue(j.Id, out var r)) continue;
                r.Status.Text = j.ShortStatus;
                if (j.IsActive)
                {
                    r.Ring.Visibility = Visibility.Visible;
                    r.Glyph.Visibility = Visibility.Collapsed;
                    r.Ring.Value = j.Fraction;
                }
                else
                {
                    r.Ring.Visibility = Visibility.Collapsed;
                    r.Glyph.Visibility = Visibility.Visible;
                    if (j.FoundProblem) { r.Glyph.Text = ""; r.Glyph.Foreground = new SolidColorBrush(Red); continue; }
                    switch (j.State)
                    {
                        case JobState.Done: r.Glyph.Text = ""; r.Glyph.Foreground = new SolidColorBrush(Green); break;
                        case JobState.Failed: r.Glyph.Text = ""; r.Glyph.Foreground = new SolidColorBrush(Red); break;
                        default: r.Glyph.Text = ""; r.Glyph.Foreground = new SolidColorBrush(Orange); break;
                    }
                }
            }
            foreach (var j in _jobs.Where(x => x.IsActive))
                if (_diskRings.TryGetValue(j.Disk.Number, out var ring)) ring.Value = j.Fraction;
        }

        void UpdateSleepStatus()
        {
            bool active = _jobs.Any(j => j.IsActive);
            _sleepGlyph.Text = active ? "" : "";
            _sleepGlyph.Foreground = active ? new SolidColorBrush(Orange) : _sleepText.Foreground;
            _sleepText.Text = active ? "PC bleibt wach" : "Energiesparmodus wie gewohnt";
        }

        // --- Auswahl ---------------------------------------------------------------------------

        void Select(string kind, string id)
        {
            _selKind = kind;
            _selId = id;
            if (kind == "disk")
            {
                var d = FindDisk(id);
                if (d != null)
                {
                    if (d.IsSsd == true) _mode = WipeMode.ByKey("ssd");
                    else if (_mode.Key == "ssd" || _mode.Key == "purge")
                        _mode = WipeMode.ByKey(AppSettings.Current.DefaultMode is "ssd" or "purge" ? "secure" : AppSettings.Current.DefaultMode) ?? WipeMode.ByKey("secure");
                    if (_view == "diag") EnsureHealth(d);
                }
            }
            _updatingLists = true;
            foreach (var list in new[] { _diskList, _intList, _jobList })
                list.SelectedItem = list.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == kind + ":" + id);
            _updatingLists = false;
        }

        void OnListSelection(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingLists) return;
            var list = (ListBox)sender;
            if (list.SelectedItem is not ListBoxItem item) return;
            var parts = ((string)item.Tag).Split(':');
            Select(parts[0], parts[1]);
            ShowDetail();
        }

        // --- Detailbereich ---------------------------------------------------------------------

        void ShowDetail()
        {
            _detailUpdater = null;
            _modeUpdater = null;
            UIElement view = null;
            bool diskSelected = false;
            if (_selKind == "job")
            {
                var j = FindJob(_selId);
                if (j != null) view = BuildJobPage(j);
            }
            else if (_selKind == "disk")
            {
                var d = FindDisk(_selId);
                if (d != null)
                {
                    diskSelected = true;
                    view = _view == "diag" ? BuildDiagPage(d) : BuildDiskPage(d);
                }
            }
            _switchBar.Visibility = diskSelected ? Visibility.Visible : Visibility.Collapsed;
            view ??= BuildEmptyPage();
            _detailHost.Content = view;
        }

        UIElement BuildEmptyPage()
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var g = Glyph("", 56);
            g.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            sp.Children.Add(g);
            bool none = _disks.Count == 0;
            var title = Text(none ? "Kein Laufwerk angeschlossen" : "Kein Laufwerk ausgewählt", 22, FontWeights.SemiBold);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.Margin = new Thickness(0, 16, 0, 6);
            sp.Children.Add(title);
            var msg = Text(none
                ? "Schließe ein Laufwerk an. Es erscheint automatisch in der Seitenleiste."
                : "Wähle links ein Laufwerk aus.", 14, brushKey: "TextFillColorSecondaryBrush", wrap: true);
            msg.TextAlignment = TextAlignment.Center;
            msg.MaxWidth = 360;
            sp.Children.Add(msg);
            return sp;
        }

        UIElement Header(string glyph, Color color, string title, string subtitle)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(Tile(glyph, color, 64, 30));
            var texts = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(Text(title, 26, FontWeights.SemiBold));
            texts.Children.Add(Text(subtitle, 14, brushKey: "TextFillColorSecondaryBrush"));
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);
            return g;
        }

        static string DiskSubtitle(DiskInfo d) => $"{Fmt.Bytes(d.Size)} · {d.TypeLabel} · {d.Bus}{(d.IsInternal ? " · intern" : "")}";

        static Border Badge(string text, Color color)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
                Child = Text(text, 11, FontWeights.SemiBold),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ((TextBlock)b.Child).Foreground = new SolidColorBrush(color);
            return b;
        }

        UIElement NoteCard(string glyph, string text, Button button = null, Color? glyphColor = null)
        {
            var dp = new DockPanel();
            if (button != null)
            {
                button.VerticalAlignment = VerticalAlignment.Center;
                button.Margin = new Thickness(12, 0, 0, 0);
                DockPanel.SetDock(button, Dock.Right);
                dp.Children.Add(button);
            }
            var g = Glyph(glyph, 16, glyphColor);
            g.Margin = new Thickness(0, 0, 12, 0);
            g.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(g, Dock.Left);
            dp.Children.Add(g);
            dp.Children.Add(Text(text, 14, wrap: true));
            return Card(dp);
        }

        // Laufwerk: Löschen ---------------------------------------------------------------------

        UIElement BuildDiskPage(DiskInfo d)
        {
            var body = PageBody();
            body.Children.Add(Header(d.Glyph, d.IsProtected ? Colors.Gray : Blue, d.Name, DiskSubtitle(d)));
            UIElement bar = null;
            var job = ActiveJobFor(d);

            if (!d.CanWipe)
            {
                body.Children.Add(new Border { Height = 20 });
                var btn = NormalButton("Zur Diagnose");
                btn.Click += (s, e) => SetView("diag");
                body.Children.Add(NoteCard("", d.IsProtected
                    ? "Auf diesem Laufwerk läuft Windows. Es ist geschützt und kann nicht gelöscht werden – die Diagnose ist aber möglich."
                    : "Dieses Laufwerk ist schreibgeschützt und kann nicht gelöscht werden.", btn));
            }
            else if (job != null)
            {
                body.Children.Add(new Border { Height = 20 });
                var btn = NormalButton("Fortschritt anzeigen");
                btn.Click += (s, e) => { Select("job", job.Id.ToString()); ShowDetail(); };
                body.Children.Add(NoteCard("", $"Auf diesem Laufwerk läuft gerade: {job.Title}.", btn));
            }
            else
            {
                if (d.IsInternal)
                {
                    body.Children.Add(new Border { Height = 16 });
                    body.Children.Add(NoteCard("", "Internes Laufwerk. Prüfe genau, ob es wirklich gelöscht werden soll – WipeKit fragt vor dem Start noch einmal nach.", null, Orange));
                }
                // Methode wählen
                body.Children.Add(PageSection("Methode"));
                var modes = new StackPanel();
                var recommended = WipeMode.Recommended(d);
                if (!_mode.AvailableFor(d)) _mode = recommended;
                for (int i = 0; i < WipeMode.All.Length; i++)
                {
                    var m = WipeMode.All[i];
                    bool usable = m.AvailableFor(d);
                    if (i > 0) modes.Children.Add(Divider());
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.Children.Add(Tile(m.Glyph, m.Tint, 32, 15));
                    var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
                    titleRow.Children.Add(Text(m.Title, 14, FontWeights.SemiBold));
                    if (m.Key == recommended.Key) titleRow.Children.Add(Badge("Empfohlen", Blue));
                    if (m.ReadOnly) titleRow.Children.Add(Badge("Ändert nichts", Colors.Gray));
                    texts.Children.Add(titleRow);
                    texts.Children.Add(Text(usable ? m.Tagline : "Nur für direkt angeschlossene NVMe-SSDs", 12, brushKey: "TextFillColorSecondaryBrush"));
                    Grid.SetColumn(texts, 1);
                    g.Children.Add(texts);
                    var radio = new RadioButton
                    {
                        Content = g,
                        GroupName = "mode",
                        IsChecked = m.Key == _mode.Key,
                        IsEnabled = usable,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Opacity = usable ? 1 : 0.55,
                    };
                    var mode = m;
                    radio.Checked += (s, e) => { _mode = mode; _modeUpdater?.Invoke(); };
                    modes.Children.Add(radio);
                }
                body.Children.Add(Card(modes));

                // Details zur Methode
                var modeTitle = PageSection("");
                body.Children.Add(modeTitle);
                var explanation = Text("", 14, wrap: true);
                var best = ValueText("");
                var standard = ValueText("");
                var meterBars = new Border[5];
                var meter = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                for (int i = 0; i < 5; i++)
                {
                    meterBars[i] = new Border { Width = 14, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
                    meter.Children.Add(meterBars[i]);
                }
                var secLabel = Text("", 14);
                secLabel.Margin = new Thickness(8, 0, 0, 0);
                meter.Children.Add(secLabel);
                var duration = ValueText("");
                var note = Text("", 12, brushKey: "TextFillColorSecondaryBrush", wrap: true);
                note.Margin = new Thickness(2, 10, 0, 0);
                var hint = Text("", 12, wrap: true);
                hint.Foreground = new SolidColorBrush(Orange);
                hint.Margin = new Thickness(2, 6, 0, 0);

                var detailStack = new StackPanel();
                detailStack.Children.Add(explanation);
                detailStack.Children.Add(Divider());
                detailStack.Children.Add(Rows(InfoRow("Geeignet für", best), InfoRow("Sicherheit", meter),
                    InfoRow("Orientiert sich an", standard), InfoRow("Geschätzte Dauer", duration)));
                body.Children.Add(Card(detailStack));
                body.Children.Add(note);
                body.Children.Add(hint);

                var dp = new DockPanel();
                var action = DangerButton("Löschen …");
                var accent = AccentButton("Prüfen …");
                action.Click += (s, e) => ConfirmStart(d);
                accent.Click += (s, e) => ConfirmStart(d);
                DockPanel.SetDock(action, Dock.Right);
                DockPanel.SetDock(accent, Dock.Right);
                dp.Children.Add(action);
                dp.Children.Add(accent);
                var barText = Text("", 14, brushKey: "TextFillColorSecondaryBrush", verticalCenter: true);
                dp.Children.Add(barText);
                bar = dp;

                _modeUpdater = () =>
                {
                    var m = _mode;
                    modeTitle.Text = $"Über »{m.Title}«";
                    explanation.Text = m.Explanation;
                    best.Text = m.BestFor;
                    standard.Text = m.Standard;
                    for (int i = 0; i < 5; i++)
                        meterBars[i].Background = i < m.SecurityLevel
                            ? new SolidColorBrush(m.Tint)
                            : new SolidColorBrush(Color.FromArgb(50, 128, 128, 128));
                    secLabel.Text = m.SecurityLabel;
                    double speed = d.IsSsd == true ? 300e6 : 130e6;
                    double units = m.WorkUnits + (m.Verify == VerifyKind.Samples ? 0.02 : 0);
                    duration.Text = m.Purge ? "Meist Sekunden bis wenige Minuten – je nach SSD"
                        : "ca. " + Fmt.ShortDuration(TimeSpan.FromSeconds(d.Size * units / speed));
                    note.Text = m.SecurityNote + (m.ReadOnly ? "" : "\n" + WipeMode.NotCertified);
                    string h = null;
                    if (m.ReadOnly || m.Purge) h = null;
                    else if (d.IsSsd == true && m.Key != "ssd") h = "Dieses Laufwerk ist eine SSD – dafür sind »SSD« oder »Firmware-Löschung« besser geeignet.";
                    else if (d.IsSsd == false && m.Key == "ssd") h = "Dieses Laufwerk ist eine Festplatte – TRIM wird hier nicht unterstützt. »Sicher« ist die bessere Wahl.";
                    else if (d.IsSsd == null) h = "Der Laufwerkstyp wird vom USB-Adapter nicht gemeldet. Handelt es sich um eine SSD, wähle den Modus »SSD«.";
                    hint.Text = h == null ? "" : "⚠ " + h;
                    hint.Visibility = h == null ? Visibility.Collapsed : Visibility.Visible;
                    action.Visibility = m.ReadOnly ? Visibility.Collapsed : Visibility.Visible;
                    accent.Visibility = m.ReadOnly ? Visibility.Visible : Visibility.Collapsed;
                    barText.Text = m.ReadOnly ? "Es werden keine Daten verändert." : $"Alle Daten auf »{d.Name}« werden unwiderruflich gelöscht.";
                };
                _modeUpdater();
            }

            body.Children.Add(PageSection("Informationen"));
            body.Children.Add(Card(Rows(
                InfoRow("Gerät", ValueText($"{d.Id} ({d.DevicePath})")),
                InfoRow("Kapazität", ValueText($"{Fmt.Bytes(d.Size)} ({Fmt.ExactBytes(d.Size)})")),
                InfoRow("Anschluss", ValueText(d.Bus + (d.IsInternal ? " (intern)" : ""))),
                InfoRow("Typ", ValueText(d.TypeLabel)),
                InfoRow("Seriennummer", ValueText(string.IsNullOrEmpty(d.Serial) ? (HealthFor(d)?.Health?.Serial ?? "–") : d.Serial)),
                InfoRow("Laufwerke", ValueText(d.Volumes.Count > 0 ? string.Join(", ", d.Volumes) : "Keine")))));
            return PageWithBar(body, bar);
        }

        void ConfirmStart(DiskInfo d)
        {
            var m = _mode;
            if (m.ReadOnly)
            {
                var a = MessageBox.Show(this, $"WipeKit liest alle Sektoren von »{d.Name}« ({d.Id}, {Fmt.Bytes(d.Size)}) und prüft, ob das Laufwerk leer ist. Es werden keine Daten verändert.",
                    "Laufwerk prüfen?", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
                if (a == MessageBoxResult.OK) StartJob(d, m, null, false);
                return;
            }
            string text = $"Alle Daten auf »{d.Name}« ({d.Id}, {Fmt.Bytes(d.Size)}) werden mit der Methode »{m.Title}« " +
                          "unwiderruflich gelöscht.\n\nDieser Vorgang kann nicht rückgängig gemacht werden.";
            if (m.Purge) text += "\n\nDie SSD löscht sich dabei selbst. Den PC währenddessen nicht ausschalten.";
            if (d.IsInternal)
                text += $"\n\nACHTUNG: »{d.Name}« ist ein INTERNES Laufwerk dieses PCs. Seriennummer: {(string.IsNullOrEmpty(d.Serial) ? "nicht verfügbar" : d.Serial)}. Bitte genau prüfen.";
            text += "\n\nFortfahren?";
            var answer = MessageBox.Show(this, text, d.IsInternal ? "Internes Laufwerk löschen?" : "Laufwerk löschen?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            StartJob(d, m, null, d.IsInternal);
        }

        void StartJob(DiskInfo d, WipeMode m, string purpose, bool internalConfirmed)
        {
            if (ActiveJobFor(d) != null) return;
            var job = new WipeJob(d, m, purpose) { InternalConfirmed = internalConfirmed };
            _jobs.Insert(0, job);
            _lastStates[job.Id] = job.State;
            job.Start();
            Select("job", job.Id.ToString());
            RebuildJobList();
            RebuildDiskList();
            UpdateSleepStatus();
            ShowDetail();
        }

        // Laufwerk: Diagnose --------------------------------------------------------------------

        static Color RatingColor(Rating r)
        {
            var (a, b, c) = HealthParser.RatingRgb(r);
            return Color.FromRgb((byte)(a * 255), (byte)(b * 255), (byte)(c * 255));
        }

        static Border StatTile(string glyph, string caption, string value, Color? valueColor, string foot)
        {
            var sp = new StackPanel();
            var cap = new StackPanel { Orientation = Orientation.Horizontal };
            var g = Glyph(glyph, 12);
            g.Margin = new Thickness(0, 0, 6, 0);
            g.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            cap.Children.Add(g);
            cap.Children.Add(Text(caption, 12, FontWeights.SemiBold, "TextFillColorSecondaryBrush"));
            sp.Children.Add(cap);
            var v = Text(value, 28, FontWeights.SemiBold);
            v.Margin = new Thickness(0, 4, 0, 2);
            if (valueColor.HasValue) v.Foreground = new SolidColorBrush(valueColor.Value);
            sp.Children.Add(v);
            sp.Children.Add(Text(foot, 12, brushKey: "TextFillColorSecondaryBrush", wrap: true));
            var card = Card(sp);
            card.Margin = new Thickness(0, 0, 8, 0);
            return card;
        }

        UIElement BuildDiagPage(DiskInfo d)
        {
            var body = PageBody();
            body.Children.Add(Header(d.Glyph, d.IsProtected ? Colors.Gray : Blue, d.Name, DiskSubtitle(d)));
            var e = HealthFor(d);
            var h = e?.Health;
            if (h == null)
            {
                body.Children.Add(new Border { Height = 24 });
                var pr = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 0, 0, 10) };
                body.Children.Add(pr);
                body.Children.Add(Text("Gesundheitsdaten werden gelesen …", 14, brushKey: "TextFillColorSecondaryBrush"));
                if (e == null || !e.Loading) EnsureHealth(d);
                return PageWithBar(body, null);
            }

            // Übersicht
            var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 20, -8, 0) };
            tiles.Children.Add(StatTile("", "GESAMTZUSTAND", HealthParser.RatingText(h.Rating), RatingColor(h.Rating),
                h.Available ? (h.Reasons.FirstOrDefault() ?? "") : "keine Daten"));
            if (h.IsSsd == true)
                tiles.Children.Add(StatTile("", "RESTLEBENSDAUER", h.LifePercent.HasValue ? $"{h.LifePercent} %" : "–", null, "laut Laufwerk"));
            else
                tiles.Children.Add(StatTile("", "BETRIEBSSTUNDEN", HFmt.Count(h.PowerOnHours), null, "Stunden eingeschaltet"));
            tiles.Children.Add(StatTile("", "TEMPERATUR", HFmt.Temp(h.Temperature), null, "aktuell"));
            body.Children.Add(tiles);

            if (h.Available)
            {
                body.Children.Add(PageSection("Bewertung"));
                var reasons = new StackPanel();
                foreach (var r in h.Reasons)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                    var ic = Glyph(h.Rating == Rating.VeryGood || h.Rating == Rating.Good ? "" : "", 14, RatingColor(h.Rating));
                    ic.Margin = new Thickness(0, 0, 10, 0);
                    DockPanel.SetDock(ic, Dock.Left);
                    row.Children.Add(ic);
                    row.Children.Add(Text(r, 14, wrap: true));
                    reasons.Children.Add(row);
                }
                body.Children.Add(Card(reasons));

                body.Children.Add(PageSection("Kennwerte"));
                var kv = new List<UIElement>();
                foreach (var (k, v) in HFmt.KeyValues(h).Skip(1))
                {
                    var t = ValueText(v);
                    bool zero = v == "0" || v.StartsWith("–");
                    if (k.StartsWith("Unkorrigierbare") && !zero) { t.Foreground = new SolidColorBrush(Red); t.FontWeight = FontWeights.SemiBold; }
                    if (k.StartsWith("Ausstehende") && !zero) { t.Foreground = new SolidColorBrush(Orange); t.FontWeight = FontWeights.SemiBold; }
                    kv.Add(InfoRow(k, t));
                }
                body.Children.Add(Card(Rows(kv.ToArray())));
            }
            else
            {
                body.Children.Add(PageSection("Keine Gesundheitsdaten"));
                var sp = new StackPanel();
                sp.Children.Add(Text(h.Reason, 14, wrap: true));
                if (d.Bus.Equals("USB", StringComparison.OrdinalIgnoreCase))
                {
                    var t = Text("WipeKit hat alle bekannten USB-Adapter-Typen ausprobiert (SAT, ASMedia, JMicron, Realtek). Manche Gehäuse geben die Daten trotzdem nicht weiter. Abhilfe: das Laufwerk intern per SATA oder NVMe anschließen oder ein USB-Gehäuse mit ASMedia-, JMicron- oder Realtek-Chip verwenden. Der Oberflächentest funktioniert auch ohne diese Daten.",
                        13, brushKey: "TextFillColorSecondaryBrush", wrap: true);
                    t.Margin = new Thickness(0, 8, 0, 0);
                    sp.Children.Add(t);
                }
                body.Children.Add(Card(sp));
            }

            // Tests
            body.Children.Add(PageSection("Tests"));
            var tests = new List<UIElement>();
            bool busy = ActiveJobFor(d) != null || e.SelfTest != null;
            if (e.SelfTest != null)
            {
                var running = new DockPanel();
                var ab = NormalButton("Abbrechen");
                ab.Click += (s, ev) => AbortSelfTest(d);
                DockPanel.SetDock(ab, Dock.Right);
                running.Children.Add(ab);
                var pr = new ProgressBar { IsIndeterminate = h.SelfTestRemaining == null, Value = 100 - (h.SelfTestRemaining ?? 100), Maximum = 100, Width = 120, Height = 4, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(pr, Dock.Left);
                running.Children.Add(pr);
                running.Children.Add(Text($"{e.SelfTest} läuft im Laufwerk" + (h.SelfTestRemaining.HasValue ? $" – noch {h.SelfTestRemaining} %" : ""), 14, verticalCenter: true));
                tests.Add(running);
            }
            else if (e.SelfTestMessage != null)
                tests.Add(InfoRow("Letzter Selbsttest", ValueText(e.SelfTestMessage)));
            UIElement TestRow(string title, string sub, Action start)
            {
                var dp = new DockPanel();
                var b = NormalButton("Starten");
                b.IsEnabled = !busy;
                b.VerticalAlignment = VerticalAlignment.Center;
                b.Click += (s, ev) => start();
                DockPanel.SetDock(b, Dock.Right);
                dp.Children.Add(b);
                var tx = new StackPanel();
                tx.Children.Add(Text(title, 14, FontWeights.SemiBold));
                tx.Children.Add(Text(sub, 12, brushKey: "TextFillColorSecondaryBrush", wrap: true));
                dp.Children.Add(tx);
                return dp;
            }
            if (h.Available && h.SupportsSelfTests)
            {
                tests.Add(TestRow("Kurztest", $"Elektronik und Teile der Oberfläche · ca. {h.ShortMinutes ?? 2} Min.", () => StartSelfTest(d, "short")));
                tests.Add(TestRow("Langtest", "Gesamte Oberfläche durch das Laufwerk · ca. " + (h.LongMinutes.HasValue ? Fmt.ShortDuration(TimeSpan.FromMinutes(h.LongMinutes.Value)) : "1–2 Std."), () => StartSelfTest(d, "long")));
                if (h.SupportsConveyance) tests.Add(TestRow("Transporttest", "Transportschäden erkennen · ca. 2 Min.", () => StartSelfTest(d, "conveyance")));
            }
            tests.Add(TestRow("Oberflächentest", "WipeKit liest jeden Sektor und misst die Geschwindigkeit · ca. " +
                Fmt.ShortDuration(TimeSpan.FromSeconds(d.Size / (d.IsSsd == true ? 400e6 : 130e6))), () => StartJob(d, WipeMode.ByKey("check"), "scan", false)));
            body.Children.Add(Card(Rows(tests.ToArray())));
            var foot = Text("Keiner der Tests verändert Daten. Selbsttests führt das Laufwerk selbst aus.", 12, brushKey: "TextFillColorSecondaryBrush", wrap: true);
            foot.Margin = new Thickness(2, 8, 0, 0);
            body.Children.Add(foot);

            if (h.Available)
            {
                body.Children.Add(PageSection("Laufwerk"));
                body.Children.Add(Card(Rows(HFmt.DriveDetails(h).Select(x => (UIElement)InfoRow(x.Item1, ValueText(x.Item2))).ToArray())));

                if (h.Attributes.Count > 0)
                {
                    int warn = h.Attributes.Count(a => a.Status != "OK");
                    var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                    double[] widths = { 44, 0, 60, 100, 80, 110, 70 };
                    foreach (var w in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
                    string[] heads = { "ID", "Attribut", "Wert", "Schlechtester", "Grenzwert", "Rohwert", "Status" };
                    int rowI = 0;
                    void Cell(string s, int col, bool head = false, bool right = false, Color? color = null)
                    {
                        var t = Text(s, head ? 12 : 13, head ? FontWeights.SemiBold : FontWeights.Normal, head ? "TextFillColorSecondaryBrush" : null);
                        t.Margin = new Thickness(0, 3, 10, 3);
                        if (right) t.TextAlignment = TextAlignment.Right;
                        if (color.HasValue) { t.Foreground = new SolidColorBrush(color.Value); t.FontWeight = FontWeights.SemiBold; }
                        Grid.SetRow(t, rowI);
                        Grid.SetColumn(t, col);
                        grid.Children.Add(t);
                    }
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    for (int c = 0; c < heads.Length; c++) Cell(heads[c], c, true, c != 1 && c != 6);
                    foreach (var a in h.Attributes)
                    {
                        rowI++;
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        Cell(a.Id.ToString(), 0, right: true);
                        Cell(a.Name, 1);
                        Cell(a.Value.ToString(), 2, right: true);
                        Cell(a.Worst.ToString(), 3, right: true);
                        Cell(a.Thresh.ToString(), 4, right: true);
                        Cell(a.RawString, 5, right: true);
                        Cell(a.Status, 6, color: a.Status == "Fehler" ? Red : a.Status == "Warnung" ? Orange : (Color?)null);
                    }
                    var ex = new Expander
                    {
                        Header = warn == 0 ? $"S.M.A.R.T.-Attribute ({h.Attributes.Count}, alle unauffällig)" : $"S.M.A.R.T.-Attribute ({warn} auffällig)",
                        Content = grid,
                        IsExpanded = warn > 0,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                    body.Children.Add(PageSection("Details"));
                    body.Children.Add(Card(ex));
                }
                if (h.Nvme.Count > 0)
                {
                    var ex = new Expander
                    {
                        Header = "NVMe-Gesundheitswerte",
                        Content = Rows(HFmt.NvmeRows(h).Select(x => (UIElement)InfoRow(x.Item1, ValueText(x.Item2))).ToArray()),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                    if (h.Attributes.Count == 0) body.Children.Add(PageSection("Details"));
                    else body.Children.Add(new Border { Height = 8 });
                    body.Children.Add(Card(ex));
                }
                if (h.SelfTests.Count > 0)
                {
                    body.Children.Add(PageSection("Selbsttest-Protokoll des Laufwerks"));
                    body.Children.Add(Card(Rows(h.SelfTests.Take(8).Select(t => (UIElement)InfoRow(t.Kind,
                        ValueText(t.Result + (t.Hours.HasValue ? $" · Std. {HFmt.Count(t.Hours)}" : "")))).ToArray())));
                }
            }

            var dpBar = new DockPanel();
            var save = AccentButton("Bericht speichern");
            save.ToolTip = "Gesundheitsbericht als PDF und Text speichern (Strg+S)";
            save.Click += (s, ev) => SaveHealthReport(d);
            var upd = NormalButton("Aktualisieren");
            upd.Margin = new Thickness(0, 0, 8, 0);
            upd.IsEnabled = !e.Loading;
            upd.Click += (s, ev) => _ = ReadHealth(d);
            DockPanel.SetDock(save, Dock.Right);
            DockPanel.SetDock(upd, Dock.Right);
            dpBar.Children.Add(save);
            dpBar.Children.Add(upd);
            dpBar.Children.Add(Text(e.ReadAt.HasValue ? "Gelesen " + Fmt.Clock(e.ReadAt.Value) : "", 14, brushKey: "TextFillColorSecondaryBrush", verticalCenter: true));
            return PageWithBar(body, dpBar);
        }

        void StartSelfTest(DiskInfo d, string test)
        {
            var e = HealthFor(d);
            if (e == null) return;
            Task.Run(() => SmartService.SelfTest(d, e.DevType, test)).ContinueWith(t =>
            {
                var (ok, msg) = t.Result;
                if (!ok)
                {
                    MessageBox.Show(this, string.IsNullOrEmpty(msg) ? "Das Laufwerk bzw. der Adapter unterstützt diesen Test nicht." : msg,
                        "Selbsttest konnte nicht gestartet werden", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                e.SelfTest = test == "short" ? "Kurztest" : test == "long" ? "Langtest" : "Transporttest";
                e.SelfTestMessage = null;
                ShowDetail();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                timer.Tick += async (s, ev) =>
                {
                    var r = await Task.Run(() => SmartService.Read(d));
                    e.Health = r.Health;
                    e.Json = r.Json;
                    e.ReadAt = DateTime.Now;
                    if (r.Health.SelfTestRemaining == null || e.SelfTest == null)
                    {
                        timer.Stop();
                        if (e.SelfTest != null)
                        {
                            var last = r.Health.SelfTests.FirstOrDefault();
                            e.SelfTestMessage = last != null ? $"{last.Kind}: {last.Result}" : "abgeschlossen";
                            e.SelfTest = null;
                            if (!_smoke) MessageBox.Show(this, $"{d.Name}: {e.SelfTestMessage}", "Selbsttest beendet", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    RebuildDiskList();
                    if (_selKind == "disk" && _selId == d.Number.ToString()) ShowDetail();
                };
                timer.Start();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void AbortSelfTest(DiskInfo d)
        {
            var e = HealthFor(d);
            if (e == null) return;
            Task.Run(() => SmartService.SelfTest(d, e.DevType, "abort")).ContinueWith(t =>
            {
                e.SelfTest = null;
                e.SelfTestMessage = "Abgebrochen";
                _ = ReadHealth(d);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void SaveHealthReport(DiskInfo d)
        {
            var h = HealthFor(d)?.Health;
            try
            {
                var ident = Reports.Identity(h, d.Name, d.Size, d.Serial);
                var r = Reports.HealthReport(h, ident, AppVersion, OsLabel, Environment.MachineName, AppSettings.Current.ReportMeta);
                var (pdf, _) = Reports.Save(r);
                if (!_smoke) OpenFile(pdf);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Bericht konnte nicht gespeichert werden", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Vorgang -----------------------------------------------------------------------------------

        UIElement BuildJobPage(WipeJob j)
        {
            var body = PageBody();

            // Fortschrittsring
            var heroStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
            var ringGrid = new Grid { Width = 160, Height = 160 };
            var ring = new RingProgress { Thickness = 10 };
            ringGrid.Children.Add(ring);
            var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var pct = Text("", 30, FontWeights.SemiBold);
            pct.HorizontalAlignment = HorizontalAlignment.Center;
            var centerGlyph = Glyph("", 48);
            var stepText = Text("", 12, brushKey: "TextFillColorSecondaryBrush");
            stepText.HorizontalAlignment = HorizontalAlignment.Center;
            center.Children.Add(pct);
            center.Children.Add(centerGlyph);
            center.Children.Add(stepText);
            ringGrid.Children.Add(center);
            heroStack.Children.Add(ringGrid);
            var headline = Text("", 22, FontWeights.SemiBold);
            headline.HorizontalAlignment = HorizontalAlignment.Center;
            headline.Margin = new Thickness(0, 16, 0, 4);
            var subline = Text("", 14, brushKey: "TextFillColorSecondaryBrush", wrap: true);
            subline.TextAlignment = TextAlignment.Center;
            subline.MaxWidth = 560;
            heroStack.Children.Add(headline);
            heroStack.Children.Add(subline);
            body.Children.Add(Card(heroStack));

            SpeedCurve curve = null;
            TextBlock rBad = null, rData = null;
            if (j.IsCheck)
            {
                body.Children.Add(PageSection("Lesegeschwindigkeit"));
                curve = new SpeedCurve { Height = 160 };
                body.Children.Add(Card(curve));
                body.Children.Add(PageSection("Befund"));
                rBad = ValueText("–");
                rData = ValueText("–");
                body.Children.Add(Card(Rows(InfoRow("Unlesbare Bereiche", rBad), InfoRow("Daten gefunden", rData))));
            }

            UIElement bar;
            TextBlock rStep = null, rDone = null, rSpeed = null, rEta = null, rEnd = null, rElapsed = null, rSleep = null;
            Button cancel = null;
            if (j.IsActive)
            {
                body.Children.Add(PageSection("Fortschritt"));
                rStep = ValueText("–"); rDone = ValueText("–"); rSpeed = ValueText("–");
                rEta = ValueText("–"); rEnd = ValueText("–"); rElapsed = ValueText("–");
                var rows = new List<UIElement> { InfoRow("Schritt", rStep) };
                if (!j.Mode.Purge)
                {
                    rows.Add(InfoRow("Verarbeitet", rDone));
                    rows.Add(InfoRow("Geschwindigkeit", rSpeed));
                    rows.Add(InfoRow("Verbleibend", rEta));
                    rows.Add(InfoRow("Voraussichtlich fertig", rEnd));
                }
                rows.Add(InfoRow("Läuft seit", rElapsed));
                body.Children.Add(Card(Rows(rows.ToArray())));
                rSleep = ValueText("–");
                body.Children.Add(PageSection("Energie"));
                body.Children.Add(Card(InfoRow("Energiesparmodus", rSleep)));
                var foot = Text("Der Bildschirm darf ausgehen. Sobald der Vorgang beendet ist, verhält sich der PC wieder wie gewohnt.",
                    12, brushKey: "TextFillColorSecondaryBrush", wrap: true);
                foot.Margin = new Thickness(2, 8, 0, 0);
                body.Children.Add(foot);

                var dp = new DockPanel();
                cancel = NormalButton("Abbrechen …");
                cancel.IsEnabled = !j.Mode.Purge;
                if (j.Mode.Purge) cancel.ToolTip = "Eine laufende Firmware-Löschung kann nicht abgebrochen werden.";
                cancel.Click += (s, e) =>
                {
                    var a = MessageBox.Show(this, j.IsCheck ? "Es werden keine Daten verändert." : "Das Laufwerk ist danach nur teilweise überschrieben.",
                        $"{j.Title} abbrechen?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                    if (a == MessageBoxResult.Yes) { j.RequestCancel(); _detailUpdater?.Invoke(); }
                };
                DockPanel.SetDock(cancel, Dock.Right);
                dp.Children.Add(cancel);
                dp.Children.Add(Text("Bitte WipeKit geöffnet lassen, bis der Vorgang abgeschlossen ist.", 14,
                    brushKey: "TextFillColorSecondaryBrush", verticalCenter: true));
                bar = dp;
            }
            else
            {
                body.Children.Add(PageSection("Ergebnis"));
                var rows = new List<UIElement>();
                var msg = Text(j.Message ?? "–", 14, wrap: true);
                if (j.State == JobState.Failed) msg.Foreground = new SolidColorBrush(Red);
                rows.Add(msg);
                if (j.Elapsed.HasValue) rows.Add(InfoRow("Dauer", ValueText(Fmt.Duration(j.Elapsed.Value))));
                if (!j.IsCheck)
                {
                    if (j.Mode.Verify == VerifyKind.Full)
                        rows.Add(InfoRow("Prüfung", ValueText(j.Verified == true ? "Bestanden" : j.Verified == false ? "Nicht bestanden" : "Nicht durchgeführt")));
                    if (j.SampleResult != null) rows.Add(InfoRow(j.Mode.Purge ? "Prüfmarken" : "Stichproben", ValueText(j.SampleResult)));
                    if (j.PurgeMethod != null) rows.Add(InfoRow("Firmware-Befehl", ValueText(j.PurgeMethod)));
                    if (j.Mode.Trims) rows.Add(InfoRow("TRIM", ValueText(j.TrimResult ?? "Nicht durchgeführt")));
                }
                if (j.FinishedAt.HasValue) rows.Add(InfoRow("Beendet", ValueText(Fmt.DateTimeText(j.FinishedAt.Value))));
                body.Children.Add(Card(Rows(rows.ToArray())));

                if (j.ProtocolPath != null)
                {
                    body.Children.Add(PageSection("Protokolle"));
                    var prow = new List<UIElement>();
                    void ReportRow(string title, string pdf, string txt)
                    {
                        if (pdf == null) return;
                        var dpp = new DockPanel();
                        var bp = NormalButton("PDF öffnen");
                        bp.Click += (s, e) => OpenFile(pdf);
                        var bt = NormalButton("Text");
                        bt.Margin = new Thickness(8, 0, 0, 0);
                        bt.MinWidth = 70;
                        bt.Click += (s, e) => OpenFile(txt);
                        var bf = IconButton("", "Im Explorer zeigen");
                        bf.Margin = new Thickness(8, 0, 0, 0);
                        bf.Click += (s, e) => OpenExplorerSelect(pdf);
                        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                        btns.Children.Add(bp);
                        btns.Children.Add(bt);
                        btns.Children.Add(bf);
                        DockPanel.SetDock(btns, Dock.Right);
                        dpp.Children.Add(btns);
                        var tx = new StackPanel();
                        tx.Children.Add(Text(title, 14, FontWeights.SemiBold));
                        tx.Children.Add(Text(Path.GetFileNameWithoutExtension(pdf), 12, brushKey: "TextFillColorSecondaryBrush"));
                        dpp.Children.Add(tx);
                        prow.Add(dpp);
                    }
                    j.Reports.TryGetValue("title", out var ptitle);
                    ReportRow(ptitle ?? "Protokoll", j.ProtocolPath, j.Reports.TryGetValue("txt", out var t1) ? t1 : null);
                    if (j.Reports.TryGetValue("pdf_full", out var pf))
                        ReportRow("Ausführliches Protokoll", pf, j.Reports.TryGetValue("txt_full", out var t2) ? t2 : null);
                    body.Children.Add(Card(Rows(prow.ToArray())));
                    var where = Text($"Gespeichert in {Reports.Folder} (Unterordner »PDF« und »Text«).", 12, brushKey: "TextFillColorSecondaryBrush", wrap: true);
                    where.Margin = new Thickness(2, 8, 0, 0);
                    body.Children.Add(where);
                }

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var remove = NormalButton("Entfernen");
                remove.Click += (s, e) => RemoveJob(j);
                buttons.Children.Add(remove);
                if (j.ProtocolPath != null)
                {
                    var open = AccentButton("Protokoll öffnen");
                    open.Margin = new Thickness(8, 0, 0, 0);
                    open.Click += (s, e) => OpenFile(j.ProtocolPath);
                    buttons.Children.Add(open);
                }
                bar = buttons;
            }

            body.Children.Add(PageSection("Laufwerk"));
            body.Children.Add(Card(Rows(
                InfoRow("Name", ValueText(j.Disk.Name)),
                InfoRow("Gerät", ValueText(j.Disk.Id)),
                InfoRow("Kapazität", ValueText(Fmt.Bytes(j.Disk.Size))),
                InfoRow("Vorgang", ValueText($"{(j.IsCheck ? j.Title : j.Mode.Title)} · {j.Mode.Tagline}")))));

            _detailUpdater = () =>
            {
                var state = j.State;
                ring.RingColor = j.FoundProblem ? Red : state == JobState.Done ? Green : state == JobState.Failed ? Red : state == JobState.Cancelled ? Orange : Blue;
                ring.Value = state == JobState.Done ? 1 : j.Fraction;
                bool active = j.IsActive;
                pct.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                centerGlyph.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
                pct.Text = Fmt.Percent(j.Fraction);
                stepText.Text = state == JobState.Running && j.Mode.StepCount > 1 ? $"Schritt {j.Step} von {j.Mode.StepCount}" : "";
                centerGlyph.Text = j.FoundProblem ? "" : state == JobState.Done ? "" : state == JobState.Failed ? "" : "";
                centerGlyph.Foreground = new SolidColorBrush(ring.RingColor);
                string t = j.Title;
                headline.Text = state switch
                {
                    JobState.Starting => "Wird vorbereitet",
                    JobState.Running => j.IsCheck ? $"{t} läuft" : "Wird gelöscht",
                    JobState.Done => j.IsCheck ? $"{t} abgeschlossen" : "Löschen abgeschlossen",
                    JobState.Failed => $"{t} fehlgeschlagen",
                    _ => $"{t} abgebrochen",
                };
                subline.Text = state == JobState.Done ? (j.IsCheck ? j.Message : $"»{j.Disk.Name}« wurde sicher gelöscht.") : active ? j.Phase : j.Disk.Name;

                if (curve != null)
                {
                    curve.Update(j.Curve, SpeedCurve.TotalBuckets(j.DiskSize > 0 ? j.DiskSize : j.Disk.Size));
                    int bad = j.BadBlocks ?? 0;
                    rBad.Text = bad == 0 ? "keine" : $"{bad} (je 64 KiB)";
                    rBad.Foreground = bad == 0 ? rData.Foreground : new SolidColorBrush(Red);
                    var nz = j.NonZeroBytes;
                    rData.Text = nz == null ? "–" : nz == 0 ? "keine – nur Nullen" : $"{Fmt.Bytes(nz.Value)} enthalten Daten";
                }

                if (rStep != null)
                {
                    if (state == JobState.Running)
                    {
                        rStep.Text = j.Mode.StepCount > 1 ? $"{j.Step} von {j.Mode.StepCount} · {j.Phase}" : j.Phase;
                        rDone.Text = $"{Fmt.Bytes(j.BytesDone)} von {Fmt.Bytes(j.BytesTotal)}";
                        rSpeed.Text = j.Speed > 0 ? Fmt.Speed(j.Speed) : "–";
                        var eta = j.Eta;
                        rEta.Text = eta.HasValue ? "ca. " + Fmt.Duration(eta.Value) : "Wird berechnet …";
                        rEnd.Text = eta.HasValue ? Fmt.Clock(DateTime.Now + eta.Value) : "–";
                        rElapsed.Text = j.Elapsed.HasValue ? Fmt.Duration(j.Elapsed.Value) : "–";
                    }
                    else
                    {
                        rStep.Text = j.Phase;
                    }
                    rSleep.Text = j.SleepPrevented ? "Verhindert – Bildschirm darf ausgehen" : "Wird vorbereitet …";
                    if (j.CancelRequested)
                    {
                        cancel.Content = "Wird abgebrochen …";
                        cancel.IsEnabled = false;
                    }
                }
            };
            _detailUpdater();
            return PageWithBar(body, bar);
        }

        static void OpenFile(string path)
        {
            try { if (path != null) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        static void OpenExplorerSelect(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }

        // --- Zyklus --------------------------------------------------------------------------------

        void Tick()
        {
            bool changed = false;
            foreach (var j in _jobs.ToList())
            {
                var state = j.State;
                if (!_lastStates.TryGetValue(j.Id, out var last) || last != state)
                {
                    _lastStates[j.Id] = state;
                    changed = true;
                    if (!j.IsActive && !j.Reported) JobFinished(j);
                }
            }
            if (changed)
            {
                RebuildJobList();
                RebuildDiskList();
                UpdateSleepStatus();
                if (_selKind == "job") ShowDetail();
            }
            else
            {
                UpdateJobRows();
                _detailUpdater?.Invoke();
            }
        }

        void RemoveJob(WipeJob j)
        {
            if (j.IsActive) return;
            _jobs.Remove(j);
            _lastStates.Remove(j.Id);
            if (_selKind == "job" && _selId == j.Id.ToString())
            {
                _selKind = null;
                _selId = null;
                var first = _disks.FirstOrDefault(d => d.CanWipe && ActiveJobFor(d) == null) ?? _disks.FirstOrDefault();
                if (first != null) Select("disk", first.Number.ToString());
            }
            RebuildJobList();
            RebuildDiskList();
            UpdateSleepStatus();
            ShowDetail();
        }

        void JobFinished(WipeJob j)
        {
            j.Reported = true;
            if (!string.IsNullOrEmpty(j.HealthAfter))
            {
                var hh = HealthParser.ParseText(j.HealthAfter);
                if (hh.Available)
                {
                    if (!_health.TryGetValue(j.Disk.Number, out var e)) _health[j.Disk.Number] = e = new HealthEntry();
                    e.Health = hh;
                    e.Json = j.HealthAfter;
                    e.ReadAt = DateTime.Now;
                }
            }
            j.Reports = WriteReports(j);
            var d = j.Disk;
            string title, text;
            MessageBoxImage image;
            if (j.State == JobState.Done)
            {
                title = j.IsCheck ? $"{j.Title} abgeschlossen" : "Löschen abgeschlossen";
                text = j.IsCheck ? $"{d.Name}: {j.Message}" : $"{d.Name} ({d.Id}, {Fmt.Bytes(d.Size)}) wurde erfolgreich gelöscht.";
                if (j.Elapsed.HasValue) text += "\nDauer: " + Fmt.Duration(j.Elapsed.Value);
                if (!j.IsCheck && j.Verified == true)
                    text += j.Mode.Verify == VerifyKind.Full ? "\nPrüfung: bestanden – jeder Sektor ist überschrieben." : "\nKontrolle: " + j.SampleResult;
                if (j.Mode.Trims && j.TrimResult != null) text += "\nTRIM: " + j.TrimResult;
                image = j.FoundProblem ? MessageBoxImage.Warning : MessageBoxImage.Information;
            }
            else if (j.State == JobState.Cancelled)
            {
                title = $"{j.Title} abgebrochen";
                text = $"{d.Name} ({d.Id}): {j.Message}";
                image = MessageBoxImage.Warning;
            }
            else
            {
                title = $"{j.Title} fehlgeschlagen";
                text = $"{d.Name} ({d.Id}): {j.Message ?? "Unbekannter Fehler"}";
                image = MessageBoxImage.Error;
            }
            if (_smoke) return;
            var settings = AppSettings.Current;
            var before = string.IsNullOrEmpty(j.HealthBefore) ? null : HealthParser.ParseText(j.HealthBefore);
            var after = string.IsNullOrEmpty(j.HealthAfter) ? null : HealthParser.ParseText(j.HealthAfter);
            var health = after != null && after.Available ? after : before;
            bool warning = health != null && health.Available && (health.Rating == Rating.Bad || health.Rating == Rating.Caution);
            string notice = Notice(j, health, warning);
            if (settings.PlaySound)
            {
                if (j.State == JobState.Done && !j.FoundProblem && !warning) SystemSounds.Asterisk.Play(); else SystemSounds.Hand.Play();
            }
            if (settings.NotifyLocal) Notifier.Show(title, notice, j.State != JobState.Done || warning);
            if (settings.Push.Service != "off")
            {
                var ev = new PushEvent
                {
                    Kind = j.IsCheck ? "check" : "wipe",
                    State = j.State == JobState.Done ? "done" : j.State == JobState.Cancelled ? "cancelled" : "failed",
                    Warning = warning, Notice = notice, Meta = HubMeta(j, health),
                };
                foreach (var key in new[] { "pdf", "pdf_full" })
                    if (j.Reports != null && j.Reports.TryGetValue(key, out var path) && File.Exists(path))
                        ev.Files.Add((Path.GetFileName(path), File.ReadAllBytes(path)));
                var cfg = settings.Push;
                System.Threading.Tasks.Task.Run(() =>
                {
                    string err = Push.Send(cfg, ev);
                    if (err != null) Dispatcher.BeginInvoke(new Action(() => Notifier.Show("Benachrichtigung aufs Handy fehlgeschlagen", err, true)));
                });
            }
            Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(this, text, title, MessageBoxButton.OK, image)));
        }

        /// <summary>Kurze Mitteilung (für Handy und Info-Center)</summary>
        static string Notice(WipeJob j, Health health, bool warning)
        {
            var d = j.Disk;
            var ident = Reports.Identity(health, d.Name, d.Size, d.Serial);
            string label = string.Join(" ", new[] { ident.Vendor == "Unbekannt" ? "" : ident.Vendor, ident.Model }.Where(x => x != ""));
            string what = $"{(label == "" ? d.Name : label)} ({Fmt.Bytes(d.Size)})";
            string body;
            if (j.State == JobState.Done)
            {
                if (j.IsCheck)
                    body = j.BadBlocks > 0 ? $"{what}: {j.BadBlocks} unlesbare Bereiche gefunden"
                         : $"{what}: " + (j.NonZeroBytes == 0 ? "Laufwerk ist leer" : (j.Title == "Oberflächentest" ? "Oberflächentest bestanden" : "Laufwerk enthält Daten"));
                else body = $"{what}: erfolgreich gelöscht";
            }
            else if (j.State == JobState.Cancelled) body = $"{what}: {j.Title} abgebrochen";
            else
            {
                string m = j.Message ?? "";
                body = $"{what}: {j.Title} fehlgeschlagen" + (m == "" ? "" : " – " + (m.Length > 120 ? m.Substring(0, 120) : m));
            }
            if (warning) body += " · Gesundheit: " + HealthParser.RatingText(health.Rating);
            return body;
        }

        static Dictionary<string, object> HubMeta(WipeJob j, Health health)
        {
            var d = j.Disk;
            var ident = Reports.Identity(health, d.Name, d.Size, d.Serial);
            string type = "unknown";
            if (health != null && health.Available)
                type = health.Protocol == "NVMe" ? "nvme" : health.IsSsd == true ? "ssd" : health.IsSsd == false ? "hdd" : "unknown";
            else if (d.IsSsd == true) type = "ssd";
            else if (d.IsSsd == false) type = "hdd";
            if (type == "unknown" && d.Bus == "USB" && d.Size < 600_000_000_000) type = "usb";
            var started = j.StartedAt;
            var ended = j.FinishedAt ?? DateTime.Now;
            var m = new Dictionary<string, object>
            {
                ["vendor"] = ident.Vendor, ["model"] = ident.Model, ["serial"] = ident.Serial, ["size"] = d.Size, ["type"] = type,
                ["mode"] = j.IsCheck ? j.Title : j.Mode.Title, ["method"] = j.Mode.MethodDescription,
                ["result"] = j.State == JobState.Done ? (j.IsCheck ? "Abgeschlossen" : "Erfolgreich gelöscht")
                           : j.State == JobState.Cancelled ? "Abgebrochen" : "Fehlgeschlagen",
                ["rating"] = health != null ? HealthParser.RatingText(health.Rating) : "Unbekannt",
                ["started"] = started.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["ended"] = ended.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["duration"] = (int)Math.Max(0, (ended - started).TotalSeconds),
                ["host"] = Environment.MachineName, ["platform"] = OsLabel, ["app"] = AppVersion,
            };
            if (j.State != JobState.Done && !string.IsNullOrEmpty(j.Message)) m["message"] = j.Message;
            return m;
        }

        Dictionary<string, string> WriteReports(WipeJob j)
        {
            var result = new Dictionary<string, string>();
            try
            {
                var d = j.Disk;
                var before = string.IsNullOrEmpty(j.HealthBefore) ? null : HealthParser.ParseText(j.HealthBefore);
                var after = string.IsNullOrEmpty(j.HealthAfter) ? null : HealthParser.ParseText(j.HealthAfter);
                string verification = "nicht vorgesehen";
                if (j.Mode.Verify == VerifyKind.Full)
                    verification = j.Verified == true
                        ? (j.Mode.Passes[j.Mode.Passes.Length - 1] == Pattern.Seeded
                            ? "bestanden – alle Sektoren gelesen und mit den geschriebenen Zufallsdaten verglichen"
                            : "bestanden – alle Sektoren gelesen, alle enthalten Nullen")
                        : j.Verified == false ? "NICHT bestanden" : "nicht durchgeführt";
                else if (j.Mode.Verify == VerifyKind.Samples)
                    verification = j.SampleResult ?? "nicht durchgeführt";
                var w = new WipeRecord
                {
                    ModeTitle = j.IsCheck ? j.Title : j.Mode.Title, Method = j.Mode.MethodDescription, Standard = j.Mode.Standard,
                    State = j.State, Started = j.StartedAt, Ended = j.FinishedAt ?? DateTime.Now,
                    BytesWritten = j.Mode.Purge ? 0 : j.BytesDone, Message = j.Message ?? "", Verification = verification,
                    Trim = j.Mode.Trims ? (j.TrimResult ?? "nicht durchgeführt") : "",
                    Device = $"{d.Id} ({d.DevicePath})", Bus = d.Bus + (d.IsInternal ? " (intern)" : ""), TypeLabel = d.TypeLabel,
                    Volumes = d.Volumes, ExactSize = j.DiskSize > 0 ? j.DiskSize : d.Size,
                    Host = Environment.MachineName, Platform = OsLabel, AppVersion = AppVersion,
                    ReadOnly = j.IsCheck, BadBlocks = j.IsCheck ? j.BadBlocks : null, NonZero = j.IsCheck ? j.NonZeroBytes : null,
                    PurgeMethod = j.PurgeMethod ?? "", Meta = AppSettings.Current.ReportMeta,
                };
                var ident = Reports.Identity(before ?? after, d.Name, d.Size, d.Serial);
                if (j.IsCheck)
                {
                    var (pdf, txt) = Reports.Save(Reports.WipeReport(w, ident, before, after, true));
                    result["pdf"] = pdf; result["txt"] = txt; result["title"] = "Prüfprotokoll";
                }
                else
                {
                    var (pdf, txt) = Reports.Save(Reports.WipeReport(w, ident, before, after, false));
                    result["pdf"] = pdf; result["txt"] = txt; result["title"] = "Löschprotokoll";
                    if (AppSettings.Current.DetailedReport)
                    {
                        var (pdf2, txt2) = Reports.Save(Reports.WipeReport(w, ident, before, after, true));
                        result["pdf_full"] = pdf2; result["txt_full"] = txt2;
                    }
                }
            }
            catch (Exception ex)
            {
                result["error"] = ex.Message;
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "WipeKit-Fehler.log"), $"{DateTime.Now:u} Bericht: {ex}\n"); } catch { }
            }
            return result;
        }

        // --- Schließen --------------------------------------------------------------------------------

        void OnClosing(object sender, CancelEventArgs e)
        {
            var active = _jobs.Where(j => j.IsActive).ToList();
            if (active.Count == 0 || _closingConfirmed || _smoke) return;
            string text = (active.Count == 1 ? "Ein Vorgang läuft noch." : $"{active.Count} Vorgänge laufen noch.") +
                          "\n\nBeim Beenden wird der Vorgang abgebrochen; ein Löschvorgang hinterlässt das Laufwerk nur teilweise überschrieben. Trotzdem beenden?";
            var a = MessageBox.Show(this, text, "WipeKit beenden?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (a != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _closingConfirmed = true;
            Mouse.OverrideCursor = Cursors.Wait;
            foreach (var j in active) j.RequestCancel();
            foreach (var j in active) j.Join(TimeSpan.FromSeconds(20));   // gibt das Laufwerk wieder frei
            Mouse.OverrideCursor = null;
        }

        // --- Automatische Prüfung der Oberfläche (GitHub-Build) -------------------------------------

        void SaveShot(string name)
        {
            string dir = Environment.GetEnvironmentVariable("WIPEKIT_SHOTS");
            if (string.IsNullOrEmpty(dir) || Content is not FrameworkElement root) return;
            Directory.CreateDirectory(dir);
            root.UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int w = (int)Math.Ceiling(root.ActualWidth * scale), h = (int)Math.Ceiling(root.ActualHeight * scale);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var bg = TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
                dc.DrawRectangle(bg, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            var bmp = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(System.IO.Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }

        void RunSmokeTest()
        {
            try
            {
                string fx = Environment.GetEnvironmentVariable("WIPEKIT_FIXTURES") ?? "tests\\fixtures";
                string Fixture(string n) { try { return File.ReadAllText(System.IO.Path.Combine(fx, $"smartctl-{n}.json")); } catch { return ""; } }
                var disks = new List<DiskInfo>
                {
                    new DiskInfo { Number = 3, Name = "WDC WD5000AAKX-00ERMA0", Size = 500_107_862_016, Bus = "USB", IsSsd = false, Volumes = new List<string> { "Backup (E:)" }, Serial = "WD-WCC2EXX12345" },
                    new DiskInfo { Number = 4, Name = "Samsung SSD 970 EVO Plus 1TB", Size = 1_000_204_886_016, Bus = "NVMe", IsSsd = true, IsInternal = true, Serial = "S4EWNX0R123456A" },
                    new DiskInfo { Number = 5, Name = "USB-Stick", Size = 64_000_000_000, Bus = "USB", IsSsd = null },
                    new DiskInfo { Number = 0, Name = "Crucial CT500MX500SSD1", Size = 500_107_862_016, Bus = "SATA", IsSsd = true, IsProtected = true, IsInternal = true },
                };
                foreach (var (n, f) in new[] { (3, "hdd"), (4, "nvme"), (5, "usb-unsupported"), (0, "ssd") })
                {
                    string json = Fixture(f);
                    _health[n] = new HealthEntry { Health = HealthParser.ParseText(json), Json = json, ReadAt = DateTime.Now };
                }
                if (_health[3].Health.Rating != Rating.Good) throw new Exception("Bewertung HDD: " + _health[3].Health.Rating);
                if (_health[4].Health.LifePercent != 96) throw new Exception("Restlebensdauer NVMe");
                ApplyDisks(disks, true);
                foreach (var view in new[] { "wipe", "diag" })
                {
                    SetView(view);
                    foreach (var d in disks)
                    {
                        Select("disk", d.Number.ToString());
                        ShowDetail();
                        if (view == "wipe")
                            foreach (var m in WipeMode.All.Where(x => x.AvailableFor(d)))
                            {
                                _mode = m;
                                _modeUpdater?.Invoke();
                            }
                    }
                }
                SetView("wipe");
                Select("disk", "3");
                _mode = WipeMode.ByKey("secure");
                ShowDetail();
                SaveShot("windows-1-loeschen");
                SetView("diag");
                Select("disk", "3");
                ShowDetail();
                SaveShot("windows-2-diagnose");
                Select("disk", "4");
                ShowDetail();
                SaveShot("windows-3-diagnose-ssd");
                Select("disk", "5");
                ShowDetail();
                SaveShot("windows-4-usb-hinweis");
                SetView("wipe");
                Select("disk", "4");
                _mode = WipeMode.ByKey("purge");
                ShowDetail();
                SaveShot("windows-5-firmware-loeschung");

                string hdd = Fixture("hdd");
                foreach (var (key, state, purpose) in new[] { ("ssd", JobState.Running, (string)null), ("secure", JobState.Done, null), ("quick", JobState.Failed, null),
                                                              ("check", JobState.Done, "scan"), ("check", JobState.Running, "scan"), ("purge", JobState.Done, null), ("maximum", JobState.Cancelled, null) })
                {
                    var job = new WipeJob(disks[0], WipeMode.ByKey(key), purpose);
                    string message = state == JobState.Failed ? "Schreibfehler bei 212 GB von 500 GB: Das Gerät ist nicht bereit. Laufwerk getrennt oder defekt?"
                        : state == JobState.Cancelled ? "Vom Benutzer abgebrochen. Das Laufwerk ist nur teilweise überschrieben."
                        : key == "check" ? "Alle Sektoren sind lesbar. Das Laufwerk ist leer (nur Nullen)."
                        : key == "purge" ? "Die SSD hat sich selbst gelöscht (Sanitize – Crypto Erase). Die Kontrolle der Prüfmarken war erfolgreich."
                        : "Alle Sektoren wurden überschrieben und die Prüfung war erfolgreich.";
                    job.SetTestState(state, 0.42, message, hdd);
                    _jobs.Insert(0, job);
                    Select("job", job.Id.ToString());
                    Tick();
                    RebuildJobList();
                    ShowDetail();
                    _detailUpdater?.Invoke();
                    if (!job.IsActive && job.ProtocolPath == null)
                        throw new Exception($"Kein Protokoll für {key}/{state}: {string.Join(", ", job.Reports.Select(kv => kv.Key + "=" + kv.Value))}");
                    if (key == "ssd" && state == JobState.Running) { UpdateSleepStatus(); SaveShot("windows-6-fortschritt"); }
                    if (key == "secure" && state == JobState.Done) SaveShot("windows-7-fertig");
                    if (key == "check" && state == JobState.Done) SaveShot("windows-8-oberflaechentest");
                }

                // Gesundheitsberichte für alle Beispieldaten
                foreach (var d in disks)
                {
                    var h = _health[d.Number].Health;
                    var (pdf, txt) = Reports.Save(Reports.HealthReport(h, Reports.Identity(h, d.Name, d.Size, d.Serial), AppVersion, OsLabel, "CI"));
                    var bytes = File.ReadAllBytes(pdf);
                    if (bytes.Length < 1000 || bytes[0] != '%' || bytes[1] != 'P') throw new Exception("PDF ungültig: " + pdf);
                    if (!File.ReadAllText(txt).Contains("Gesamtzustand")) throw new Exception("Text ungültig: " + txt);
                }
                string name = Reports.BaseName(Reports.Identity(_health[3].Health, "", 0, ""), new DateTime(2026, 11, 12, 3, 3, 0));
                if (name != "2026-11-12_03-03_WESTERN-DIGITAL-WD5000AAKX-00ERMA0-500GB-WD-WCC2EXX12345")
                    throw new Exception("Dateiname: " + name);
                UpdateSleepStatus();
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WipeKit-Smoke.log"), ex.ToString()); } catch { }
                Application.Current.Shutdown(1);
            }
        }
    }
}
