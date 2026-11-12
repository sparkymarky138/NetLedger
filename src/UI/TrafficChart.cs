using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NetLedger.Core;

namespace NetLedger.UI;

/// <summary>Lightweight two-series area chart (download / upload) with a hover read-out.</summary>
public sealed class TrafficChart : FrameworkElement
{
    double[] _down = Array.Empty<double>();
    double[] _up = Array.Empty<double>();
    Func<int, string> _label = i => i.ToString();
    bool _isRate = true;
    int _hover = -1;

    static readonly Brush DownLine = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0xB6)));
    static readonly Brush UpLine = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xA2, 0x61)));
    static readonly Brush DownFill = Freeze(Gradient(Color.FromArgb(0x70, 0x2E, 0xC4, 0xB6)));
    static readonly Brush UpFill = Freeze(Gradient(Color.FromArgb(0x60, 0xF4, 0xA2, 0x61)));
    static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x9B, 0xAA)));
    static readonly Brush Text = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)));
    static readonly Brush TipBg = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0x0F, 0x17, 0x20)));
    static readonly Pen GridPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x24, 0x32, 0x3F)), 1));
    static readonly Pen HoverPen = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xE6, 0xED, 0xF3)), 1) { DashStyle = DashStyles.Dash });
    static readonly Pen DownPen = FreezePen(new Pen(DownLine, 1.6) { LineJoin = PenLineJoin.Round });
    static readonly Pen UpPen = FreezePen(new Pen(UpLine, 1.6) { LineJoin = PenLineJoin.Round });
    static readonly Typeface Face = new("Segoe UI");

    public string EmptyText { get; set; } = "Waiting for traffic…";

    public void SetData(double[] down, double[] up, Func<int, string> label, bool isRate)
    {
        _down = down; _up = up; _label = label; _isRate = isRate;
        InvalidateVisual();
    }

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
    static Pen FreezePen(Pen p) { p.Freeze(); return p; }
    static Brush Gradient(Color top) =>
        new LinearGradientBrush(top, Color.FromArgb(0x05, top.R, top.G, top.B), 90);

    string Fmt1(double v) => _isRate ? Fmt.Rate(v) : Fmt.Bytes(v);

    FormattedText Txt(string s, double size, Brush b) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    Rect PlotRect() => new(70, 10, Math.Max(10, ActualWidth - 70 - 10), Math.Max(10, ActualHeight - 10 - 24));

    static double NiceCeiling(double v)
    {
        if (v <= 0) return 1024;
        double m = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var f in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            if (f * m >= v) return f * m;
        return 10 * m;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // for mouse hit-testing
        if (w < 120 || h < 60) return;
        var plot = PlotRect();
        int n = Math.Min(_down.Length, _up.Length);

        double max = 0;
        for (int i = 0; i < n; i++) max = Math.Max(max, Math.Max(_down[i], _up[i]));
        double top = NiceCeiling(max * 1.08);

        for (int g = 0; g <= 4; g++)
        {
            double y = plot.Bottom - plot.Height * g / 4;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Txt(Fmt1(top * g / 4), 11, Muted);
            dc.DrawText(t, new Point(plot.Left - 8 - t.Width, y - t.Height / 2));
        }

        if (n < 2)
        {
            var t = Txt(EmptyText, 13, Muted);
            dc.DrawText(t, new Point(plot.Left + (plot.Width - t.Width) / 2, plot.Top + (plot.Height - t.Height) / 2));
            return;
        }

        DrawSeries(dc, _down, n, plot, top, DownFill, DownPen);
        DrawSeries(dc, _up, n, plot, top, UpFill, UpPen);

        int ticks = Math.Min(6, n);
        for (int k = 0; k < ticks; k++)
        {
            int i = (int)Math.Round((n - 1) * (double)k / Math.Max(1, ticks - 1));
            var t = Txt(_label(i), 11, Muted);
            double x = X(i, n, plot) - t.Width / 2;
            x = Math.Clamp(x, plot.Left - 10, plot.Right - t.Width);
            dc.DrawText(t, new Point(x, plot.Bottom + 6));
        }

        if (_hover >= 0 && _hover < n)
        {
            double x = X(_hover, n, plot);
            dc.DrawLine(HoverPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            dc.DrawEllipse(DownLine, null, new Point(x, Y(_down[_hover], plot, top)), 3.5, 3.5);
            dc.DrawEllipse(UpLine, null, new Point(x, Y(_up[_hover], plot, top)), 3.5, 3.5);

            var l1 = Txt(_label(_hover), 11, Muted);
            var l2 = Txt("↓ " + Fmt1(_down[_hover]), 12, DownLine);
            var l3 = Txt("↑ " + Fmt1(_up[_hover]), 12, UpLine);
            double bw = Math.Max(l1.Width, Math.Max(l2.Width, l3.Width)) + 20, bh = l1.Height + l2.Height + l3.Height + 14;
            double bx = x + 12 + bw > plot.Right ? x - 12 - bw : x + 12;
            var box = new Rect(bx, plot.Top + 6, bw, bh);
            dc.DrawRoundedRectangle(TipBg, GridPen, box, 6, 6);
            dc.DrawText(l1, new Point(box.Left + 10, box.Top + 6));
            dc.DrawText(l2, new Point(box.Left + 10, box.Top + 6 + l1.Height + 2));
            dc.DrawText(l3, new Point(box.Left + 10, box.Top + 6 + l1.Height + l2.Height + 2));
        }
    }

    static double X(int i, int n, Rect plot) => plot.Left + plot.Width * i / (n - 1);
    static double Y(double v, Rect plot, double top) => plot.Bottom - plot.Height * Math.Min(1, v / top);

    static void DrawSeries(DrawingContext dc, double[] data, int n, Rect plot, double top, Brush fill, Pen pen)
    {
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(plot.Left, plot.Bottom), true, true);
            for (int i = 0; i < n; i++) g.LineTo(new Point(X(i, n, plot), Y(data[i], plot, top)), false, false);
            g.LineTo(new Point(plot.Right, plot.Bottom), false, false);
        }
        area.Freeze();
        dc.DrawGeometry(fill, null, area);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(plot.Left, Y(data[0], plot, top)), false, false);
            for (int i = 1; i < n; i++) g.LineTo(new Point(X(i, n, plot), Y(data[i], plot, top)), true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int n = Math.Min(_down.Length, _up.Length);
        if (n < 2) return;
        var plot = PlotRect();
        double x = e.GetPosition(this).X;
        int idx = (int)Math.Round((x - plot.Left) / plot.Width * (n - 1));
        idx = Math.Clamp(idx, 0, n - 1);
        if (idx != _hover) { _hover = idx; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
    }
}
