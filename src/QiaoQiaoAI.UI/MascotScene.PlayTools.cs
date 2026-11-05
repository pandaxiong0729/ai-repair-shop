using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QiaoQiaoAI.UI;

/// <summary>Original vector props and local face deformation; no extra image downloads are needed.</summary>
internal sealed partial class MascotScene
{
    private static readonly Brush Coral = VentSurface.Brush("#E97479");
    private static readonly Brush RedInk = VentSurface.Brush("#C64B60");
    private static readonly Brush PaperWhite = VentSurface.Brush("#FFFCF0");
    private static readonly Brush Sky = VentSurface.Brush("#97CFDD");
    private static readonly Geometry CrumpledPaper = PaperShape(
        "M -23,-6 Q -25,-10 -20,-14 Q -19,-18 -14,-17 L -11,-23 Q -6,-21 -3,-22 " +
        "Q 2,-25 7,-21 Q 10,-18 15,-19 Q 20,-18 19,-12 Q 23,-10 23,-5 " +
        "Q 21,-1 23,3 Q 25,8 20,11 Q 19,17 15,18 L 13,22 Q 8,20 4,23 " +
        "Q 0,26 -5,22 Q -10,24 -13,20 Q -19,20 -18,14 Q -24,12 -22,7 " +
        "Q -25,3 -22,-1 Q -20,-4 -23,-6 Z");
    private static readonly Geometry PaperCreases = PaperShape(
        "M -19,-11 Q -16,-9 -15,-5 C -14,-2 -9,-4 -8,-1 " +
        "M -10,-20 Q -12,-14 -8,-12 Q -6,-10 -7,-6 " +
        "M -6,-17 Q -1,-16 0,-12 Q 1,-8 6,-9 " +
        "M 3,-21 Q 2,-16 7,-14 Q 11,-12 9,-8 " +
        "M 17,-15 Q 13,-11 14,-7 Q 15,-4 11,-2 " +
        "M 21,-5 Q 17,-7 16,-2 Q 14,1 17,4 " +
        "M -20,2 Q -16,0 -14,4 C -12,7 -8,3 -5,5 " +
        "M -13,-7 Q -9,-9 -5,-5 Q -3,-2 1,-4 " +
        "M -3,-10 Q -5,-6 -1,-3 Q 3,0 1,4 " +
        "M 7,-5 Q 3,-2 7,2 Q 9,4 6,8 " +
        "M -16,11 Q -11,12 -10,9 Q -8,7 -4,8 " +
        "M -6,0 Q -8,3 -5,7 Q -1,11 -5,14 " +
        "M -12,18 Q -9,14 -5,17 Q -3,19 0,18 " +
        "M -1,9 Q 2,7 5,12 Q 8,15 12,12 " +
        "M 15,6 Q 10,8 13,11 Q 15,14 12,17 " +
        "M 4,22 Q 3,19 6,17 Q 8,15 6,13");
    private static readonly Geometry PaperWrinkles = PaperShape(
        "M -19,-6 Q -17,-4 -18,-1 M -18,-10 Q -18,-12 -16,-13 M -13,-14 Q -11,-12 -12,-10 " +
        "M -7,-19 Q -5,-17 -6,-15 M -3,-13 Q -1,-12 -1,-10 M 5,-18 Q 7,-17 8,-18 " +
        "M 11,-16 Q 10,-13 12,-11 M 15,-11 Q 17,-9 16,-7 M 19,-3 Q 18,-1 20,0 " +
        "M -12,-4 Q -10,-2 -11,0 M -7,-8 Q -5,-7 -4,-7 M -2,-7 Q -1,-5 1,-5 " +
        "M 3,-10 Q 5,-8 4,-7 M 10,-6 Q 11,-3 10,-2 M 4,-1 Q 3,1 5,2 " +
        "M -19,5 Q -17,7 -15,6 M -14,1 Q -12,2 -13,3 M -9,1 Q -10,3 -8,4 " +
        "M -3,1 Q -2,3 -3,5 M 0,6 Q 2,5 3,7 M 9,3 Q 11,4 10,6 " +
        "M 17,7 Q 19,9 17,10 M 13,3 Q 15,2 15,0 M -17,13 Q -15,13 -14,16 " +
        "M -10,12 Q -8,11 -7,13 M -7,9 Q -6,11 -7,12 M -3,12 Q -2,15 0,14 " +
        "M 3,12 Q 3,14 5,15 M 9,10 Q 11,9 11,7 M 8,19 Q 10,18 10,16 " +
        "M -8,19 Q -6,18 -5,20 M -2,21 Q -1,19 1,19");
    private static readonly (Geometry Shape, Brush Fill)[] PaperFolds =
    [
        (PaperShape("M -20,-12 Q -18,-7 -15,-5 Q -12,-2 -8,-1 Q -16,-1 -18,-5 Z"), PaperGradient("#FFFFFF", "#CED3D0", 115)),
        (PaperShape("M -10,-20 Q -12,-14 -8,-12 Q -6,-10 -7,-6 Q -11,-7 -12,-11 Z"), PaperGradient("#FFFFFF", "#E1E3DE", 5)),
        (PaperShape("M -6,-17 Q -1,-16 0,-12 Q 1,-8 6,-9 Q 2,-5 -1,-10 Z"), PaperGradient("#FFFFFF", "#D9DDD8", 85)),
        (PaperShape("M 3,-21 Q 2,-16 7,-14 Q 11,-12 9,-8 Q 15,-12 9,-16 Z"), PaperGradient("#FDFDF9", "#CDD3CF", 160)),
        (PaperShape("M 17,-15 Q 13,-11 14,-7 Q 15,-4 11,-2 Q 20,-5 18,-10 Z"), PaperGradient("#FFFFFF", "#D6DBD5", 25)),
        (PaperShape("M -20,2 Q -16,0 -14,4 Q -12,7 -5,5 Q -10,10 -16,6 Z"), PaperGradient("#FFFFFF", "#D4DAD4", 85)),
        (PaperShape("M -13,-7 Q -9,-9 -5,-5 Q -3,-2 1,-4 Q -3,2 -7,-3 Z"), PaperGradient("#FFFFFF", "#E0E3DC", 100)),
        (PaperShape("M -3,-10 Q -5,-6 -1,-3 Q 3,0 1,4 Q 7,0 3,-4 Z"), PaperGradient("#FFFFFF", "#CFD5CF", 155)),
        (PaperShape("M 7,-5 Q 3,-2 7,2 Q 9,4 6,8 Q 12,5 10,1 Z"), PaperGradient("#FFFFFF", "#D5DAD4", 35)),
        (PaperShape("M -6,0 Q -8,3 -5,7 Q -1,11 -5,14 Q 2,12 -1,7 Z"), PaperGradient("#FFFFFF", "#D4DAD3", 140)),
        (PaperShape("M -16,11 Q -11,12 -10,9 Q -8,7 -4,8 Q -9,12 -11,15 Z"), PaperGradient("#FFFFFC", "#CED5CF", 70)),
        (PaperShape("M -12,18 Q -9,14 -5,17 Q -3,19 0,18 Q -3,23 -7,19 Z"), PaperGradient("#FFFFFF", "#D3D9D1", 110)),
        (PaperShape("M -1,9 Q 2,7 5,12 Q 8,15 12,12 Q 8,20 4,15 Z"), PaperGradient("#FFFFFF", "#CED6CE", 75)),
        (PaperShape("M 15,6 Q 10,8 13,11 Q 15,14 12,17 Q 19,13 17,9 Z"), PaperGradient("#FFFFFF", "#C9D1C9", 20))
    ];
    private static readonly Geometry PaperCurls = PaperShape(
        "M -14,-17 Q -12,-20 -11,-23 Q -7,-21 -7,-17 Q -10,-20 -14,-17 Z " +
        "M 20,11 Q 18,14 15,18 Q 20,17 22,13 Q 23,10 20,11 Z " +
        "M -18,14 Q -19,18 -13,20 Q -15,17 -16,16 Z");
    private static readonly Brush PaperBody = CreatePaperBody();
    private static readonly Geometry StampGlyph = CreateStampGlyph();

