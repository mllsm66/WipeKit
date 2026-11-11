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
