using System;
using System.Windows;
using System.Windows.Media;

namespace WipeKit
{
    /// <summary>Kreisförmige Fortschrittsanzeige.</summary>
    public sealed class RingProgress : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(RingProgress),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        Color _color = Color.FromRgb(0x00, 0x67, 0xC0);
        public Color RingColor
        {
            get => _color;
            set { _color = value; InvalidateVisual(); }
        }

        public double Thickness { get; set; } = 8;

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= Thickness) return;
            double r = (size - Thickness) / 2;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);

            var track = new SolidColorBrush(Color.FromArgb(48, _color.R, _color.G, _color.B));
            track.Freeze();
            dc.DrawEllipse(null, new Pen(track, Thickness), c, r, r);

            double v = Math.Max(0, Math.Min(1, Value));
            if (v <= 0) return;
            var brush = new SolidColorBrush(_color);
            brush.Freeze();
            var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (v >= 0.9999)
            {
                dc.DrawEllipse(null, pen, c, r, r);
                return;
            }
            double angle = v * 360;
            double rad = (angle - 90) * Math.PI / 180;
            var start = new Point(c.X, c.Y - r);
            var end = new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
    }
}

namespace WipeKit
{
    /// <summary>Lesegeschwindigkeit über die Position auf dem Laufwerk (Oberflächentest).</summary>
    public sealed class SpeedCurve : System.Windows.FrameworkElement
    {
        System.Collections.Generic.List<double> _values = new System.Collections.Generic.List<double>();
        int _total = 200;

        public void Update(System.Collections.Generic.List<double> values, int total)
        {
            _values = values ?? new System.Collections.Generic.List<double>();
            _total = System.Math.Max(1, total);
            InvalidateVisual();
        }

        public static int TotalBuckets(long size)
        {
            long chunk = 4L * 1024 * 1024;
            long bucket = System.Math.Max(chunk, size / 200);
            return (int)System.Math.Max(1, (size + bucket - 1) / bucket);
        }

        protected override void OnRender(System.Windows.Media.DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            const double padL = 44, padB = 20, padT = 6;
            double gw = w - padL - 6, gh = h - padB - padT;
            if (gw <= 10 || gh <= 10) return;
            var fg = (TryFindResource("TextFillColorSecondaryBrush") as System.Windows.Media.SolidColorBrush)?.Color
                     ?? System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60);
            var accent = System.Windows.Media.Color.FromRgb(0x00, 0x67, 0xC0);
            var gridPen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, fg.R, fg.G, fg.B)), 1);
            var textBrush = new System.Windows.Media.SolidColorBrush(fg);
            double vmax = System.Math.Max(1, (_values.Count > 0 ? System.Linq.Enumerable.Max(_values) : 100) * 1.15);
            var tf = new System.Windows.Media.Typeface("Segoe UI");
            double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int i = 0; i < 5; i++)
            {
                double y = padT + gh * i / 4;
                dc.DrawLine(gridPen, new System.Windows.Point(padL, y), new System.Windows.Point(padL + gw, y));
                var ft = new System.Windows.Media.FormattedText((vmax * (4 - i) / 4).ToString("0"), System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight, tf, 11, textBrush, dpi);
                dc.DrawText(ft, new System.Windows.Point(padL - 8 - ft.Width, y - ft.Height / 2));
            }
            void Label(string s, double x, bool right = false, bool center = false)
            {
                var ft = new System.Windows.Media.FormattedText(s, System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight, tf, 11, textBrush, dpi);
                double px = right ? x - ft.Width : center ? x - ft.Width / 2 : x;
                dc.DrawText(ft, new System.Windows.Point(px, h - ft.Height));
            }
            Label("Anfang", padL);
            Label("MB/s", padL + gw / 2, center: true);
            Label("Ende", padL + gw, right: true);
            if (_values.Count == 0) return;
            var pts = new System.Collections.Generic.List<System.Windows.Point>();
            for (int i = 0; i < _values.Count; i++)
                pts.Add(new System.Windows.Point(padL + gw * (i + 0.5) / _total, padT + gh * (1 - _values[i] / vmax)));
            var fill = new System.Windows.Media.StreamGeometry();
            using (var ctx = fill.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(pts[0].X, padT + gh), true, true);
                foreach (var p in pts) ctx.LineTo(p, true, false);
                ctx.LineTo(new System.Windows.Point(pts[pts.Count - 1].X, padT + gh), true, false);
            }
            dc.DrawGeometry(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(45, accent.R, accent.G, accent.B)), null, fill);
            var line = new System.Windows.Media.StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(pts[0], false, false);
                foreach (var p in pts) ctx.LineTo(p, true, true);
            }
            dc.DrawGeometry(null, new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(accent), 1.8), line);
        }
    }
}
