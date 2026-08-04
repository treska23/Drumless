using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Controls;

/// <summary>
/// Escenario de falsa perspectiva inspirado en una carretera/túnel musical.
/// Se dibuja en un único FrameworkElement para no crear cientos de controles WPF.
/// </summary>
public sealed class DrumJourneyVisualizer : FrameworkElement
{
    private const double PulseLifetimeSeconds = 1.25d;

    private static readonly Brush BackgroundBrush = CreateGradient(
        Color.FromRgb(5, 8, 16),
        Color.FromRgb(13, 17, 29));
    private static readonly Brush RoadBrush = CreateSolid(Color.FromRgb(14, 20, 34));
    private static readonly Brush RoadGlowBrush = CreateSolid(Color.FromArgb(34, 82, 216, 255));
    private static readonly Brush GridBrush = CreateSolid(Color.FromArgb(115, 74, 145, 181));
    private static readonly Brush TextBrush = CreateSolid(Color.FromRgb(236, 242, 250));
    private static readonly Brush MutedTextBrush = CreateSolid(Color.FromRgb(147, 160, 181));
    private static readonly Brush HudBrush = CreateSolid(Color.FromArgb(188, 7, 11, 19));
    private static readonly Brush LockedBrush = CreateSolid(Color.FromRgb(92, 232, 174));
    private static readonly Brush SearchingBrush = CreateSolid(Color.FromRgb(244, 190, 92));
    private static readonly Brush OffBeatBrush = CreateSolid(Color.FromRgb(239, 104, 116));
    private static readonly Brush PerfectBrush = CreateSolid(Color.FromRgb(228, 250, 255));
    private static readonly Brush OnTimeBrush = CreateSolid(Color.FromRgb(103, 228, 179));
    private static readonly Brush AcceptableBrush = CreateSolid(Color.FromRgb(248, 193, 88));
    private static readonly Brush KickBrush = CreateSolid(Color.FromRgb(93, 192, 255));
    private static readonly Brush SnareBrush = CreateSolid(Color.FromRgb(244, 116, 181));
    private static readonly Brush HiHatBrush = CreateSolid(Color.FromRgb(255, 215, 108));
    private static readonly Brush TomBrush = CreateSolid(Color.FromRgb(148, 120, 255));
    private static readonly Brush CymbalBrush = CreateSolid(Color.FromRgb(105, 235, 210));
    private static readonly Brush OtherBrush = CreateSolid(Color.FromRgb(201, 211, 226));
    private static readonly Pen RoadPen = CreatePen(Color.FromArgb(175, 84, 190, 232), 1.25d);
    private static readonly Pen GridPen = CreatePen(Color.FromArgb(120, 75, 142, 177), 1d);

    private readonly List<JourneyPulse> _pulses = [];
    private DrumJourneyState _state = new(
        0d,
        false,
        false,
        0d,
        false,
        0,
        0,
        0,
        1,
        0d,
        "SIN TEMPO · analiza o introduce el BPM para puntuar");
    private TimeSpan _lastRenderingTime;
    private double _sceneTime;
    private bool _renderingAttached;