    private static Geometry PaperShape(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    private static Brush PaperGradient(string light, string shade, double angle)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(light),
            (Color)ColorConverter.ConvertFromString(shade), angle);
        brush.Freeze();
        return brush;
    }

    private static Brush CreatePaperBody()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(.4, .35), GradientOrigin = new Point(.25, .2), RadiusX = .8, RadiusY = .8,
            GradientStops = new GradientStopCollection
            {
                new(Colors.White, 0), new((Color)ColorConverter.ConvertFromString("#F5F5EF"), .6),
                new((Color)ColorConverter.ConvertFromString("#CBD2C9"), 1)
            }
        };
        brush.Freeze();
        return brush;
    }

    private static Geometry CreateStampGlyph()
    {
        var formatted = new FormattedText("滚", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight, new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
                FontWeights.Bold, FontStretches.Normal), 68, RedInk, 1);
        var geometry = formatted.BuildGeometry(new Point());
        var bounds = geometry.Bounds;
        geometry.Transform = new TranslateTransform(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2);
        geometry.Freeze();
        return geometry;
    }
    private bool _pinching;
    private double _pinchReturnAge = 1;
    private Point _pinchStart;
    private Point _pinchAnchor;
    private Vector _pinchOffset;
    private Vector _pinchReleaseOffset;
    private string _replyText = "这段回答没有回答我的问题。";
    private readonly Dictionary<BitmapSource, byte[]> _sourcePixels = [];
    private WriteableBitmap? _warpBitmap;
    private byte[]? _warpOutput;

    public string ReplyText
    {
        get => _replyText;
        set => _replyText = string.IsNullOrWhiteSpace(value) ? "这段回答没有回答我的问题。" : value[..Math.Min(120, value.Length)];
    }
    public bool IsPinching => _pinching;
    public bool IsPinchReturning => !_pinching && _pinchReturnAge < .95;
    public Vector PinchDisplacement => _pinchOffset;

    public void BeginPinch(Point position)
    {
        if (!HasSkin || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return;
        CancelPinch();
        var rect = CharacterRect;
        _pinching = true;
        _pinchStart = position;
        // Keep the neck and hairline anchored while pulling the nearest cheek.
        _pinchAnchor = new Point(rect.X + rect.Width * (position.X < 230 ? .33 : .67), rect.Y + rect.Height * .59);
        _lastTool = InteractionTool.Pinch;
        _sinceHit = 0;
        InvalidateVisual();
    }

    public void UpdatePinch(Point position)
    {
        if (!_pinching || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return;
        var offset = position - _pinchStart;
        offset.Y = Math.Clamp(offset.Y, -24, 24);
        if (offset.Length > 56) offset *= 56 / offset.Length;
        var rect = CharacterRect;
        // For F(p)=p+offset*w(p), det(DF)=1+offset·grad(w). This global bound keeps
        // det >= .1 for horizontal, vertical, diagonal and reversed spring motion.
        var minSide = Math.Min(_pinchAnchor.X - rect.Left, rect.Right - _pinchAnchor.X);
        var gradientBound = Math.Abs(offset.X) / minSide + Math.Abs(offset.Y) * Math.PI / 120;
        if (gradientBound > .9) offset *= .9 / gradientBound;
        _pinchOffset = offset;
        InvalidateVisual();
    }

    public void ReleasePinch()
    {
        if (!_pinching) return;
        _pinching = false;
        _pinchReleaseOffset = _pinchOffset;
        _pinchReturnAge = 0;
        Hit(InteractionTool.Pinch, _pinchAnchor);
    }

    public void CancelPinch()
    {
        _pinching = false;
        _pinchReturnAge = 1;
        _pinchOffset = _pinchReleaseOffset = default;
        InvalidateVisual();
    }

    private void StartAutomaticPinch(Point position)
    {
        BeginPinch(position);
        UpdatePinch(new Point(position.X + (position.X < 230 ? -42 : 42), position.Y - 8));
        _pinching = false;
        _pinchReleaseOffset = _pinchOffset;
        _pinchReturnAge = 0;
    }

    private void AdvancePinch(double seconds)
    {
        if (_pinching) { _sinceHit = 0; return; }
        if (!IsPinchReturning) return;
        _pinchReturnAge = Math.Min(1, _pinchReturnAge + seconds);
        _pinchOffset = _pinchReturnAge >= .95 ? default
            : _pinchReleaseOffset * (Math.Cos(_pinchReturnAge * 22) * Math.Exp(-_pinchReturnAge * 5.5));
    }

    private void DrawCharacter(DrawingContext dc, BitmapSource frame, Rect rect)
    {
        if (_pinchOffset.Length < .05) { dc.DrawImage(frame, rect); return; }
        // Inverse sampling uses one continuous surface, so no triangle clip seams can appear.
        // Reuse pixel buffers; generated frames stay in memory and never change the supplied artwork.
        if (!_sourcePixels.TryGetValue(frame, out var input))
        {
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            input = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            converted.CopyPixels(input, frame.PixelWidth * 4, 0);
            _sourcePixels.Add(frame, input);
        }
        const int width = 320;
        var height = (int)Math.Round(width * rect.Height / rect.Width);
        if (_warpBitmap is null || _warpBitmap.PixelHeight != height)
        {
            _warpBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            _warpOutput = new byte[width * height * 4];
        }
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var target = new Point(rect.X + (x + .5) / width * rect.Width, rect.Y + (y + .5) / height * rect.Height);
            var source = target;
            for (var iteration = 0; iteration < 8; iteration++)
            {
                WeightAndGradient(source, rect, out var weight, out var gradient);
                var residual = source + _pinchOffset * weight - target;
                if (residual.LengthSquared < .0001) break;
                var determinant = 1 + Vector.Multiply(_pinchOffset, gradient);
                source -= residual - _pinchOffset * (Vector.Multiply(gradient, residual) / determinant);
                source.X = Math.Clamp(source.X, rect.Left, rect.Right);
                source.Y = Math.Clamp(source.Y, rect.Top, rect.Bottom);
            }
            var sx = Math.Clamp((source.X - rect.X) / rect.Width * frame.PixelWidth - .5, 0, frame.PixelWidth - 1);
            var sy = Math.Clamp((source.Y - rect.Y) / rect.Height * frame.PixelHeight - .5, 0, frame.PixelHeight - 1);
            var ix = (int)sx; var iy = (int)sy;
            var right = Math.Min(ix + 1, frame.PixelWidth - 1);
            var below = Math.Min(iy + 1, frame.PixelHeight - 1);
            var fx = sx - ix; var fy = sy - iy;
            for (var channel = 0; channel < 4; channel++)
            {
                var upper = input[(iy * frame.PixelWidth + ix) * 4 + channel] * (1 - fx) + input[(iy * frame.PixelWidth + right) * 4 + channel] * fx;
                var lower = input[(below * frame.PixelWidth + ix) * 4 + channel] * (1 - fx) + input[(below * frame.PixelWidth + right) * 4 + channel] * fx;
                _warpOutput![(y * width + x) * 4 + channel] = (byte)Math.Clamp(Math.Round(upper * (1 - fy) + lower * fy), 0, 255);
            }
        }
        _warpBitmap.WritePixels(new Int32Rect(0, 0, width, height), _warpOutput!, width * 4, 0);
        dc.DrawImage(_warpBitmap, rect);
    }

    private void WeightAndGradient(Point point, Rect rect, out double weight, out Vector gradient)
    {
        var ny = (point.Y - _pinchAnchor.Y) / 60;
        if (Math.Abs(ny) >= 1) { weight = 0; gradient = default; return; }
        var left = point.X <= _pinchAnchor.X;
        var side = left ? _pinchAnchor.X - rect.Left : rect.Right - _pinchAnchor.X;
        var across = Math.Clamp((left ? point.X - rect.Left : rect.Right - point.X) / side, 0, 1);
        var vertical = .5 * (1 + Math.Cos(ny * Math.PI));
        weight = across * vertical;
        gradient = new Vector((left ? 1 : -1) * vertical / side, -across * Math.Sin(ny * Math.PI) * Math.PI / 120);
    }

    private void DrawPinchGrip(DrawingContext dc)
    {
        if (!_pinching) return;
        var point = _pinchAnchor + _pinchOffset;
        dc.DrawEllipse(Gold, new Pen(DarkGold, 1), point + new Vector(-5, -3), 5, 8);
        dc.DrawEllipse(Gold, new Pen(DarkGold, 1), point + new Vector(5, 3), 5, 8);
        DrawText(dc, "松手弹回", 11, Plum, new Point(175, 13));
    }

    private void AddPlayTransform(TransformGroup transforms)
    {
        var t = _sinceHit;
        if (_lastTool == InteractionTool.Jelly && t < 1.5)
        {
            var squash = t < .2 ? Math.Sin(t / .2 * Math.PI / 2) : t < .4 ? 1
                : Math.Cos((t - .4) * 16) * Math.Exp(-(t - .4) * 5);
            transforms.Children.Add(new ScaleTransform(1 + squash * .43, 1 - squash * .72, 230, 198));
        }
        if (_lastTool == InteractionTool.SpringGlove && t is >= .16 and < .75)
        {
            var force = Math.Sin((t - .16) / .59 * Math.PI) * Math.Exp(-(t - .16) * 4);
            transforms.Children.Add(new RotateTransform(force * 17, 230, 190));
            transforms.Children.Add(new TranslateTransform(force * 17, 0));
        }
    }

    private static void DrawPlayEffect(DrawingContext dc, Impact effect)
    {
        dc.PushOpacity(Math.Clamp((effect.Lifetime - effect.Age) / .35, 0, 1));
        switch (effect.Tool)
        {
            case InteractionTool.PaperReject: DrawPaperReject(dc, effect); break;
            case InteractionTool.Stamp: DrawStamp(dc, effect); break;
            case InteractionTool.Pinch: DrawPinchSnap(dc, effect); break;
            case InteractionTool.SpringGlove: DrawSpringGlove(dc, effect); break;
            case InteractionTool.Jelly: DrawJelly(dc, effect); break;
            case InteractionTool.Egg: DrawEgg(dc, effect); break;
            case InteractionTool.Whip: DrawWhipEffect(dc, effect); break;
        }
        dc.Pop();
    }

    private static void DrawPaperReject(DrawingContext dc, Impact effect)
    {
        var t = effect.Age;
        if (t < .55)
        {
            var p = t / .55;
            var center = new Point(89, 94);
            dc.PushTransform(new TranslateTransform(center.X, center.Y));
            dc.PushTransform(new RotateTransform(-24 + p * 48 + effect.Sequence * 31));
            var squeeze = Math.Sin(p * Math.PI * 2) * .08;
            dc.PushTransform(new ScaleTransform(1 + squeeze, 1 - squeeze));
            DrawCrumpledPaper(dc);
            dc.Pop();
            dc.Pop(); dc.Pop();
            return;
        }
        var flight = Math.Clamp((t - .55) / .42, 0, 1);
        var x = 89 + (effect.Target.X - 89) * flight;
        var y = 94 + (effect.Target.Y - 94) * flight - Math.Sin(flight * Math.PI) * 70;
        var afterContact = Math.Max(0, t - .97);
        if (t > 1.07)
        {
            var fall = t - 1.07;
            x += (effect.Sequence % 2 == 0 ? -1 : 1) * fall * 48;
            y += fall * fall * 125 - Math.Min(fall, .25) * 55;
        }
        // The soft contact shadow and the folded edge thickness separate it from a flat icon.
        dc.PushOpacity(.18);
        dc.DrawEllipse(Plum, null, new Point(x + 4, y + 26), 20, 5);
        dc.Pop();
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new RotateTransform((t - .55) * 430 + effect.Sequence * 31));
        var squash = afterContact < .15 ? Math.Sin(afterContact / .15 * Math.PI) * .22 : 0;
        dc.PushTransform(new ScaleTransform(1 + squash, 1 - squash));
        DrawCrumpledPaper(dc);
        dc.Pop();
        dc.Pop(); dc.Pop();
        if (t is > .96 and < 1.35) DrawBurst(dc, effect.Target, t - .96, Purple);
    }

    private static void DrawCrumpledPaper(DrawingContext dc)
    {
        dc.PushTransform(new TranslateTransform(.45, .8));
        dc.DrawGeometry(VentSurface.Brush("#BEC6BD"), null, CrumpledPaper);
        dc.Pop();
        dc.DrawGeometry(PaperBody, new Pen(VentSurface.Brush("#BAC2B8"), .4)
            { LineJoin = PenLineJoin.Round }, CrumpledPaper);
        dc.PushClip(CrumpledPaper);
        foreach (var fold in PaperFolds) dc.DrawGeometry(fold.Fill, null, fold.Shape);
        // A rumpled sheet has soft, wandering valleys and tiny wrinkles, not a polygon mesh.
        dc.DrawGeometry(null, new Pen(VentSurface.Brush("#26909C8D"), 1.4)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, PaperCreases);
        dc.PushTransform(new TranslateTransform(-.4, -.4));
        dc.DrawGeometry(null, new Pen(VentSurface.Brush("#DDFFFFFF"), .8)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, PaperCreases);
        dc.DrawGeometry(null, new Pen(VentSurface.Brush("#CCFFFFFF"), .55), PaperWrinkles);
        dc.Pop();
        dc.DrawGeometry(null, new Pen(VentSurface.Brush("#788E9B89"), .3)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, PaperCreases);
        dc.DrawGeometry(null, new Pen(VentSurface.Brush("#748E9B89"), .22), PaperWrinkles);
        dc.Pop();
        // Lifted corners expose the thin bright back of the paper.
        dc.DrawGeometry(VentSurface.Brush("#FFFFFC"), new Pen(VentSurface.Brush("#BAC2B8"), .3), PaperCurls);
    }

    private static void DrawStamp(DrawingContext dc, Impact effect)
    {
        var t = effect.Age;
        var target = effect.Target;
        if (t > .25)
        {
            dc.PushTransform(new TranslateTransform(target.X, target.Y));
            dc.PushTransform(new RotateTransform(-12));
            dc.DrawGeometry(RedInk, null, StampGlyph);
            dc.Pop(); dc.Pop();
        }
        if (t < .65)
        {
            var drop = t < .25 ? (1 - Math.Pow(t / .25, 2)) * 95 : Math.Pow((t - .25) / .4, 2) * 155;
            dc.PushTransform(new TranslateTransform(target.X, target.Y - 12 - drop));
            dc.DrawRoundedRectangle(DarkGold, new Pen(Plum, 2), new Rect(-16, -55, 32, 47), 10, 10);
            dc.DrawEllipse(Gold, new Pen(Plum, 2), new Point(0, -53), 28, 12);
            dc.DrawRoundedRectangle(Plum, null, new Rect(-37, -14, 74, 25), 6, 6);
            dc.DrawRoundedRectangle(Coral, null, new Rect(-33, 5, 66, 8), 3, 3);
            dc.Pop();
        }
        if (t is > .25 and < .6) DrawBurst(dc, target, t - .25, RedInk);
    }

    private static void DrawPinchSnap(DrawingContext dc, Impact effect)
    {
        if (effect.Age < .5) DrawBurst(dc, effect.Target, effect.Age, Gold);
    }

    private static void DrawSpringGlove(DrawingContext dc, Impact effect)
    {
        var t = effect.Age;
        if (t > .82) return;
        var extension = t < .22 ? Math.Sin(t / .22 * Math.PI / 2) : t < .35 ? 1 : Math.Max(0, 1 - (t - .35) / .47);
        var start = new Point(32, effect.Target.Y + 12);
        var endX = start.X + (effect.Target.X - 26 - start.X) * extension;
        dc.DrawRoundedRectangle(Purple, new Pen(Plum, 2), new Rect(9, start.Y - 22, 30, 44), 6, 6);
        var coil = new StreamGeometry();
        using (var path = coil.Open())
        {
            path.BeginFigure(start, false, false);
            for (var i = 1; i <= 22; i++)
                path.LineTo(new Point(start.X + (endX - start.X) * i / 22, start.Y + (i == 22 ? 0 : i % 2 == 0 ? -10 : 10)), true, false);
        }
        dc.DrawGeometry(null, new Pen(Plum, 3) { LineJoin = PenLineJoin.Round }, coil);
        dc.PushTransform(new TranslateTransform(endX, start.Y));
        dc.DrawRoundedRectangle(RedInk, null, new Rect(-13, -17, 24, 34), 5, 5);
        dc.DrawRoundedRectangle(Coral, new Pen(RedInk, 2), new Rect(-5, -28, 52, 48), 17, 17);
        dc.DrawEllipse(Coral, new Pen(RedInk, 2), new Point(6, 17), 13, 12);
        dc.DrawRoundedRectangle(VentSurface.Brush("#FFC9BC"), null, new Rect(5, -21, 30, 6), 3, 3);
        dc.Pop();
        if (t is > .21 and < .55) DrawBurst(dc, effect.Target, t - .21, Gold);
    }

    private static void DrawJelly(DrawingContext dc, Impact effect)
    {
        var t = effect.Age;
        if (t < .55)
        {
            var y = t < .2 ? 25 + t / .2 * 112 : t < .4 ? 137 : 137 - (t - .4) / .15 * 145;
            dc.DrawRoundedRectangle(Sky, new Pen(Plum, 2), new Rect(104, y, 252, 13), 6, 6);
            dc.DrawRoundedRectangle(VentSurface.Brush("#DFF9F8"), null, new Rect(111, y + 2, 238, 4), 2, 2);
            dc.DrawLine(new Pen(Sky, 6), new Point(230, y), new Point(230, Math.Max(0, y - 20)));
        }
        if (t is > .2 and < 1.2)
        {
            dc.DrawEllipse(null, new Pen(Sky, 2), new Point(230, 197), 72 + t * 30, 5 + Math.Sin(t * 13) * 3);
            DrawText(dc, "Q 弹回位", 11, Plum, new Point(344, 155));
        }
    }

    private static void DrawEgg(DrawingContext dc, Impact effect)
    {
        var t = effect.Age;
        const double flight = .3;
        if (t < flight)
        {
            var p = t / flight;
            var start = new Point(effect.Sequence % 2 == 0 ? 45 : 415, 180);
            var point = start + (effect.Target - start) * p + new Vector(0, -Math.Sin(p * Math.PI) * 80);
            dc.PushTransform(new RotateTransform(p * 380, point.X, point.Y));
            dc.DrawEllipse(PaperWhite, new Pen(DarkGold, 1.3), point, 13, 18);
            dc.Pop();
            return;
        }
        var age = t - flight;
        var slide = Math.Max(0, age - .35) * 28;
        var center = effect.Target + new Vector(0, slide);
        var scale = 1 + Math.Max(0, .2 - age) * 1.4;
        dc.PushTransform(new ScaleTransform(scale, scale, center.X, center.Y));
        var white = VentSurface.Brush("#EEFFFCF0");
        dc.DrawEllipse(white, null, center, 37, 23);
        for (var i = 0; i < 6; i++)
        {
            var angle = i * Math.PI / 3 + effect.Sequence;
            dc.DrawEllipse(white, null, center + new Vector(Math.Cos(angle) * 28, Math.Sin(angle) * 18), 16, 11);
        }
        dc.DrawRoundedRectangle(white, null, new Rect(center.X - 23, center.Y + 10, 7, Math.Min(42, age * 26) + 4), 3, 3);
        dc.DrawRoundedRectangle(white, null, new Rect(center.X + 17, center.Y + 8, 6, Math.Min(30, age * 20) + 4), 3, 3);
        dc.DrawEllipse(Gold, new Pen(DarkGold, 1), center + new Vector(1, 1), 19, 15);
        dc.DrawEllipse(VentSurface.Brush("#FFF1AD"), null, center + new Vector(-5, -4), 5, 3);
        dc.Pop();
        if (age < .7)
        {
            for (var side = -1; side <= 1; side += 2)
            {
                var shell = effect.Target + new Vector(side * (24 + age * 66), age * age * 160 - age * 65);
                dc.PushTransform(new TranslateTransform(shell.X, shell.Y));
                dc.PushTransform(new RotateTransform(side * age * 180));
                dc.DrawGeometry(PaperWhite, new Pen(DarkGold, 1), Geometry.Parse("M -13,0 L -8,-5 -3,0 2,-5 7,0 13,-4 C 18,18 -12,23 -13,0 Z"));
                dc.Pop(); dc.Pop();
            }
        }
    }

    private static void DrawBurst(DrawingContext dc, Point center, double age, Brush color)
    {
        dc.PushOpacity(Math.Clamp(1 - age / .5, 0, 1));
        for (var i = 0; i < 7; i++)
        {
            var angle = i * Math.PI * 2 / 7;
            var offset = new Vector(Math.Cos(angle), Math.Sin(angle));
            dc.DrawLine(new Pen(color, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                center + offset * (24 + age * 70), center + offset * (34 + age * 85));
        }
        dc.Pop();
    }
}
