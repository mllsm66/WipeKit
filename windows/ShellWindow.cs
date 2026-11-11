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
    /// <summary>Hauptfenster: Seitenleiste mit Laufwerken und Löschvorgängen, rechts die Details.</summary>
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
        string _selKind;     // "disk" | "job"
        string _selId;
        WipeMode _mode = WipeMode.ByKey("secure");
        string _diskSignature = "";
        bool _updatingLists;
        bool _refreshing;
        bool _closingConfirmed;

        readonly ListBox _diskList = new ListBox();
        readonly ListBox _jobList = new ListBox();
        readonly TextBlock _noDisks;
        readonly TextBlock _jobsHeader;
        readonly TextBlock _sleepGlyph;
        readonly TextBlock _sleepText;
        readonly ContentControl _detailHost = new ContentControl();
        readonly DispatcherTimer _timer;
        readonly DispatcherTimer _refreshDebounce;
        readonly Dictionary<Guid, (RingProgress Ring, TextBlock Glyph, TextBlock Status)> _jobRows =
            new Dictionary<Guid, (RingProgress, TextBlock, TextBlock)>();
        readonly Dictionary<int, RingProgress> _diskRings = new Dictionary<int, RingProgress>();
        Action _detailUpdater;
        Action _modeUpdater;

        public ShellWindow(bool smokeTest)
        {
            _smoke = smokeTest;
            Title = "WipeKit";
            Width = 1080;
            Height = 760;
            MinWidth = 900;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/WipeKit.ico")); } catch { }

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // --- Seitenleiste ------------------------------------------------------------
            var side = new DockPanel { Margin = new Thickness(12, 12, 8, 8) };

            var header = new DockPanel { Margin = new Thickness(6, 0, 0, 12) };
            var refresh = IconButton("", "Laufwerke neu einlesen");
            refresh.Click += (s, e) => RefreshDisks(true);
            DockPanel.SetDock(refresh, Dock.Right);
            header.Children.Add(refresh);
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
            _jobsHeader = SectionHeader("Löschvorgänge");
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

            // --- Inhalt ------------------------------------------------------------------
            var content = new Border
            {
                Margin = new Thickness(0, 12, 12, 12),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Child = _detailHost,
                ClipToBounds = true,
            };
            content.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
            content.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            Grid.SetColumn(content, 1);
            root.Children.Add(content);
            Content = root;

            _diskList.SelectionChanged += OnListSelection;
            _jobList.SelectionChanged += OnListSelection;
            Closing += OnClosing;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
            _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _refreshDebounce.Tick += (s, e) => { _refreshDebounce.Stop(); RefreshDisks(false); };
            var periodic = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            periodic.Tick += (s, e) => RefreshDisks(false);
            periodic.Start();

            RebuildJobList();
            UpdateSleepStatus();
            ShowDetail();

            if (_smoke)
                Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(RunSmokeTest), DispatcherPriority.ApplicationIdle);
            else
                RefreshDisks(true);
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
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
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

        async void RefreshDisks(bool force)
        {
            if (_refreshing) return;
            _refreshing = true;
            List<DiskInfo> disks;
            try
            {
                disks = await Task.Run(() => DiskService.ListExternalDisks());
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
                var first = _disks.FirstOrDefault(d => !d.IsProtected && ActiveJobFor(d) == null);
                if (first != null) Select("disk", first.Number.ToString());
            }
            RebuildDiskList();
            ShowDetail();
        }

        void RebuildDiskList()
        {
            _updatingLists = true;
            _diskList.Items.Clear();
            _diskRings.Clear();
            _noDisks.Visibility = _disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                var job = ActiveJobFor(d);
                if (d.IsProtected)
                {
                    var lockIcon = Glyph("", 13);
                    Grid.SetColumn(lockIcon, 2);
                    g.Children.Add(lockIcon);
                }
                else if (job != null)
                {
                    var ring = new RingProgress { Width = 16, Height = 16, Thickness = 2.5, Value = job.Fraction, RingColor = Blue };
                    Grid.SetColumn(ring, 2);
                    g.Children.Add(ring);
                    _diskRings[d.Number] = ring;
                }
                var item = new ListBoxItem { Content = g, Tag = "disk:" + d.Number };
                _diskList.Items.Add(item);
                if (_selKind == "disk" && _selId == d.Number.ToString()) _diskList.SelectedItem = item;
            }
            _updatingLists = false;
        }

        // --- Löschvorgänge in der Seitenleiste ---------------------------------------------------

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
                texts.Children.Add(Text(j.Disk.Name, 14));
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
                    switch (j.State)
                    {
                        case JobState.Done: r.Glyph.Text = ""; r.Glyph.Foreground = new SolidColorBrush(Green); break;
                        case JobState.Failed: r.Glyph.Text = ""; r.Glyph.Foreground = new SolidColorBrush(Red); break;
                        default: r.Glyph.Text = ""; r.Glyph.Foreground = new SolidColorBrush(Orange); break;
                    }
                }
            }
            foreach (var j in _jobs.Where(x => x.IsActive))
                if (_diskRings.TryGetValue(j.Disk.Number, out var ring)) ring.Value = j.Fraction;
        }

        void UpdateSleepStatus()
        {
            bool active = _jobs.Any(j => j.IsActive);
            _sleepGlyph.Text = active ? "" : "";
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
                    else if (_mode.Key == "ssd") _mode = WipeMode.ByKey("secure");
                }
            }
            _updatingLists = true;
            _diskList.SelectedItem = _diskList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == kind + ":" + id);
            _jobList.SelectedItem = _jobList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == kind + ":" + id);
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
            if (_selKind == "job")
            {
                var j = FindJob(_selId);
                if (j != null) view = BuildJobPage(j);
            }
            else if (_selKind == "disk")
            {
                var d = FindDisk(_selId);
                if (d != null) view = BuildDiskPage(d);
            }
            view ??= BuildEmptyPage();
            _detailHost.Content = view;
        }

        UIElement BuildEmptyPage()
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var g = Glyph("", 56);
            g.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            sp.Children.Add(g);
            bool none = _disks.Count == 0;
            var title = Text(none ? "Kein Laufwerk angeschlossen" : "Kein Laufwerk ausgewählt", 22, FontWeights.SemiBold);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.Margin = new Thickness(0, 16, 0, 6);
            sp.Children.Add(title);
            var msg = Text(none
                ? "Schließe ein Laufwerk per USB an. Es erscheint automatisch in der Seitenleiste."
                : "Wähle links das Laufwerk aus, das gelöscht werden soll.", 14, brushKey: "TextFillColorSecondaryBrush", wrap: true);
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

        // Laufwerk ---------------------------------------------------------------------------------

        UIElement BuildDiskPage(DiskInfo d)
        {
            var body = PageBody();
            body.Children.Add(Header(d.Glyph, d.IsProtected ? Colors.Gray : Blue, d.Name, $"{Fmt.Bytes(d.Size)} · {d.TypeLabel} · {d.Bus}"));
            UIElement bar = null;
            var job = ActiveJobFor(d);

            if (d.IsProtected)
            {
                body.Children.Add(new Border { Height = 20 });
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var lockG = Glyph("", 16);
                lockG.Margin = new Thickness(0, 0, 12, 0);
                row.Children.Add(lockG);
                row.Children.Add(Text("Auf diesem Laufwerk läuft Windows. Es ist geschützt und kann nicht gelöscht werden.", 14, wrap: true));
                body.Children.Add(Card(row));
            }
            else if (job != null)
            {
                body.Children.Add(new Border { Height = 20 });
                var dp = new DockPanel();
                var btn = NormalButton("Fortschritt anzeigen");
                btn.Click += (s, e) => { Select("job", job.Id.ToString()); ShowDetail(); };
                DockPanel.SetDock(btn, Dock.Right);
                dp.Children.Add(btn);
                dp.Children.Add(Text("Dieses Laufwerk wird gerade gelöscht.", 14, verticalCenter: true));
                body.Children.Add(Card(dp));
            }
            else
            {
                // Methode wählen
                body.Children.Add(PageSection("Löschmethode"));
                var modes = new StackPanel();
                var recommended = WipeMode.Recommended(d);
                for (int i = 0; i < WipeMode.All.Length; i++)
                {
                    var m = WipeMode.All[i];
                    if (i > 0) modes.Children.Add(Divider());
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.Children.Add(Tile(m.Glyph, m.Tint, 32, 15));
                    var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
                    titleRow.Children.Add(Text(m.Title, 14, FontWeights.SemiBold));
                    if (m.Key == recommended.Key)
                    {
                        var badge = new Border
                        {
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(6, 1, 6, 1),
                            Margin = new Thickness(8, 0, 0, 0),
                            Background = new SolidColorBrush(Color.FromArgb(40, Blue.R, Blue.G, Blue.B)),
                            Child = Text("Empfohlen", 11, FontWeights.SemiBold),
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        ((TextBlock)badge.Child).Foreground = new SolidColorBrush(Blue);
                        titleRow.Children.Add(badge);
                    }
                    texts.Children.Add(titleRow);
                    texts.Children.Add(Text(m.Tagline, 12, brushKey: "TextFillColorSecondaryBrush"));
                    Grid.SetColumn(texts, 1);
                    g.Children.Add(texts);
                    var radio = new RadioButton
                    {
                        Content = g,
                        GroupName = "mode",
                        IsChecked = m.Key == _mode.Key,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        VerticalContentAlignment = VerticalAlignment.Center,
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
                var meterBars = new Border[4];
                var meter = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                for (int i = 0; i < 4; i++)
                {
                    meterBars[i] = new Border { Width = 16, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
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
                detailStack.Children.Add(Rows(InfoRow("Geeignet für", best), InfoRow("Sicherheit", meter), InfoRow("Geschätzte Dauer", duration)));
                body.Children.Add(Card(detailStack));
                body.Children.Add(note);
                body.Children.Add(hint);

                _modeUpdater = () =>
                {
                    var m = _mode;
                    modeTitle.Text = $"Über »{m.Title}«";
                    explanation.Text = m.Explanation;
                    best.Text = m.BestFor;
                    for (int i = 0; i < 4; i++)
                        meterBars[i].Background = i < m.SecurityLevel
                            ? new SolidColorBrush(m.Tint)
                            : new SolidColorBrush(Color.FromArgb(50, 128, 128, 128));
                    secLabel.Text = m.SecurityLabel;
                    double speed = d.IsSsd == true ? 300e6 : 130e6;
                    duration.Text = "ca. " + Fmt.ShortDuration(TimeSpan.FromSeconds(d.Size * (double)m.WorkUnits / speed));
                    note.Text = m.SecurityNote;
                    string h = null;
                    if (d.IsSsd == true && m.Key != "ssd") h = "Dieses Laufwerk ist eine SSD – dafür ist der Modus »SSD« besser geeignet.";
                    else if (d.IsSsd == false && m.Key == "ssd") h = "Dieses Laufwerk ist eine Festplatte – TRIM wird hier nicht unterstützt. »Sicher« ist die bessere Wahl.";
                    else if (d.IsSsd == null) h = "Der Laufwerkstyp wird vom USB-Adapter nicht gemeldet. Handelt es sich um eine SSD, wähle den Modus »SSD«.";
                    hint.Text = h == null ? "" : "⚠ " + h;
                    hint.Visibility = h == null ? Visibility.Collapsed : Visibility.Visible;
                };
                _modeUpdater();

                var dp = new DockPanel();
                var wipe = DangerButton("Löschen …");
                wipe.Click += (s, e) => ConfirmWipe(d);
                DockPanel.SetDock(wipe, Dock.Right);
                dp.Children.Add(wipe);
                dp.Children.Add(Text($"Alle Daten auf »{d.Name}« werden unwiderruflich gelöscht.", 14,
                    brushKey: "TextFillColorSecondaryBrush", verticalCenter: true));
                bar = dp;
            }

            body.Children.Add(PageSection("Informationen"));
            body.Children.Add(Card(Rows(
                InfoRow("Gerät", ValueText($"{d.Id} ({d.DevicePath})")),
                InfoRow("Kapazität", ValueText($"{Fmt.Bytes(d.Size)} ({Fmt.ExactBytes(d.Size)})")),
                InfoRow("Anschluss", ValueText(d.Bus)),
                InfoRow("Typ", ValueText(d.TypeLabel)),
                InfoRow("Laufwerke", ValueText(d.Volumes.Count > 0 ? string.Join(", ", d.Volumes) : "Keine")))));
            return PageWithBar(body, bar);
        }

        void ConfirmWipe(DiskInfo d)
        {
            var m = _mode;
            string text = $"Alle Daten auf »{d.Name}« ({d.Id}, {Fmt.Bytes(d.Size)}) werden mit der Methode »{m.Title}« " +
                          "unwiderruflich gelöscht.\n\nDieser Vorgang kann nicht rückgängig gemacht werden. Fortfahren?";
            var answer = MessageBox.Show(this, text, "Laufwerk löschen?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            if (ActiveJobFor(d) != null) return;
            var job = new WipeJob(d, m);
            _jobs.Insert(0, job);
            _lastStates[job.Id] = job.State;
            job.Start();
            Select("job", job.Id.ToString());
            RebuildJobList();
            RebuildDiskList();
            UpdateSleepStatus();
            ShowDetail();
        }

        // Löschvorgang ------------------------------------------------------------------------------

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
            var centerGlyph = Glyph("", 48);
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
            subline.MaxWidth = 520;
            heroStack.Children.Add(headline);
            heroStack.Children.Add(subline);
            body.Children.Add(Card(heroStack));

            UIElement bar;
            TextBlock rStep = null, rDone = null, rSpeed = null, rEta = null, rEnd = null, rElapsed = null, rSleep = null;
            Button cancel = null;
            if (j.IsActive)
            {
                body.Children.Add(PageSection("Fortschritt"));
                rStep = ValueText("–"); rDone = ValueText("–"); rSpeed = ValueText("–");
                rEta = ValueText("–"); rEnd = ValueText("–"); rElapsed = ValueText("–");
                body.Children.Add(Card(Rows(
                    InfoRow("Schritt", rStep), InfoRow("Verarbeitet", rDone), InfoRow("Geschwindigkeit", rSpeed),
                    InfoRow("Verbleibend", rEta), InfoRow("Voraussichtlich fertig", rEnd), InfoRow("Läuft seit", rElapsed))));
                rSleep = ValueText("–");
                body.Children.Add(PageSection("Energie"));
                body.Children.Add(Card(InfoRow("Energiesparmodus", rSleep)));
                var foot = Text("Der Bildschirm darf ausgehen. Sobald der Vorgang beendet ist, verhält sich der PC wieder wie gewohnt.",
                    12, brushKey: "TextFillColorSecondaryBrush", wrap: true);
                foot.Margin = new Thickness(2, 8, 0, 0);
                body.Children.Add(foot);

                var dp = new DockPanel();
                cancel = NormalButton("Abbrechen …");
                cancel.Click += (s, e) =>
                {
                    var a = MessageBox.Show(this, "Das Laufwerk ist danach nur teilweise überschrieben.", "Löschvorgang abbrechen?",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
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
                if (j.Mode.Verifies)
                    rows.Add(InfoRow("Prüfung", ValueText(j.Verified == true ? "Bestanden" : j.Verified == false ? "Nicht bestanden" : "Nicht durchgeführt")));
                if (j.Mode.Trims) rows.Add(InfoRow("TRIM", ValueText(j.TrimResult ?? "Nicht durchgeführt")));
                if (j.FinishedAt.HasValue) rows.Add(InfoRow("Beendet", ValueText(Fmt.DateTimeText(j.FinishedAt.Value))));
                body.Children.Add(Card(Rows(rows.ToArray())));

                if (j.ProtocolPath != null)
                {
                    body.Children.Add(PageSection("Protokoll"));
                    var dpp = new DockPanel();
                    var folder = NormalButton("Im Explorer zeigen");
                    folder.Click += (s, e) => OpenExplorerSelect(j.ProtocolPath);
                    DockPanel.SetDock(folder, Dock.Right);
                    dpp.Children.Add(folder);
                    dpp.Children.Add(Text(Path.GetFileName(j.ProtocolPath), 14, verticalCenter: true));
                    body.Children.Add(Card(dpp));
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
                InfoRow("Methode", ValueText($"{j.Mode.Title} · {j.Mode.Tagline}")))));

            _detailUpdater = () =>
            {
                var state = j.State;
                ring.RingColor = state == JobState.Done ? Green : state == JobState.Failed ? Red : state == JobState.Cancelled ? Orange : Blue;
                ring.Value = state == JobState.Done ? 1 : j.Fraction;
                bool active = j.IsActive;
                pct.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                centerGlyph.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
                pct.Text = Fmt.Percent(j.Fraction);
                stepText.Text = state == JobState.Running ? $"Schritt {j.Step} von {j.Mode.StepCount}" : "";
                centerGlyph.Text = state == JobState.Done ? "" : state == JobState.Failed ? "" : "";
                centerGlyph.Foreground = new SolidColorBrush(ring.RingColor);
                headline.Text = state switch
                {
                    JobState.Starting => "Wird vorbereitet",
                    JobState.Running => "Wird gelöscht",
                    JobState.Done => "Löschen abgeschlossen",
                    JobState.Failed => "Löschen fehlgeschlagen",
                    _ => "Löschen abgebrochen",
                };
                subline.Text = state == JobState.Done ? $"»{j.Disk.Name}« wurde sicher gelöscht." : active ? j.Phase : j.Disk.Name;

                if (rStep != null)
                {
                    if (state == JobState.Running)
                    {
                        rStep.Text = $"{j.Step} von {j.Mode.StepCount} · {j.Phase}";
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
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
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
                var first = _disks.FirstOrDefault(d => !d.IsProtected && ActiveJobFor(d) == null);
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
            j.ProtocolPath = WriteProtocol(j);
            var d = j.Disk;
            string title, text;
            MessageBoxImage image;
            if (j.State == JobState.Done)
            {
                title = "Löschen abgeschlossen";
                text = $"{d.Name} ({d.Id}, {Fmt.Bytes(d.Size)}) wurde erfolgreich gelöscht.";
                if (j.Elapsed.HasValue) text += "\nDauer: " + Fmt.Duration(j.Elapsed.Value);
                if (j.Verified == true) text += "\nPrüfung: bestanden – jeder Sektor ist überschrieben.";
                if (j.Mode.Trims && j.TrimResult != null) text += "\nTRIM: " + j.TrimResult;
                image = MessageBoxImage.Information;
            }
            else if (j.State == JobState.Cancelled)
            {
                title = "Löschvorgang abgebrochen";
                text = $"{d.Name} ({d.Id}): {j.Message}";
                image = MessageBoxImage.Warning;
            }
            else
            {
                title = "Löschen fehlgeschlagen";
                text = $"{d.Name} ({d.Id}): {j.Message ?? "Unbekannter Fehler"}";
                image = MessageBoxImage.Error;
            }
            if (_smoke) return;
            if (j.State == JobState.Done) SystemSounds.Asterisk.Play(); else SystemSounds.Hand.Play();
            Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(this, text, title, MessageBoxButton.OK, image)));
        }

        public static string ProtocolFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WipeKit-Protokolle");

        string WriteProtocol(WipeJob j)
        {
            try
            {
                Directory.CreateDirectory(ProtocolFolder);
                var d = j.Disk;
                DateTime started = j.StartedAt;
                DateTime ended = j.FinishedAt ?? DateTime.Now;
                TimeSpan duration = ended - started;
                if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
                string safe = string.Concat(d.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
                string path = Path.Combine(ProtocolFolder, $"{started:yyyy-MM-dd_HH-mm}_Datentraeger{d.Number}_{safe}.txt");
                string result = j.State == JobState.Done ? "ERFOLGREICH"
                    : j.State == JobState.Cancelled ? "ABGEBROCHEN – Laufwerk nur teilweise überschrieben" : "FEHLGESCHLAGEN";
                string verification = "nicht vorgesehen (Modus »Schnell«)";
                if (j.Mode.Verifies)
                {
                    verification = j.Verified == true
                        ? (j.Mode.Passes[j.Mode.Passes.Length - 1] == Pattern.Seeded
                            ? "bestanden – alle Sektoren gelesen und mit den geschriebenen Zufallsdaten verglichen"
                            : "bestanden – alle Sektoren gelesen, alle enthalten Nullen")
                        : j.Verified == false ? "NICHT bestanden" : "nicht durchgeführt";
                }
                string trim = j.Mode.Trims ? (j.TrimResult ?? "nicht durchgeführt") : "nicht vorgesehen";
                double avg = duration.TotalSeconds > 0 ? j.BytesDone / duration.TotalSeconds : 0;
                string version = typeof(ShellWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
                string text =
$@"WIPEKIT – LÖSCHPROTOKOLL
========================

Laufwerk:          {d.Name}
Kennung:           {d.Id} ({d.DevicePath})
Kapazität:         {Fmt.Bytes(d.Size)} ({Fmt.ExactBytes(j.DiskSize > 0 ? j.DiskSize : d.Size)})
Anschluss:         {d.Bus}
Typ:               {d.TypeLabel}
Frühere Volumes:   {(d.Volumes.Count > 0 ? string.Join(", ", d.Volumes) : "–")}

Methode:           {j.Mode.Title} – {j.Mode.MethodDescription}
Beginn:            {Fmt.DateTimeText(started)}
Ende:              {Fmt.DateTimeText(ended)}
Dauer:             {Fmt.Duration(duration)}
Ø Geschwindigkeit: {Fmt.Speed(avg)}

Ergebnis:          {result}
Verifikation:      {verification}
TRIM:              {trim}
Meldung:           {j.Message ?? "–"}

Rechner:           {Environment.MachineName}
Erstellt mit WipeKit {version} (Windows)
";
                File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
                return path;
            }
            catch
            {
                return null;
            }
        }

        // --- Schließen --------------------------------------------------------------------------------

        void OnClosing(object sender, CancelEventArgs e)
        {
            var active = _jobs.Where(j => j.IsActive).ToList();
            if (active.Count == 0 || _closingConfirmed || _smoke) return;
            string text = (active.Count == 1 ? "Ein Löschvorgang läuft noch." : $"{active.Count} Löschvorgänge laufen noch.") +
                          "\n\nBeim Beenden wird das Löschen abgebrochen und das Laufwerk ist nur teilweise überschrieben. Trotzdem beenden?";
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
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }

        void RunSmokeTest()
        {
            try
            {
                var disks = new List<DiskInfo>
                {
                    new DiskInfo { Number = 3, Name = "Test SSD", Size = 500_000_000_000, Bus = "USB", IsSsd = true, Volumes = new List<string> { "Daten (E:)" } },
                    new DiskInfo { Number = 4, Name = "Test HDD", Size = 2_000_000_000_000, Bus = "USB", IsSsd = false },
                    new DiskInfo { Number = 5, Name = "Test Unbekannt", Size = 64_000_000_000, Bus = "SD", IsSsd = null },
                    new DiskInfo { Number = 6, Name = "Windows To Go", Size = 256_000_000_000, Bus = "USB", IsSsd = true, IsProtected = true },
                };
                ApplyDisks(disks, true);
                foreach (var d in disks)
                {
                    Select("disk", d.Number.ToString());
                    ShowDetail();
                    foreach (var m in WipeMode.All)
                    {
                        _mode = m;
                        _modeUpdater?.Invoke();
                    }
                }
                Select("disk", "3");
                ShowDetail();
                SaveShot("windows-1-ssd");
                Select("disk", "4");
                ShowDetail();
                SaveShot("windows-2-festplatte");
                foreach (var state in new[] { JobState.Starting, JobState.Running, JobState.Done, JobState.Failed, JobState.Cancelled })
                {
                    var job = new WipeJob(disks[0], WipeMode.ByKey("ssd"));
                    job.SetTestState(state, 0.42, "Testmeldung");
                    _jobs.Insert(0, job);
                    Select("job", job.Id.ToString());
                    Tick();
                    RebuildJobList();
                    ShowDetail();
                    _detailUpdater?.Invoke();
                    if (state == JobState.Running) { UpdateSleepStatus(); SaveShot("windows-3-loeschen"); }
                    if (state == JobState.Done) SaveShot("windows-4-fertig");
                }
                UpdateSleepStatus();
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "WipeKit-Smoke.log"), ex.ToString()); } catch { }
                Application.Current.Shutdown(1);
            }
        }
    }
}