    public DrumJourneyVisualizer()
    {
        MinHeight = 250d;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = false;
        AutomationProperties.SetName(
            this,
            "Viaje rítmico tridimensional que reacciona a los golpes MIDI");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void PushHit(DrumJourneyHitEvent hit)
    {
        VerifyAccess();
        _pulses.Add(new JourneyPulse(hit, 0d));
        if (_pulses.Count > 120)
        {
            _pulses.RemoveRange(0, _pulses.Count - 120);
        }
        InvalidateVisual();
    }

    public void UpdateState(DrumJourneyState state)
    {
        VerifyAccess();
        if (Math.Abs(state.PositionSeconds - _state.PositionSeconds) > 0.75d ||
            state.PositionSeconds < _state.PositionSeconds - 0.1d)
        {
            _sceneTime = state.PositionSeconds;
            _pulses.Clear();
        }
        _state = state;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 40d || height < 80d)
        {
            return;
        }

        drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(0d, 0d, width, height));
        DrawAtmosphere(drawingContext, width, height);
        DrawRoad(drawingContext, width, height);
        DrawPulses(drawingContext, width, height);
        DrawHud(drawingContext, width, height);
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_renderingAttached)
        {
            return;
        }
        _renderingAttached = true;
        _lastRenderingTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        if (!_renderingAttached)
        {
            return;
        }
        _renderingAttached = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs eventArgs)
    {
        if (eventArgs is not RenderingEventArgs rendering)
        {
            return;
        }

        if (_lastRenderingTime == TimeSpan.Zero)
        {
            _lastRenderingTime = rendering.RenderingTime;
            return;
        }

        var delta = Math.Clamp(
            (rendering.RenderingTime - _lastRenderingTime).TotalSeconds,
            0d,
            0.10d);
        _lastRenderingTime = rendering.RenderingTime;
        if (_state.IsPlaying)
        {
            _sceneTime += delta;
        }

        for (var index = _pulses.Count - 1; index >= 0; index--)
        {
            var pulse = _pulses[index];
            pulse = pulse with { AgeSeconds = pulse.AgeSeconds + delta };
            if (pulse.AgeSeconds >= PulseLifetimeSeconds)
            {
                _pulses.RemoveAt(index);
            }
            else
            {
                _pulses[index] = pulse;
            }
        }

        InvalidateVisual();
    }

    private void DrawAtmosphere(DrawingContext drawingContext, double width, double height)
    {
        var horizonY = height * 0.24d;
        var horizonGlow = new RadialGradientBrush(
            Color.FromArgb(_state.TempoLocked ? (byte)115 : (byte)70, 68, 180, 226),
            Color.FromArgb(0, 5, 8, 16))
        {
            RadiusX = 0.62d,
            RadiusY = 0.30d,
            Center = new Point(0.5d, 0.26d),
            GradientOrigin = new Point(0.5d, 0.26d)
        };
        drawingContext.DrawEllipse(
            horizonGlow,
            null,
            new Point(width * 0.5d, horizonY),
            width * 0.48d,
            height * 0.26d);

        var speed = ResolveVisualSpeed();
        var starPhase = _sceneTime * speed;
        for (var index = 0; index < 34; index++)
        {
            var seedX = Fraction(index * 0.61803398875d);
            var seedY = Fraction(index * 0.371d + starPhase * (0.035d + index % 5 * 0.006d));
            var side = index % 2 == 0 ? -1d : 1d;
            var x = width * 0.5d + side * width * (0.16d + seedX * 0.36d);
            var y = horizonY + seedY * (height - horizonY);
            var size = 0.7d + seedY * 2.2d;
            drawingContext.PushOpacity(0.18d + seedY * 0.42d);
            drawingContext.DrawEllipse(GridBrush, null, new Point(x, y), size, size);
            drawingContext.Pop();
        }
    }

    private void DrawRoad(DrawingContext drawingContext, double width, double height)
    {
        var horizonY = height * 0.24d;
        var bottomY = height * 0.98d;
        var centerX = width * 0.5d;
        var farHalfWidth = Math.Max(14d, width * 0.035d);
        var nearHalfWidth = width * 0.47d;

        var road = new StreamGeometry();
        using (var context = road.Open())
        {
            context.BeginFigure(
                new Point(centerX - farHalfWidth, horizonY),
                isFilled: true,
                isClosed: true);
            context.LineTo(new Point(centerX + farHalfWidth, horizonY), true, false);
            context.LineTo(new Point(centerX + nearHalfWidth, bottomY), true, false);
            context.LineTo(new Point(centerX - nearHalfWidth, bottomY), true, false);
        }
        road.Freeze();
        drawingContext.DrawGeometry(RoadBrush, RoadPen, road);

        drawingContext.PushOpacity(_state.TempoLocked ? 0.20d : 0.10d);
        drawingContext.DrawGeometry(RoadGlowBrush, null, road);
        drawingContext.Pop();

        foreach (var lane in new[] { -0.62d, -0.31d, 0d, 0.31d, 0.62d })
        {
            drawingContext.PushOpacity(lane == 0d ? 0.52d : 0.28d);
            drawingContext.DrawLine(
                GridPen,
                new Point(centerX + farHalfWidth * lane, horizonY),
                new Point(centerX + nearHalfWidth * lane, bottomY));
            drawingContext.Pop();
        }

        var speed = ResolveVisualSpeed();
        var phase = Fraction(_sceneTime * speed * 0.72d);
        const int crossLineCount = 17;
        for (var index = 0; index < crossLineCount; index++)
        {
            var depth = Fraction(index / (double)crossLineCount + phase);
            var perspective = Math.Pow(depth, 2.25d);
            var y = horizonY + (bottomY - horizonY) * perspective;
            var halfWidth = farHalfWidth + (nearHalfWidth - farHalfWidth) * Math.Pow(depth, 1.45d);
            var opacity = 0.12d + perspective * 0.58d;
            drawingContext.PushOpacity(opacity);
            drawingContext.DrawLine(
                GridPen,
                new Point(centerX - halfWidth, y),
                new Point(centerX + halfWidth, y));
            drawingContext.Pop();
        }

        if (_state.HasTempo && _state.Bpm > 0d)
        {
            var beatPhase = Fraction(_state.PositionSeconds * _state.Bpm / 60d);
            var beatGlow = Math.Exp(-beatPhase * 12d) * (_state.TempoLocked ? 0.16d : 0.07d);
            drawingContext.PushOpacity(beatGlow);
            drawingContext.DrawRectangle(PerfectBrush, null, new Rect(0d, 0d, width, height));
            drawingContext.Pop();
        }
    }

    private void DrawPulses(DrawingContext drawingContext, double width, double height)
    {
        var horizonY = height * 0.24d;
        var bottomY = height * 0.96d;
        var centerX = width * 0.5d;
        var farHalfWidth = Math.Max(14d, width * 0.035d);
        var nearHalfWidth = width * 0.47d;

        foreach (var pulse in _pulses)
        {
            var progress = Math.Clamp(pulse.AgeSeconds / PulseLifetimeSeconds, 0d, 1d);
            var depth = Math.Clamp(0.64d + progress * 0.36d, 0d, 1d);
            var perspective = Math.Pow(depth, 2.18d);
            var y = horizonY + (bottomY - horizonY) * perspective;
            var halfWidth = farHalfWidth + (nearHalfWidth - farHalfWidth) * Math.Pow(depth, 1.42d);
            var lane = ResolveLane(pulse.Hit.Instrument);
            var x = centerX + lane * halfWidth * 0.78d;
            var velocityScale = 0.72d + pulse.Hit.Velocity / 127d * 0.90d;
            var size = (9d + perspective * 25d) * velocityScale;
            var opacity = Math.Pow(1d - progress, 0.68d);
            var instrumentBrush = ResolveInstrumentBrush(pulse.Hit.Instrument);
            var judgementBrush = ResolveJudgementBrush(pulse.Hit.Judgement);

            drawingContext.PushOpacity(opacity * 0.24d);
            drawingContext.DrawEllipse(
                instrumentBrush,
                null,
                new Point(x, y),
                size * 1.85d,
                size * 1.85d);
            drawingContext.Pop();

            drawingContext.PushOpacity(opacity);
            DrawInstrumentShape(
                drawingContext,
                pulse.Hit.Instrument,
                new Point(x, y),
                size,
                instrumentBrush,
                judgementBrush);
            drawingContext.Pop();

            if (pulse.Hit.Judgement is DrumJourneyJudgement.Perfect or DrumJourneyJudgement.OnTime)
            {
                var ringProgress = Math.Clamp(progress * 1.8d, 0d, 1d);
                var ringRadius = size * (1.05d + ringProgress * 2.2d);
                drawingContext.PushOpacity((1d - ringProgress) * 0.72d);
                drawingContext.DrawEllipse(
                    null,
                    new Pen(judgementBrush, pulse.Hit.Judgement == DrumJourneyJudgement.Perfect ? 2.4d : 1.5d),
                    new Point(x, y),
                    ringRadius,
                    ringRadius * 0.58d);
                drawingContext.Pop();
            }
        }
    }

    private void DrawHud(DrawingContext drawingContext, double width, double height)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        drawingContext.DrawRectangle(HudBrush, null, new Rect(0d, 0d, width, 64d));
        drawingContext.DrawRectangle(HudBrush, null, new Rect(0d, height - 47d, width, 47d));

        DrawText(
            drawingContext,
            "VIAJE RÍTMICO · PROTOTIPO",
            15d,
            TextBrush,
            new Point(16d, 10d),
            pixelsPerDip,
            FontWeights.SemiBold);
        DrawText(
            drawingContext,
            _state.Status,
            11.5d,
            _state.TempoLocked ? LockedBrush : _state.HasTempo ? SearchingBrush : MutedTextBrush,
            new Point(16d, 35d),
            pixelsPerDip,
            FontWeights.Medium);

        var scoreText = $"SCORE  {_state.Score:000000}";
        var score = CreateText(scoreText, 18d, TextBrush, pixelsPerDip, FontWeights.SemiBold);
        drawingContext.DrawText(score, new Point(Math.Max(16d, width - score.Width - 17d), 12d));

        var bottomY = height - 34d;
        DrawText(
            drawingContext,
            $"RACHA  {_state.Streak}",
            13d,
            TextBrush,
            new Point(16d, bottomY),
            pixelsPerDip,
            FontWeights.SemiBold);
        DrawText(
            drawingContext,
            $"×{_state.Multiplier}",
            17d,
            _state.Multiplier > 1 ? LockedBrush : MutedTextBrush,
            new Point(112d, bottomY - 3d),
            pixelsPerDip,
            FontWeights.Bold);
        DrawText(
            drawingContext,
            _state.TempoLocked
                ? $"PRECISIÓN  {_state.AccuracyPercent:0}%"
                : _state.HasTempo
                    ? "Mantén un pulso estable para enlazar el tempo"
                    : "El escenario reacciona aunque todavía no haya puntuación",
            11.5d,
            _state.TempoLocked ? LockedBrush : MutedTextBrush,
            new Point(Math.Min(width * 0.36d, 190d), bottomY + 1d),
            pixelsPerDip,
            FontWeights.Medium);
    }

    private static void DrawInstrumentShape(
        DrawingContext drawingContext,
        DrumJourneyInstrument instrument,
        Point center,
        double size,
        Brush fill,
        Brush outline)
    {
        var pen = new Pen(outline, 1.8d);
        switch (instrument)
        {
            case DrumJourneyInstrument.Kick:
                drawingContext.DrawEllipse(fill, pen, center, size, size * 0.72d);
                drawingContext.DrawEllipse(null, pen, center, size * 0.42d, size * 0.42d);
                break;
            case DrumJourneyInstrument.Snare:
            {
                var diamond = new StreamGeometry();
                using var context = diamond.Open();
                context.BeginFigure(new Point(center.X, center.Y - size), true, true);
                context.LineTo(new Point(center.X + size, center.Y), true, false);
                context.LineTo(new Point(center.X, center.Y + size), true, false);
                context.LineTo(new Point(center.X - size, center.Y), true, false);
                drawingContext.DrawGeometry(fill, pen, diamond);
                break;
            }
            case DrumJourneyInstrument.HiHat:
                drawingContext.DrawLine(pen, new Point(center.X - size, center.Y), new Point(center.X + size, center.Y));
                drawingContext.DrawLine(pen, new Point(center.X - size * 0.74d, center.Y - size * 0.40d), new Point(center.X + size * 0.74d, center.Y + size * 0.40d));
                drawingContext.DrawLine(pen, new Point(center.X - size * 0.74d, center.Y + size * 0.40d), new Point(center.X + size * 0.74d, center.Y - size * 0.40d));
                break;
            case DrumJourneyInstrument.Tom:
                drawingContext.DrawRoundedRectangle(
                    fill,
                    pen,
                    new Rect(center.X - size, center.Y - size * 0.68d, size * 2d, size * 1.36d),
                    size * 0.28d,
                    size * 0.28d);
                break;
            case DrumJourneyInstrument.Cymbal:
                drawingContext.DrawEllipse(fill, pen, center, size * 1.18d, size * 0.48d);
                drawingContext.DrawLine(pen, new Point(center.X, center.Y - size * 0.80d), new Point(center.X, center.Y + size * 0.80d));
                break;
            default:
            {
                var triangle = new StreamGeometry();
                using var context = triangle.Open();
                context.BeginFigure(new Point(center.X, center.Y - size), true, true);
                context.LineTo(new Point(center.X + size, center.Y + size), true, false);
                context.LineTo(new Point(center.X - size, center.Y + size), true, false);
                drawingContext.DrawGeometry(fill, pen, triangle);
                break;
            }
        }
    }

    private double ResolveVisualSpeed()
    {
        if (!_state.IsPlaying)
        {
            return 0d;
        }
        return _state.HasTempo && _state.Bpm > 0d
            ? Math.Clamp(_state.Bpm / 120d, 0.55d, 1.85d)
            : 0.72d;
    }

    private static double ResolveLane(DrumJourneyInstrument instrument) => instrument switch
    {
        DrumJourneyInstrument.HiHat => -0.58d,
        DrumJourneyInstrument.Snare => -0.22d,
        DrumJourneyInstrument.Kick => 0d,
        DrumJourneyInstrument.Tom => 0.27d,
        DrumJourneyInstrument.Cymbal => 0.60d,
        _ => 0.44d
    };

    private static Brush ResolveInstrumentBrush(DrumJourneyInstrument instrument) => instrument switch
    {
        DrumJourneyInstrument.Kick => KickBrush,
        DrumJourneyInstrument.Snare => SnareBrush,
        DrumJourneyInstrument.HiHat => HiHatBrush,
        DrumJourneyInstrument.Tom => TomBrush,
        DrumJourneyInstrument.Cymbal => CymbalBrush,
        _ => OtherBrush
    };

    private static Brush ResolveJudgementBrush(DrumJourneyJudgement judgement) => judgement switch
    {
        DrumJourneyJudgement.Perfect => PerfectBrush,
        DrumJourneyJudgement.OnTime => OnTimeBrush,
        DrumJourneyJudgement.Acceptable => AcceptableBrush,
        DrumJourneyJudgement.OffBeat => OffBeatBrush,
        _ => OtherBrush
    };

    private static void DrawText(
        DrawingContext drawingContext,
        string text,
        double fontSize,
        Brush brush,
        Point origin,
        double pixelsPerDip,
        FontWeight weight) =>
        drawingContext.DrawText(
            CreateText(text, fontSize, brush, pixelsPerDip, weight),
            origin);

    private static FormattedText CreateText(
        string text,
        double fontSize,
        Brush brush,
        double pixelsPerDip,
        FontWeight weight) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            fontSize,
            brush,
            pixelsPerDip);

    private static double Fraction(double value) => value - Math.Floor(value);

    private static Brush CreateSolid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush CreateGradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, new Point(0.5d, 0d), new Point(0.5d, 1d));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(CreateSolid(color), thickness);
        pen.Freeze();
        return pen;
    }

    private sealed record JourneyPulse(
        DrumJourneyHitEvent Hit,
        double AgeSeconds);
}
