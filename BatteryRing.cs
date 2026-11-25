using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BatteryWidget;

public sealed class BatteryRing : FrameworkElement
{
    private static readonly Brush Track = MakeBrush(58, 232, 239, 249);
    private static readonly Brush Label = MakeBrush(255, 244, 247, 252);
    private static readonly Typeface TextFace = new(new FontFamily("Segoe UI"), FontStyles.Normal,
        FontWeights.SemiBold, FontStretches.Normal);

    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(int?), typeof(BatteryRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(BatteryRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public int? Percent
    {
        get => (int?)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double diameter = Math.Min(RenderSize.Width, RenderSize.Height);
        if (diameter < 10)
            return;
        double thickness = diameter * 0.072;
        double radius = (diameter - thickness) / 2 - 1;
        var center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        drawing.DrawEllipse(null, new Pen(Track, thickness), center, radius, radius);

        if (Percent is int value && value > 0)
        {
            var pen = new Pen(Accent, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (value >= 100)
                drawing.DrawEllipse(null, pen, center, radius, radius);
            else
            {
                double angle = value * Math.PI / 50;
                var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
                var arc = new StreamGeometry();
                using (var context = arc.Open())
                {
                    context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                    context.ArcTo(end, new Size(radius, radius), 0, value > 50, SweepDirection.Clockwise, true, false);
                }
                drawing.DrawGeometry(null, pen, arc);
            }
        }

        var text = new FormattedText(Percent is int percent ? $"{percent}%" : "—", CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, TextFace, diameter * 0.245, Percent.HasValue ? Label : Accent,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    private static Brush MakeBrush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }
}
