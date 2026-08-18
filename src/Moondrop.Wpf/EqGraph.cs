using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using Moondrop.Core.Protocol;

namespace Moondrop.Wpf;

public readonly record struct EqGraphValue(int Frequency, double Gain);

public static class EqGraphProjection
{
    public static EqGraphValue FromNormalized(double x, double y)
    {
        var normalizedX = Math.Clamp(x, 0, 1);
        var normalizedY = Math.Clamp(y, 0, 1);
        var frequency = (int)Math.Round(20 * Math.Pow(1000, normalizedX));
        var gain = Math.Round((12 - normalizedY * 30) * 10) / 10;
        return new EqGraphValue(frequency, gain);
    }

    public static string FormatReadout(EqGraphValue value)
    {
        var frequency = value.Frequency >= 1000
            ? $"{value.Frequency / 1000.0:0.##} kHz"
            : $"{value.Frequency} Hz";
        var gain = value.Gain > 0
            ? $"+{value.Gain:0.0}"
            : value.Gain < 0
                ? $"−{Math.Abs(value.Gain):0.0}"
                : "0.0";
        return $"{frequency}  {gain} dB";
    }
}

public sealed record EqBandResponseSeries(int BandIndex, IReadOnlyList<double> MagnitudeDb);

public sealed record EqGraphResponseSeries(
    IReadOnlyList<double> Frequencies,
    IReadOnlyList<double> CombinedDb,
    IReadOnlyList<EqBandResponseSeries> Bands);

public static class EqGraphResponse
{
    public static EqGraphResponseSeries Calculate(IEnumerable<BandViewModel> bands, double preGain, int sampleCount = 360)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleCount, 2);
        var prepared = bands
            .Where(x => x.Enabled)
            .Select(x => (x.Index, Response: DawnPro2Protocol.PrepareMagnitudeResponse(x.ToCoreBand())))
            .ToArray();
        var frequencies = new double[sampleCount];
        var combined = new double[sampleCount];
        var individual = prepared
            .Select(x => (x.Index, Values: new double[sampleCount]))
            .ToArray();

        for (var i = 0; i < sampleCount; i++)
        {
            var ratio = i / (sampleCount - 1.0);
            var frequency = 20 * Math.Pow(1000, ratio);
            frequencies[i] = frequency;
            combined[i] = preGain;
            for (var bandIndex = 0; bandIndex < prepared.Length; bandIndex++)
            {
                var value = prepared[bandIndex].Response.MagnitudeDb(frequency);
                individual[bandIndex].Values[i] = value;
                combined[i] += value;
            }
        }

        return new EqGraphResponseSeries(
            frequencies,
            combined,
            individual.Select(x => new EqBandResponseSeries(x.Index, x.Values)).ToArray());
    }
}

public sealed class EqGraph : FrameworkElement
{
    public static readonly DependencyProperty BandsProperty = DependencyProperty.Register(
        nameof(Bands),
        typeof(IEnumerable<BandViewModel>),
        typeof(EqGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnBandsChanged));

