using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DialWin;

internal static class Palette
{
    public static readonly Brush Panel = Frozen(new SolidColorBrush(Color.FromArgb(0xEB, 0x1A, 0x1A, 0x1C)));
    public static readonly Brush Wedge = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2F)));
    public static readonly Brush WedgeActive = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x4A, 0x50)));
    public static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x2D)));
    public static readonly Brush Text = Frozen(new SolidColorBrush(Colors.White));
    public static readonly Brush TextDim = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2)));

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
}

/// <summary>Click-through window that never takes focus from the app being controlled.</summary>
internal abstract class OverlayWindow : Window
{
    protected OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            Native.SetWindowLong(h, Native.GWL_EXSTYLE,
                ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);
        };
    }

    protected void FadeTo(double opacity, int ms, Action? done = null)
    {
        var a = new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(ms));
        if (done != null) a.Completed += (_, _) => done();
        BeginAnimation(OpacityProperty, a);
    }
}

internal sealed class WheelWindow : OverlayWindow
{
    const double Size = 340, Outer = 156, Inner = 72, Gap = 2.5;

    readonly DialModel _model;
    readonly Canvas _canvas = new() { Width = Size, Height = Size };
    Ellipse? _ring;

    public WheelWindow(DialModel model)
    {
        _model = model;
        Width = Size;
        Height = Size;
        Content = _canvas;
        Opacity = 0;
        model.Changed += Refresh;
        model.DwellChanged += UpdateRing;
    }

    void Refresh()
    {
        if (_model.Phase != Phase.InMode)
        {
            Draw();
            if (!IsVisible)
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - Size - 16;
                Top = area.Bottom - Size - 16;
                Show();
            }
            FadeTo(1, 120);
        }
        else if (IsVisible)
        {
            FadeTo(0, 120, Hide);
        }
    }

    /// <summary>The ring fills while the dial is held still; when it's full, the highlighted slice is chosen.</summary>
    void UpdateRing()
    {
        if (_ring != null) { _canvas.Children.Remove(_ring); _ring = null; }
        if (!_model.Dwelling || _model.Phase == Phase.InMode || !IsVisible) return;

        const double thick = 4, d = (Inner - 8) * 2;
        double circumference = Math.PI * d / thick; // dash lengths are in multiples of the thickness
        var ring = new Ellipse
        {
            Width = d, Height = d,
            Stroke = Palette.Accent,
            StrokeThickness = thick,
            StrokeDashArray = new DoubleCollection { circumference, circumference + 1 },
            StrokeDashOffset = circumference,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-90),
        };
        Canvas.SetLeft(ring, Size / 2 - d / 2);
        Canvas.SetTop(ring, Size / 2 - d / 2);
        ring.BeginAnimation(Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(circumference, 0, TimeSpan.FromMilliseconds(_model.DwellMs)));
        _canvas.Children.Add(ring);
        _ring = ring;
    }

    static Point OnCircle(double r, double deg)
    {
        double rad = deg * Math.PI / 180;
        return new Point(Size / 2 + r * Math.Cos(rad), Size / 2 + r * Math.Sin(rad));
    }

    void Draw()
    {
        _canvas.Children.Clear();
        _ring = null;
        _canvas.Children.Add(new Ellipse { Width = Size - 4, Height = Size - 4, Fill = Palette.Panel });
        Canvas.SetLeft(_canvas.Children[0], 2);
        Canvas.SetTop(_canvas.Children[0], 2);

        var items = _model.WheelItems;
        int n = items.Count;
        double step = 360.0 / Math.Max(n, 3);
        for (int i = 0; i < n; i++)
        {
            bool selected = i == _model.Selection;
            double mid = -90 + step * i;
            double a0 = mid - step / 2 + Gap / 2, a1 = mid + step / 2 - Gap / 2;
            double r = selected ? Outer + 6 : Outer;

            var fig = new PathFigure { StartPoint = OnCircle(r, a0), IsClosed = true };
            fig.Segments.Add(new ArcSegment(OnCircle(r, a1), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(OnCircle(Inner, a1), true));
            fig.Segments.Add(new ArcSegment(OnCircle(Inner, a0), new Size(Inner, Inner), 0, false, SweepDirection.Counterclockwise, true));
            _canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = new PathGeometry(new[] { fig }),
                Fill = selected ? Palette.WedgeActive : Palette.Wedge,
                Stroke = selected ? Palette.Accent : null,
                StrokeThickness = selected ? 2 : 0,
            });

            var p = OnCircle((Outer + Inner) / 2, mid);
            FrameworkElement label;
            if (items[i].Icon is { } icon)
            {
                label = new Image { Source = icon, Width = 32, Height = 32, Opacity = selected ? 1 : 0.75 };
            }
            else
            {
                bool active = _model.Phase == Phase.Picking && i == _model.ActiveIndex;
                label = new TextBlock
                {
                    Text = items[i].Short,
                    Foreground = selected ? Palette.Text : Palette.TextDim,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13,
                    FontWeight = selected || active ? FontWeights.SemiBold : FontWeights.Normal,
                };
            }
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, p.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, p.Y - label.DesiredSize.Height / 2);
            _canvas.Children.Add(label);
        }

        var centre = new TextBlock
        {
            Text = n == 0 ? "No apps open" : items[_model.Selection].Label,
            Foreground = Palette.Text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 70,
            Width = Inner * 1.7,
        };
        centre.Measure(new Size(centre.Width, double.PositiveInfinity));
        Canvas.SetLeft(centre, Size / 2 - centre.Width / 2);
        Canvas.SetTop(centre, Size / 2 - centre.DesiredSize.Height / 2);
        _canvas.Children.Add(centre);
    }
}

internal sealed class HudWindow : OverlayWindow
{
    readonly TextBlock _title = new()
    {
        Foreground = Palette.TextDim, FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
    };
    readonly TextBlock _detail = new()
    {
        Foreground = Palette.Text, FontFamily = new FontFamily("Segoe UI"), FontSize = 20, FontWeight = FontWeights.SemiBold,
    };
    readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1300) };

    public HudWindow()
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 180;
        Opacity = 0;

        var stack = new StackPanel { Margin = new Thickness(26, 12, 26, 14) };
        stack.Children.Add(_title);
        stack.Children.Add(_detail);
        Content = new Border { CornerRadius = new CornerRadius(22), Background = Palette.Panel, Child = stack };

        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            FadeTo(0, 200, Hide);
        };
    }

    public void ShowText(string title, string detail)
    {
        _title.Text = title;
        _detail.Text = string.IsNullOrEmpty(detail) ? title : detail;
        _title.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;

        if (!IsVisible) Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 24;
        FadeTo(1, 100);
        _hide.Stop();
        _hide.Start();
    }
}
