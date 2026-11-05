using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QiaoQiaoAI.UI;

internal sealed partial class MascotScene
{
    private readonly List<BitmapSource> _deepSeekIdleFrames = [];
    private readonly List<BitmapSource> _deepSeekReactionFrames = [];
    private SkinConfig _deepSeekConfig = new();
    private Rect _deepSeekRect = new(130, 8, 200, 200);
    private string _deepSeekStatus = "缺少 DeepSeek 鲸鱼动画图集";
    private double _deepSeekReactionClock;
    private bool _transparentStage;
    private bool _whipping;
    private bool _whipSelected;
    private bool _whipReturning;
    private const int WhipNodeCount = 18;
    private const double WhipSegmentLength = 12.5;
    private const double WhipPhysicsStep = 1d / 120;
    private static readonly Point RestingWhipHandle = new(66, 162);
    // One persistent chain owns the only rope and grip. Impacts never allocate a second model.
    private readonly Point[] _whipPoints = new Point[WhipNodeCount];
    private readonly Point[] _whipPreviousPoints = new Point[WhipNodeCount];
    private readonly Point[] _whipSweepFrom = new Point[WhipNodeCount];
    private IReadOnlyList<Point>? _whipPointView;
    private Point _whipHandle = RestingWhipHandle;
    private Vector _whipHandleVelocity;
    private bool _whipContactPending;
    private bool _whipHasPointerMotion;
    private bool _consumingWhipContact;
    private Point _whipContactPoint;
    private double _whipContactAge;
    private double _whipReleaseAge;
    private bool _whipModelReady;
    private double _sinceWhip = 100;
    private readonly List<RedMark> _redMarks = [];
    private double _blushStrength;
    private static readonly Brush WhipLeather = VentSurface.Brush("#9763B6");
    private static readonly Brush WhipHighlight = VentSurface.Brush("#D8B4EB");
    private static readonly Brush Blush = MakeBlush();

    public bool TransparentStage { get => _transparentStage; set { _transparentStage = value; InvalidateVisual(); } }
    public bool HasDeepSeekSkin => _deepSeekIdleFrames.Count > 0 && _deepSeekReactionFrames.Count > 0;
    public bool IsWhipping => _whipping;
    public bool WhipSelected
    {
        get => _whipSelected;
        set
        {
            if (_whipSelected == value) return;
            _whipSelected = value;
            CancelWhip();
            InvalidateVisual();
        }
    }
    public Point WhipHandlePosition => _whipHandle;
    public int VisibleWhipCount => _whipSelected && HasSkin ? 1 : 0;
    public IReadOnlyList<Point> WhipPoints => _whipPointView ??= Array.AsReadOnly(_whipPoints);
    public int RedMarkCount => _redMarks.Count;
    public double BlushStrength => _blushStrength;
    private bool IsDeepSeekReacting => SelectedCharacter == CharacterKind.DeepSeek && HasDeepSeekSkin && _sinceHit < .88;
    private int DeepSeekFrameColumn => IsDeepSeekReacting
        ? (int)((_deepSeekReactionClock + 1e-9) / .22) % _deepSeekReactionFrames.Count
        : (int)(_clock * 4) % Math.Max(1, _deepSeekIdleFrames.Count);
    public int DeepSeekFrame => !HasDeepSeekSkin ? -1 : (IsDeepSeekReacting ? _deepSeekConfig.ReactionRow : _deepSeekConfig.IdleRow) * _deepSeekConfig.Columns + DeepSeekFrameColumn;
    private Rect CharacterRect => SelectedCharacter switch
    {
        CharacterKind.Doubao => _doubaoRect,
        CharacterKind.DeepSeek => _deepSeekRect,
        _ => MascotRect
    };