    public static readonly DependencyProperty PreGainProperty = DependencyProperty.Register(
        nameof(PreGain),
        typeof(double),
        typeof(EqGraph),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnPreGainChanged));

    public static readonly DependencyProperty SelectedBandIndexProperty = DependencyProperty.Register(
        nameof(SelectedBandIndex),
        typeof(int),
        typeof(EqGraph),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    private Pen? _gridPen;
    private Pen? _zeroPen;
    private Brush? _disabledHandleBrush;
    private readonly List<BandViewModel> _bandCache = [];
    private readonly List<(int BandIndex, StreamGeometry Geometry)> _cachedBandResponses = [];
    private Pen? _responsePen;
    private Pen? _individualResponsePen;
    private Pen? _selectedResponsePen;
    private readonly Typeface _typeface = new("Segoe UI Variable Text");
    private int? _dragBand;
    private Point? _hoverPoint;
    private EqGraphValue? _hoverValue;
    private StreamGeometry? _cachedResponse;
    private Size _cachedSize;
    private int _cacheVersion;
    private int _renderedVersion = -1;

    public EqGraph()
    {
        Focusable = true;
        ClipToBounds = true;
        Loaded += (_, _) => SystemParameters.StaticPropertyChanged += SystemParametersChanged;
        Unloaded += (_, _) => SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
    }

    public IEnumerable<BandViewModel>? Bands
    {
        get => (IEnumerable<BandViewModel>?)GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    public double PreGain
    {
        get => (double)GetValue(PreGainProperty);
        set => SetValue(PreGainProperty, value);
    }

    public int SelectedBandIndex
    {
        get => (int)GetValue(SelectedBandIndexProperty);
        set => SetValue(SelectedBandIndexProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        if (bounds.Width < 80 || bounds.Height < 80)
            return;

        EnsureThemeDrawingResources();
        var plot = new Rect(56, 18, bounds.Width - 76, bounds.Height - 54);
        DrawGrid(dc, plot);
        DrawResponse(dc, plot);
        DrawHandles(dc, plot);
        DrawHoverReadout(dc, plot);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var band = HitTestBand(e.GetPosition(this));
        if (band is null)
            return;
        SelectedBandIndex = band.Index;
        _dragBand = band.Index;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var plot = new Rect(56, 18, Math.Max(1, ActualWidth - 76), Math.Max(1, ActualHeight - 54));
        var pointer = e.GetPosition(this);
        UpdateHover(pointer, plot);
        if (_dragBand is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var band = BandList().FirstOrDefault(b => b.Index == _dragBand.Value);
        if (band is null)
            return;
        var p = pointer;
        band.Frequency = FrequencyFromX(Math.Clamp(p.X, plot.Left, plot.Right), plot);
        band.Gain = GainFromY(Math.Clamp(p.Y, plot.Top, plot.Bottom), plot);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        _dragBand = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_dragBand is null)
        {
            _hoverPoint = null;
            _hoverValue = null;
            InvalidateVisual();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var band = HitTestBand(e.GetPosition(this)) ?? BandList().FirstOrDefault(b => b.Index == SelectedBandIndex);
        if (band is null)
            return;
        SelectedBandIndex = band.Index;
        band.Q += e.Delta > 0 ? 0.1 : -0.1;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var band = BandList().FirstOrDefault(b => b.Index == SelectedBandIndex);
        if (band is null)
            return;
        var large = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var handled = true;
        switch (e.Key)
        {
            case Key.Left when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                SelectedBandIndex = Math.Max(0, SelectedBandIndex - 1);
                break;
            case Key.Right when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                SelectedBandIndex = Math.Min(BandList().Count - 1, SelectedBandIndex + 1);
                break;
            case Key.Left:
                band.Frequency = (int)Math.Round(band.Frequency / Math.Pow(2, large ? 1.0 / 3 : 1.0 / 12));
                break;
            case Key.Right:
                band.Frequency = (int)Math.Round(band.Frequency * Math.Pow(2, large ? 1.0 / 3 : 1.0 / 12));
                break;
            case Key.Up:
                band.Gain += large ? 1 : 0.1;
                break;
            case Key.Down:
                band.Gain -= large ? 1 : 0.1;
                break;
            case Key.PageUp:
            case Key.Add:
            case Key.OemPlus:
                band.Q += large ? 1 : 0.1;
                break;
            case Key.PageDown:
            case Key.Subtract:
            case Key.OemMinus:
                band.Q -= large ? 1 : 0.1;
                break;
            default:
                handled = false;
                break;
        }
        if (handled)
        {
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new EqGraphAutomationPeer(this);

    public void RefreshThemeResources()
    {
        _responsePen = null;
        _individualResponsePen = null;
        _selectedResponsePen = null;
        _gridPen = null;
        _zeroPen = null;
        _disabledHandleBrush = null;
        InvalidateVisual();
    }

    private void SystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (
            nameof(SystemParameters.HighContrast) or
            nameof(SystemParameters.WindowGlassBrush) or
            nameof(SystemParameters.WindowGlassColor)) &&
            !string.IsNullOrEmpty(e.PropertyName))
            return;

        RefreshThemeResources();
    }

    private static void OnBandsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (EqGraph)d;
        graph.Detach(e.OldValue as IEnumerable<BandViewModel>);
        graph.Attach(e.NewValue as IEnumerable<BandViewModel>);
        graph._cachedResponse = null;
        graph._cachedBandResponses.Clear();
        graph._cacheVersion++;
        graph.InvalidateVisual();
    }

    private static void OnPreGainChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((EqGraph)d)._cacheVersion++;
    }

    private void Attach(IEnumerable<BandViewModel>? bands)
    {
        _bandCache.Clear();
        if (bands is INotifyCollectionChanged changed)
            changed.CollectionChanged += BandsCollectionChanged;
        foreach (var band in bands ?? [])
        {
            _bandCache.Add(band);
            band.PropertyChanged += BandPropertyChanged;
        }
    }

    private void Detach(IEnumerable<BandViewModel>? bands)
    {
        if (bands is INotifyCollectionChanged changed)
            changed.CollectionChanged -= BandsCollectionChanged;
        foreach (var band in bands ?? [])
            band.PropertyChanged -= BandPropertyChanged;
        _bandCache.Clear();
    }

    private void BandsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (BandViewModel band in e.OldItems ?? Array.Empty<BandViewModel>())
            band.PropertyChanged -= BandPropertyChanged;
        foreach (BandViewModel band in e.NewItems ?? Array.Empty<BandViewModel>())
            band.PropertyChanged += BandPropertyChanged;
        _bandCache.Clear();
        _bandCache.AddRange(Bands ?? []);
        _cacheVersion++;
        InvalidateVisual();
    }

    private void BandPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BandViewModel.IsSelected))
            _cacheVersion++;
        if (Dispatcher.CheckAccess())
            InvalidateVisual();
        else
            Dispatcher.BeginInvoke(InvalidateVisual);
    }

    private IReadOnlyList<BandViewModel> BandList() => _bandCache;

    private void DrawGrid(DrawingContext dc, Rect plot)
    {
        dc.DrawRectangle(null, _gridPen, plot);
        foreach (var hz in new[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
        {
            var x = XFromFrequency(hz, plot);
            dc.DrawLine(_gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            DrawText(dc, hz >= 1000 ? $"{hz / 1000}k" : hz.ToString(), new Point(x - 12, plot.Bottom + 8), 11);
        }
        for (var db = -18; db <= 12; db += 6)
        {
            var y = YFromGain(db, plot);
            dc.DrawLine(db == 0 ? _zeroPen : _gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(dc, db.ToString(), new Point(14, y - 8), 11);
        }
    }

    private void DrawResponse(DrawingContext dc, Rect plot)
    {
        var bands = BandList();
        if (bands.Count == 0)
            return;
        if (_cachedResponse is null || _cachedSize != RenderSize || _renderedVersion != _cacheVersion)
        {
            var response = EqGraphResponse.Calculate(bands, PreGain);
            _cachedBandResponses.Clear();
            foreach (var band in response.Bands)
                _cachedBandResponses.Add((band.BandIndex, CreateGeometry(band.MagnitudeDb, plot)));
            _cachedResponse = CreateGeometry(response.CombinedDb, plot);
            _cachedSize = RenderSize;
            _renderedVersion = _cacheVersion;
        }
        _individualResponsePen ??= CreateIndividualResponsePen(0.88, 1.1, selected: false);
        _selectedResponsePen ??= CreateIndividualResponsePen(1, 1.9, selected: true);
        foreach (var band in _cachedBandResponses)
            dc.DrawGeometry(null, band.BandIndex == SelectedBandIndex ? _selectedResponsePen : _individualResponsePen, band.Geometry);
        _responsePen ??= CreateResponsePen();
        dc.DrawGeometry(null, _responsePen, _cachedResponse);
    }

    private void DrawHandles(DrawingContext dc, Rect plot)
    {
        foreach (var band in BandList())
        {
            var point = new Point(XFromFrequency(band.Frequency, plot), YFromGain(band.Gain, plot));
            var selected = band.Index == SelectedBandIndex;
            var fill = band.Enabled
                ? ThemeBrush("ControlSolidFillColorDefaultBrush", SystemColors.WindowBrush)
                : _disabledHandleBrush;
            var outline = selected ? CreateAccentPen(2.5) : new Pen(ThemeBrush("TextFillColorSecondaryBrush", SystemColors.WindowTextBrush), 1.2);
            if (selected)
            {
                var guide = CreateAccentPen(1);
                guide.DashStyle = DashStyles.Dot;
                dc.DrawLine(guide, new Point(point.X, plot.Top), new Point(point.X, plot.Bottom));
                dc.DrawEllipse(null, CreateAccentPen(2), point, 14, 14);
            }
            dc.DrawEllipse(fill, outline, point, 10, 10);
            DrawCenteredText(dc, band.DisplayIndex.ToString(), point, 9.5,
                selected ? ThemeBrush("TextFillColorPrimaryBrush", SystemColors.WindowTextBrush) : ThemeBrush("TextFillColorSecondaryBrush", SystemColors.WindowTextBrush));
        }
    }

    private void DrawHoverReadout(DrawingContext dc, Rect plot)
    {
        if (_hoverPoint is not { } point || _hoverValue is not { } value)
            return;
        var guideBrush = ThemeBrush("TextFillColorSecondaryBrush", SystemColors.WindowTextBrush).Clone();
        guideBrush.Opacity = SystemParameters.HighContrast ? 0.8 : 0.28;
        var guide = new Pen(guideBrush, 1) { DashStyle = DashStyles.Dot };
        dc.DrawLine(guide, new Point(point.X, plot.Top), new Point(point.X, plot.Bottom));
        dc.DrawLine(guide, new Point(plot.Left, point.Y), new Point(plot.Right, point.Y));

        var text = EqGraphProjection.FormatReadout(value);
        var formatted = CreateFormattedText(text, 11, ThemeBrush("TextFillColorPrimaryBrush", SystemColors.WindowTextBrush));
        var width = formatted.Width + 18;
        var height = formatted.Height + 10;
        var left = Math.Clamp(point.X + 12, plot.Left + 4, plot.Right - width - 4);
        var top = Math.Clamp(point.Y - height - 10, plot.Top + 4, plot.Bottom - height - 4);
        var box = new Rect(left, top, width, height);
        dc.DrawRoundedRectangle(
            ThemeBrush("ControlSolidFillColorDefaultBrush", SystemColors.WindowBrush),
            new Pen(ThemeBrush("ControlStrokeColorDefaultBrush", SystemColors.WindowTextBrush), 1),
            box,
            5,
            5);
        dc.DrawText(formatted, new Point(left + 9, top + 5));
    }

    private void UpdateHover(Point pointer, Rect plot)
    {
        if (!plot.Contains(pointer))
        {
            if (_hoverPoint is not null)
            {
                _hoverPoint = null;
                _hoverValue = null;
                InvalidateVisual();
            }
            return;
        }

        var x = Math.Clamp(pointer.X, plot.Left, plot.Right);
        var y = Math.Clamp(pointer.Y, plot.Top, plot.Bottom);
        var value = EqGraphProjection.FromNormalized((x - plot.Left) / plot.Width, (y - plot.Top) / plot.Height);
        if (_hoverPoint == new Point(x, y) && _hoverValue == value)
            return;
        _hoverPoint = new Point(x, y);
        _hoverValue = value;
        InvalidateVisual();
    }

    private BandViewModel? HitTestBand(Point point)
    {
        var plot = new Rect(56, 18, ActualWidth - 76, ActualHeight - 54);
        return BandList().Where(b => b.Enabled)
            .Select(b => new { Band = b, Point = new Point(XFromFrequency(b.Frequency, plot), YFromGain(b.Gain, plot)) })
            .Where(x => (x.Point - point).Length <= 12)
            .OrderBy(x => (x.Point - point).Length)
            .Select(x => x.Band)
            .FirstOrDefault();
    }

    private static double XFromFrequency(double hz, Rect plot) => plot.Left + Math.Log(hz / 20, 1000) * plot.Width;

    private static int FrequencyFromX(double x, Rect plot) => (int)Math.Round(20 * Math.Pow(1000, (x - plot.Left) / plot.Width));

    private static double YFromGain(double gain, Rect plot) => plot.Top + (12 - gain) / 30 * plot.Height;

    private static double GainFromY(double y, Rect plot) => Math.Round((12 - (y - plot.Top) / plot.Height * 30) * 10) / 10;

    private void DrawText(DrawingContext dc, string text, Point point, double size)
    {
        var formatted = CreateFormattedText(text, size, ThemeBrush("TextFillColorSecondaryBrush", SystemColors.WindowTextBrush));
        dc.DrawText(formatted, point);
    }

    private void DrawCenteredText(DrawingContext dc, string text, Point center, double size, Brush brush)
    {
        var formatted = CreateFormattedText(text, size, brush);
        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private FormattedText CreateFormattedText(string text, double size, Brush brush) =>
        new(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Brush ThemeBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void EnsureThemeDrawingResources()
    {
        _gridPen ??= CreateThemePen(
            "DividerStrokeColorDefaultBrush",
            SystemColors.GrayTextBrush,
            SystemParameters.HighContrast ? 1 : 0.42,
            1);
        _zeroPen ??= CreateThemePen(
            "ControlStrokeColorDefaultBrush",
            SystemColors.WindowTextBrush,
            SystemParameters.HighContrast ? 1 : 0.68,
            1.25);
        _disabledHandleBrush ??= CreateThemeBrush(
            "ControlFillColorDisabledBrush",
            SystemColors.GrayTextBrush,
            SystemParameters.HighContrast ? 1 : 0.72);
    }

    private Pen CreateThemePen(string key, Brush fallback, double opacity, double thickness)
    {
        var pen = new Pen(CreateThemeBrush(key, fallback, opacity), thickness);
        pen.Freeze();
        return pen;
    }

    private Brush CreateThemeBrush(string key, Brush fallback, double opacity)
    {
        var brush = ThemeBrush(key, fallback).Clone();
        brush.Opacity *= opacity;
        brush.Freeze();
        return brush;
    }

    private static Pen CreateResponsePen()
    {
        var accent = SystemParameters.WindowGlassBrush.Clone();
        accent.Opacity = 0.95;
        var pen = new Pen(accent, 3);
        pen.Freeze();
        return pen;
    }

    private static Pen CreateIndividualResponsePen(double opacity, double thickness, bool selected)
    {
        var accent = SystemParameters.WindowGlassBrush.Clone();
        accent.Opacity = SystemParameters.HighContrast ? Math.Max(opacity, 0.75) : opacity;
        var pen = new Pen(accent, thickness);
        if (selected)
            pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        return pen;
    }

    private static Pen CreateAccentPen(double thickness)
    {
        var brush = SystemParameters.WindowGlassBrush.Clone();
        brush.Opacity = 0.95;
        return new Pen(brush, thickness);
    }

    private static StreamGeometry CreateGeometry(IReadOnlyList<double> values, Rect plot)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var ratio = i / (values.Count - 1.0);
                var point = new Point(
                    plot.Left + ratio * plot.Width,
                    YFromGain(Math.Clamp(values[i], -18, 12), plot));
                if (i == 0)
                    ctx.BeginFigure(point, false, false);
                else
                    ctx.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }
}

internal sealed class EqGraphAutomationPeer(EqGraph owner) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => nameof(EqGraph);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    protected override string GetNameCore() => "Parametric equalizer response graph";
    protected override string GetHelpTextCore() => "Use Control plus Left or Right to select a band; Left or Right changes frequency, Up or Down changes gain, and Page Up or Page Down changes Q. Hold Shift for larger steps.";
    protected override bool IsKeyboardFocusableCore() => true;
}