    private void LoadDeepSeek(string directory)
    {
        try
        {
            var config = VentSurface.ReadOptional<SkinConfig>(Path.Combine(directory, "deepseek-skin.json"));
            if (config is null) return;
            if (config.FrameWidth is < 1 or > 2048 || config.FrameHeight is < 1 or > 2048 ||
                config.Columns is < 1 or > 64 || config.Rows is < 1 or > 64)
                throw new InvalidDataException("鲸鱼图集帧设置超出范围。");
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(directory, config.Image));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("鲸鱼图集须位于素材目录内。");
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("鲸鱼图集过大。");
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit(); bitmap.Freeze();
            if (bitmap.PixelWidth > 16384 || bitmap.PixelHeight > 16384 || bitmap.PixelWidth < config.FrameWidth * config.Columns || bitmap.PixelHeight < config.FrameHeight * config.Rows)
                throw new InvalidDataException("鲸鱼图集尺寸与配置不符。");
            AddFrames(bitmap, config, config.IdleRow, config.IdleFrames, _deepSeekIdleFrames);
            AddFrames(bitmap, config, config.ReactionRow, config.ReactionFrames, _deepSeekReactionFrames);
            _deepSeekConfig = config;
            var frameWidth = config.FrameWidth - config.FrameInset * 2;
            var frameHeight = config.FrameHeight - config.FrameInset * 2;
            var scale = Math.Min(200d / frameWidth, 208d / frameHeight);
            _deepSeekRect = new Rect(230 - frameWidth * scale / 2, 208 - frameHeight * scale, frameWidth * scale, frameHeight * scale);
            _deepSeekStatus = $"已加载 DeepSeek 鲸鱼 · {_deepSeekIdleFrames.Count} 帧游动 + {_deepSeekReactionFrames.Count} 帧受击";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            _deepSeekIdleFrames.Clear(); _deepSeekReactionFrames.Clear();
            _deepSeekStatus = "鲸鱼动画加载失败：" + ex.Message;
        }
    }

    public void PulsePinch()
    {
        if (!_pinching) return;
        // Keep the grabbed cheek and its anchor intact during long-hold pulses.
        Hit(InteractionTool.Pinch, _pinchAnchor);
    }

    public bool IsWhipHit(Point point)
    {
        if (VisibleWhipCount == 0 || !IsFinite(point)) return false;
        // A generous grip and rope hit region remain usable in a transparent desktop window.
        if ((point - _whipHandle).Length <= 24) return true;
        for (var i = 1; i < WhipNodeCount; i++)
            if (PointSegmentDistanceSquared(point, _whipPoints[i - 1], _whipPoints[i]) <= 100) return true;
        return false;
    }

    public void BeginWhip(Point position)
    {
        if (!WhipSelected || !HasSkin || !IsFinite(position)) return;
        if (!_whipModelReady) ResetWhipModel();
        _whipping = true;
        _whipReturning = false;
        _whipHandleVelocity = default;
        _sinceWhip = 100;
        _whipHasPointerMotion = false;
        DropPendingWhipHit();
        // Grabbing a rope segment moves the same grip there; the remaining chain keeps its shape.
        // Starting a gesture itself never produces a contact or an impact burst.
        var nextHandle = ClampWhipHandle(position);
        var translation = nextHandle - _whipHandle;
        for (var i = 0; i < WhipNodeCount; i++)
        {
            _whipPoints[i] = ClampWhipPoint(_whipPoints[i] + translation);
            _whipPreviousPoints[i] = _whipPoints[i];
        }
        _whipHandle = nextHandle;
        ConstrainWhipChain();
        Array.Copy(_whipPoints, _whipPreviousPoints, WhipNodeCount);
        InvalidateVisual();
    }

    public bool UpdateWhip(Point position)
    {
        if (!_whipping || !IsFinite(position)) return false;
        // Pointer samples supersede stale contacts; physics-only contacts are consumed by the host tick.
        DropPendingWhipHit();
        var nextHandle = ClampWhipHandle(position);
        if ((nextHandle - _whipHandle).LengthSquared > .0001)
        {
            _whipHasPointerMotion = true;
            _whipHandle = nextHandle;
            SimulateWhip(WhipPhysicsStep);
        }
        InvalidateVisual();
        return TryConsumeWhipHit();
    }

    public bool TryConsumeWhipHit()
    {
        if (!_whipping || !_whipContactPending || _whipContactAge > .12 || _sinceWhip < .1) return false;
        var target = _whipContactPoint;
        DropPendingWhipHit();
        // A physical collision records a burst/reaction but must not inject another automatic lash.
        _consumingWhipContact = true;
        try { Hit(InteractionTool.Whip, target); }
        finally { _consumingWhipContact = false; }
        return true;
    }

    public void DropPendingWhipHit() { _whipContactPending = false; _whipContactAge = 0; }

    public void EndWhip()
    {
        if (!_whipping) return;
        _whipping = false;
        _whipReturning = true;
        _whipReleaseAge = 0;
        DropPendingWhipHit();
        InvalidateVisual();
    }

    public void CancelWhip()
    {
        _whipping = false;
        _whipReturning = false;
        _whipHasPointerMotion = false;
        _sinceWhip = 100;
        DropPendingWhipHit();
        ResetWhipModel();
        InvalidateVisual();
    }

    private void ResetWhipModel()
    {
        _whipHandle = RestingWhipHandle;
        _whipHandleVelocity = default;
        _whipReleaseAge = 0;
        _whipPoints[0] = _whipHandle;
        for (var i = 1; i < WhipNodeCount; i++)
        {
            var angle = -1.05 + (i - 1) * .14;
            _whipPoints[i] = _whipPoints[i - 1] + new Vector(Math.Cos(angle), Math.Sin(angle)) * WhipSegmentLength;
        }
        Array.Copy(_whipPoints, _whipPreviousPoints, WhipNodeCount);
        Array.Copy(_whipPoints, _whipSweepFrom, WhipNodeCount);
        _whipModelReady = true;
    }

    private static bool IsFinite(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static Point ClampWhipHandle(Point point) => new(Math.Clamp(point.X, 22, 438), Math.Clamp(point.Y, 26, 176));
    private static Point ClampWhipPoint(Point point) => new(Math.Clamp(point.X, 4, 456), Math.Clamp(point.Y, 4, 194));

    private static double PointSegmentDistanceSquared(Point point, Point start, Point end)
    {
        var direction = end - start;
        var denominator = direction.LengthSquared;
        if (denominator < 1e-9) return (point - start).LengthSquared;
        var t = Math.Clamp(Vector.Multiply(point - start, direction) / denominator, 0, 1);
        return (point - (start + direction * t)).LengthSquared;
    }

    private void KickWhip(Point target, double age)
    {
        if (!WhipSelected || age >= .3) return;
        if (!_whipModelReady) ResetWhipModel();
        // Continuous clicks energize this chain instead of drawing a new whip for each hit.
        var direction = target - _whipHandle;
        if (direction.Length < 1) direction = new Vector(1, -.35);
        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        for (var i = 1; i < WhipNodeCount; i++)
        {
            var wave = Math.Sin((double)i / WhipNodeCount * Math.PI + _sequence * 1.3);
            var velocity = direction * (260 + i * 10) + normal * (wave * 390);
            _whipPreviousPoints[i] = _whipPoints[i] - velocity * WhipPhysicsStep;
        }
        if (!_whipping) { _whipReturning = true; _whipReleaseAge = 0; }
    }

    private void SimulateWhip(double step)
    {
        Array.Copy(_whipPoints, _whipSweepFrom, WhipNodeCount);
        if (_whipReturning)
        {
            _whipHandleVelocity += ((RestingWhipHandle - _whipHandle) * 64 - _whipHandleVelocity * 16) * step;
            if (_whipHandleVelocity.Length > 500) _whipHandleVelocity *= 500 / _whipHandleVelocity.Length;
            _whipHandle = ClampWhipHandle(_whipHandle + _whipHandleVelocity * step);
        }
        _whipPoints[0] = _whipPreviousPoints[0] = _whipHandle;
        var damping = Math.Exp(-step * (_whipReturning ? 4.4 : 2.6));
        var floor = Math.Max(194, _whipHandle.Y + 12);
        for (var i = 1; i < WhipNodeCount; i++)
        {
            var point = _whipPoints[i];
            var velocity = (point - _whipPreviousPoints[i]) * damping;
            if (velocity.Length > 16) velocity *= 16 / velocity.Length;
            _whipPreviousPoints[i] = point;
            point += velocity + new Vector(0, 620 * step * step);
            if (point.Y > floor)
            {
                point.Y = floor;
                _whipPreviousPoints[i] = new Point(point.X - velocity.X * .6, point.Y + Math.Abs(velocity.Y) * .12);
            }
            _whipPoints[i] = point;
        }
        // Fixed-size position constraints stay stable for fast pointer jumps and long test frames.
        for (var iteration = 0; iteration < 12; iteration++)
        {
            _whipPoints[0] = _whipHandle;
            for (var i = 1; i < WhipNodeCount; i++)
            {
                var offset = _whipPoints[i] - _whipPoints[i - 1];
                var length = offset.Length;
                if (length < 1e-8) offset = new Vector(0, WhipSegmentLength);
                else offset *= (length - WhipSegmentLength) / length;
                if (length < 1e-8) _whipPoints[i] = _whipPoints[i - 1] + offset;
                else if (i == 1) _whipPoints[i] -= offset;
                else { _whipPoints[i - 1] += offset * .5; _whipPoints[i] -= offset * .5; }
            }
        }
        ConstrainWhipChain();
        if (_whipping) DetectWhipContact();
    }

    private void ConstrainWhipChain()
    {
        // Final projection guarantees a maximum segment length. Wall contact may shorten a link,
        // keeping the one rope and grip visible and reachable after a drag outside the window.
        _whipPoints[0] = _whipHandle;
        for (var i = 1; i < WhipNodeCount; i++)
        {
            var offset = _whipPoints[i] - _whipPoints[i - 1];
            if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y) || offset.Length < 1e-8) offset = new Vector(0, 1);
            offset.Normalize();
            _whipPoints[i] = ClampWhipPoint(_whipPoints[i - 1] + offset * WhipSegmentLength);
        }
    }

    private void DetectWhipContact()
    {
        if (!_whipHasPointerMotion) return;
        // Sweep the rope body, not the pointer. A stationary rope touching the face is not a new hit.
        for (var i = 3; i < WhipNodeCount; i++)
        {
            var from = _whipSweepFrom[i]; var to = _whipPoints[i];
            var previousFrom = _whipSweepFrom[i - 1]; var previousTo = _whipPoints[i - 1];
            if (Math.Max((to - from).LengthSquared, (previousTo - previousFrom).LengthSquared) < 3.5 * 3.5) continue;
            if (!IntersectsCharacter(from, to) && !IntersectsCharacter(previousFrom, previousTo) &&
                !IntersectsCharacter(previousFrom, from) && !IntersectsCharacter(previousTo, to)) continue;
            var rect = CharacterRect;
            var midpoint = previousTo + (to - previousTo) * .5;
            _whipContactPoint = new Point(Math.Clamp(midpoint.X, rect.Left + 22, rect.Right - 22), Math.Clamp(midpoint.Y, rect.Top + 30, rect.Bottom - 24));
            _whipContactPending = true;
            _whipContactAge = 0;
            return;
        }
    }

    private bool IntersectsCharacter(Point from, Point to)
    {
        var rect = CharacterRect;
        var direction = to - from;
        double min = 0, max = 1;
        bool Clip(double origin, double delta, double low, double high)
        {
            if (Math.Abs(delta) < 1e-9) return origin >= low && origin <= high;
            var a = (low - origin) / delta; var b = (high - origin) / delta;
            if (a > b) (a, b) = (b, a);
            min = Math.Max(min, a); max = Math.Min(max, b);
            return min <= max;
        }
        return Clip(from.X, direction.X, rect.Left, rect.Right) && Clip(from.Y, direction.Y, rect.Top, rect.Bottom);
    }

    private void AdvanceWhipAndMarks(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        _sinceWhip = Math.Min(100, _sinceWhip + seconds);
        _whipContactAge += seconds;
        if (_whipContactAge > .12) DropPendingWhipHit();
        if (WhipSelected && (_whipping || _whipReturning))
        {
            // No unbounded catch-up: at most 32 small integrations, even after a suspended window.
            var simulated = Math.Min(seconds, 32 * WhipPhysicsStep);
            var steps = Math.Clamp((int)Math.Ceiling(simulated / WhipPhysicsStep), 1, 32);
            for (var i = 0; i < steps; i++) SimulateWhip(simulated / steps);
            if (_whipReturning)
            {
                _whipReleaseAge += seconds;
                if (_whipReleaseAge >= 2.5)
                {
                    _whipReturning = false;
                    _whipHandleVelocity = default;
                    Array.Copy(_whipPoints, _whipPreviousPoints, WhipNodeCount);
                }
            }
        }
        _blushStrength = Math.Max(0, _blushStrength - seconds * .18);
        foreach (var mark in _redMarks) mark.Age += seconds;
        _redMarks.RemoveAll(mark => mark.Age >= 4.5);
    }

    private void AddRedMark(InteractionTool tool, Point target, double age)
    {
        if (age >= 4.5) return;
        _blushStrength = Math.Min(.85, _blushStrength + .17);
        while (_redMarks.Count >= 8) _redMarks.RemoveAt(0);
        // Pink cartoon flush, never a wound. Keep marks on the face even for clicks near the feet.
        var rect = CharacterRect;
        var whale = SelectedCharacter == CharacterKind.DeepSeek;
        var cheek = new Point(rect.X + rect.Width * (whale ? .36 : target.X < 230 ? .40 : .64), rect.Y + rect.Height * (whale ? .57 : .62));
        _redMarks.Add(new RedMark(cheek, tool == InteractionTool.Whip, _sequence % 3 - 1) { Age = age });
    }

    private void DrawRedMarks(DrawingContext dc)
    {
        if (_blushStrength <= 0 && _redMarks.Count == 0) return;
        var rect = CharacterRect;
        var whale = SelectedCharacter == CharacterKind.DeepSeek;
        var cheeks = whale ? new[] { new Point(rect.X + rect.Width * .36, rect.Y + rect.Height * .57) }
            : new[] { new Point(rect.X + rect.Width * .40, rect.Y + rect.Height * .62), new Point(rect.X + rect.Width * .64, rect.Y + rect.Height * .62) };
        dc.PushOpacity(_blushStrength);
        foreach (var cheek in cheeks) dc.DrawEllipse(Blush, null, DeformFacePoint(cheek), whale ? 21 : 15, 11);
        dc.Pop();
        foreach (var mark in _redMarks)
        {
            if (!mark.Stripe) continue;
            var point = DeformFacePoint(mark.Point);
            var fade = Math.Clamp((4.5 - mark.Age) / 3.5, 0, .65);
            var pen = new Pen(VentSurface.Brush("#F0788C"), 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.PushOpacity(fade);
            dc.PushTransform(new RotateTransform(-18 + mark.Offset * 12, point.X, point.Y));
            dc.DrawLine(pen, point + new Vector(-10, mark.Offset * 5), point + new Vector(10, mark.Offset * 5));
            dc.Pop(); dc.Pop();
        }
    }

    private Point DeformFacePoint(Point point)
    {
        if (_pinchOffset.Length < .05) return point;
        WeightAndGradient(point, CharacterRect, out var weight, out _);
        return point + _pinchOffset * weight;
    }

    private static Brush MakeBlush()
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(235, 247, 112, 140), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(130, 250, 134, 154), .5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 250, 134, 154), 1));
        brush.Freeze(); return brush;
    }

    private void DrawActiveWhip(DrawingContext dc)
    {
        if (VisibleWhipCount == 0) return;
        var rope = new StreamGeometry();
        using (var path = rope.Open())
        {
            path.BeginFigure(_whipPoints[0], false, false);
            // Quadratic midpoint smoothing follows the same simulated chain, never a second rope.
            for (var i = 1; i < WhipNodeCount - 1; i++)
            {
                var midpoint = _whipPoints[i] + (_whipPoints[i + 1] - _whipPoints[i]) * .5;
                path.QuadraticBezierTo(_whipPoints[i], midpoint, true, false);
            }
            path.LineTo(_whipPoints[^1], true, false);
        }
        dc.DrawGeometry(null, new Pen(Brushes.Transparent, 20) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, rope);
        dc.DrawEllipse(Brushes.Transparent, null, _whipHandle, 24, 24);
        dc.DrawGeometry(null, new Pen(Plum, 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, rope);
        dc.DrawGeometry(null, new Pen(WhipLeather, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, rope);
        dc.DrawEllipse(WhipHighlight, new Pen(Plum, 1), _whipPoints[^1], 3.6, 3.6);

        var direction = _whipPoints[1] - _whipHandle;
        var angle = Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
        dc.PushTransform(new RotateTransform(angle, _whipHandle.X, _whipHandle.Y));
        // A wide, solid grip follows the pointer, rather than moving the rope tip to it.
        dc.DrawRoundedRectangle(Plum, new Pen(Purple, 1.5), new Rect(_whipHandle.X - 18, _whipHandle.Y - 10, 36, 20), 7, 7);
        dc.DrawRoundedRectangle(VentSurface.Brush("#775186"), null, new Rect(_whipHandle.X - 14, _whipHandle.Y - 7, 25, 14), 4, 4);
        for (var i = 0; i < 4; i++)
            dc.DrawLine(new Pen(WhipHighlight, 1.5), _whipHandle + new Vector(-10 + i * 6, -6), _whipHandle + new Vector(-10 + i * 6, 6));
        dc.DrawEllipse(Gold, null, _whipHandle + new Vector(13, 0), 3, 6);
        dc.Pop();
    }

    private static void DrawWhipEffect(DrawingContext dc, Impact effect)
    {
        // Only the contact burst is transient. The sole rope/handle is DrawActiveWhip above.
        if (effect.Age < .4) DrawBurst(dc, effect.Target, effect.Age, VentSurface.Brush("#F08A9C"));
    }

    private sealed class RedMark(Point point, bool stripe, int offset)
    {
        public Point Point { get; } = point;
        public bool Stripe { get; } = stripe;
        public int Offset { get; } = offset;
        public double Age { get; set; }
    }
}
