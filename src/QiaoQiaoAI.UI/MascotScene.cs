using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QiaoQiaoAI.UI;

/// <summary>Immediate-mode drawing keeps every temporary effect bounded and easy to dispose.</summary>
internal sealed partial class MascotScene : FrameworkElement
{
    private static readonly Brush Plum = VentSurface.Brush("#59446E");
    private static readonly Brush Purple = VentSurface.Brush("#BBA6DE");
    private static readonly Brush Gold = VentSurface.Brush("#F1C65D");
    private static readonly Brush DarkGold = VentSurface.Brush("#BF9548");
    private static readonly Brush Brown = VentSurface.Brush("#A77662");
    private static readonly Brush BrownShade = VentSurface.Brush("#825747");
    private static readonly Brush BrownLight = VentSurface.Brush("#CAA08B");
    private static readonly Pen BrownPen = new(BrownShade, 2.5);
    private static readonly Geometry Star = CreateStar();
    private static readonly Geometry Poop = Geometry.Parse("M -21,15 C -30,10 -25,1 -15,-2 C -21,-9 -9,-16 -2,-17 C -6,-23 7,-26 2,-33 C 15,-29 20,-18 12,-13 C 25,-11 27,-2 21,2 C 35,4 31,18 20,19 L -17,19 C -22,19 -25,18 -21,15 Z");
    private static readonly Typeface CaptionFont = new("Microsoft YaHei UI");
    private readonly List<Impact> _effects = [];
    private readonly List<BitmapSource> _idleFrames = [];
    private readonly List<BitmapSource> _reactionFrames = [];
    private readonly List<BitmapSource> _doubaoIdleFrames = [];
    private readonly List<BitmapSource> _doubaoReactionFrames = [];
    private readonly int _maxEffects;
    private readonly double _hammerLife;
    private readonly double _poopLife;
    private double _fps = 8;
    private double _clock;
    private double _sinceHit = 100;
    private int _sequence;
    private InteractionTool _lastTool;
    private SkinConfig _codexConfig = new();
    private DoubaoSkinConfig _doubaoConfig = new();
    private double _doubaoReactionClock;
    private double _doubaoIdleHoldSeconds = 2.8;
    private double _doubaoBlinkSeconds = 0.4;
    private double _doubaoReactionFrameSeconds = 0.18;
    private Rect _doubaoRect = new(134, 1, 192, 208);
    private string _codexStatus = "角色素材尚未加载";
    private string _doubaoStatus = "缺少豆包动画图集 doubao-spritesheet.png 或 doubao-skin.json";
    private double _chargeProgress;
    private bool _spraying;

    private CharacterKind _selectedCharacter;
    public CharacterKind SelectedCharacter
    {
        get => _selectedCharacter;
        set
        {
            if (_selectedCharacter == value) return;
            _selectedCharacter = value;
            ClearEffects();
        }
    }
    public bool HasCodexSkin => _idleFrames.Count > 0;
    public bool HasDoubaoSkin => _doubaoIdleFrames.Count > 0 && _doubaoReactionFrames.Count > 0;
    public bool HasSkin => SelectedCharacter switch { CharacterKind.Codex => HasCodexSkin, CharacterKind.Doubao => HasDoubaoSkin, CharacterKind.DeepSeek => HasDeepSeekSkin, _ => false };
    public string AssetStatus => SelectedCharacter switch { CharacterKind.Codex => _codexStatus, CharacterKind.Doubao => _doubaoStatus, _ => _deepSeekStatus };
    public int EffectCount => _effects.Count;
    public int SuperEffectCount => _effects.Count(effect => effect.Super);
    public int MaxEffects => _maxEffects;
    private double DoubaoReactionDuration => _doubaoReactionFrames.Count * _doubaoReactionFrameSeconds;
    private bool IsDoubaoReacting => SelectedCharacter == CharacterKind.Doubao && HasDoubaoSkin && _sinceHit < DoubaoReactionDuration;
    public bool IsReacting => SelectedCharacter switch { CharacterKind.Doubao => IsDoubaoReacting, CharacterKind.DeepSeek => IsDeepSeekReacting, _ => HasCodexSkin && _reactionFrames.Count > 0 && _sinceHit < .45 };
    public int DoubaoFrame => !HasDoubaoSkin ? -1 : IsDoubaoReacting
        ? _doubaoConfig.ReactionRow * _doubaoConfig.Columns + DoubaoFrameColumn
        : _doubaoConfig.IdleRow * _doubaoConfig.Columns + DoubaoFrameColumn;
    public int CurrentCharacterFrame => SelectedCharacter == CharacterKind.DeepSeek ? DeepSeekFrame : SelectedCharacter == CharacterKind.Doubao ? DoubaoFrame : !HasCodexSkin ? -1 : IsReacting
        ? _codexConfig.ReactionRow * _codexConfig.Columns + Math.Min(_lastTool == InteractionTool.Hammer ? 1 : 3, _reactionFrames.Count - 1)
        : _codexConfig.IdleRow * _codexConfig.Columns + (int)(_clock * _fps) % _idleFrames.Count;
    private int DoubaoFrameColumn
    {
        get
        {
            if (!HasDoubaoSkin) return 0;
            if (IsDoubaoReacting)
                return (int)((_doubaoReactionClock + 1e-9) / _doubaoReactionFrameSeconds) % _doubaoReactionFrames.Count;
            var phase = _clock % (_doubaoIdleHoldSeconds + _doubaoBlinkSeconds);
            if (phase < _doubaoIdleHoldSeconds || _doubaoIdleFrames.Count == 1) return 0;
            var blinkFrame = 1 + (int)((phase - _doubaoIdleHoldSeconds) / _doubaoBlinkSeconds * (_doubaoIdleFrames.Count - 1));
            return Math.Min(_doubaoIdleFrames.Count - 1, blinkFrame);
        }
    }
    private static Rect MascotRect => new(134, 1, 192, 208);

    public MascotScene(string assetsDirectory, EffectsConfig config)
    {
        _maxEffects = Math.Clamp(config.MaxEffects, 1, 24);
        _hammerLife = FiniteClamp(config.HammerDurationSeconds, .85, .3, 2);
        _poopLife = FiniteClamp(config.PoopDurationSeconds, 2.6, .8, 6);
        ClipToBounds = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        LoadSkin(assetsDirectory);
        LoadDoubao(assetsDirectory);
        LoadDeepSeek(assetsDirectory);
        ResetWhipModel();
    }

    private static double FiniteClamp(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private void LoadSkin(string directory)
    {
        try
        {
            var config = VentSurface.ReadOptional<SkinConfig>(Path.Combine(directory, "skin.json")) ?? new();
            if (config.FrameWidth is < 1 or > 2048 || config.FrameHeight is < 1 or > 2048 ||
                config.Columns is < 1 or > 64 || config.Rows is < 1 or > 64)
                throw new InvalidDataException("图集帧设置超出范围。");
            var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            var imagePath = Path.GetFullPath(Path.Combine(directory, config.Image));
            if (!imagePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("图集须位于素材文件夹内。");
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.EndInit(); bitmap.Freeze();
            if (bitmap.PixelWidth > 16384 || bitmap.PixelHeight > 16384 ||
                bitmap.PixelWidth < config.FrameWidth * config.Columns || bitmap.PixelHeight < config.FrameHeight * config.Rows)
                throw new InvalidDataException("图集尺寸与 skin.json 不匹配。");
            AddFrames(bitmap, config, config.IdleRow, config.IdleFrames, _idleFrames);
            if (config.ReactionRow >= 0) AddFrames(bitmap, config, config.ReactionRow, config.ReactionFrames, _reactionFrames);
            _fps = FiniteClamp(config.Fps, 8, 1, 30);
            _codexConfig = config;
            _codexStatus = $"已加载 {config.Name} · {_idleFrames.Count} 帧";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            _idleFrames.Clear(); _reactionFrames.Clear();
            _codexStatus = "未能加载角色素材：" + ex.Message;
        }
    }

    private void LoadDoubao(string directory)
    {
        try
        {
            var config = VentSurface.ReadOptional<DoubaoSkinConfig>(Path.Combine(directory, "doubao-skin.json"));
            if (config is null) return;
            if (config.FrameWidth is < 1 or > 2048 || config.FrameHeight is < 1 or > 2048 ||
                config.Columns is < 1 or > 64 || config.Rows is < 1 or > 64)
                throw new InvalidDataException("豆包图集帧设置超出范围。");
            var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(directory, config.Image));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("豆包图集须位于素材文件夹内。");
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("豆包动画图集过大。");
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit(); bitmap.Freeze();
            if (bitmap.PixelWidth > 16384 || bitmap.PixelHeight > 16384 ||
                bitmap.PixelWidth < config.FrameWidth * config.Columns || bitmap.PixelHeight < config.FrameHeight * config.Rows)
                throw new InvalidDataException("豆包图集尺寸与 doubao-skin.json 不匹配。");
            AddFrames(bitmap, config, config.IdleRow, config.IdleFrames, _doubaoIdleFrames);
            AddFrames(bitmap, config, config.ReactionRow, config.ReactionFrames, _doubaoReactionFrames);
            _doubaoConfig = config;
            _doubaoIdleHoldSeconds = FiniteClamp(config.IdleHoldSeconds, 2.8, .5, 10);
            _doubaoBlinkSeconds = FiniteClamp(config.BlinkDurationSeconds, .4, .2, 1);
            _doubaoReactionFrameSeconds = FiniteClamp(config.ReactionFrameSeconds, .18, .08, .4);
            var frameWidth = config.FrameWidth - 2 * config.FrameInset;
            var frameHeight = config.FrameHeight - 2 * config.FrameInset;
            var scale = Math.Min(200d / frameWidth, 208d / frameHeight);
            var width = frameWidth * scale;
            var height = frameHeight * scale;
            _doubaoRect = new Rect(230 - width / 2, 208 - height, width, height);
            _doubaoStatus = $"已加载豆包表情动画 · {_doubaoIdleFrames.Count} 帧眨眼 + {_doubaoReactionFrames.Count} 帧受击";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            _doubaoIdleFrames.Clear(); _doubaoReactionFrames.Clear();
            _doubaoStatus = "未能加载豆包表情动画：" + ex.Message;
        }
    }

    public bool CanSelectCharacter(CharacterKind character) => character switch
    {
        CharacterKind.Codex => HasCodexSkin,
        CharacterKind.Doubao => HasDoubaoSkin,
        CharacterKind.DeepSeek => HasDeepSeekSkin,
        _ => false
    };

    private static void AddFrames(BitmapSource bitmap, SkinConfig config, int row, int count, List<BitmapSource> target)
    {
        if (row < 0 || row >= config.Rows || count < 1 || count > config.Columns) throw new InvalidDataException("图集行或帧数无效。");
        if (config.FrameInset < 0 || config.FrameInset * 2 >= Math.Min(config.FrameWidth, config.FrameHeight))
            throw new InvalidDataException("图集 frameInset 必须非负且小于帧最短边的一半。");
        for (var column = 0; column < count; column++)
        {
            var frame = new CroppedBitmap(bitmap, new Int32Rect(column * config.FrameWidth + config.FrameInset,
                row * config.FrameHeight + config.FrameInset, config.FrameWidth - 2 * config.FrameInset, config.FrameHeight - 2 * config.FrameInset));
            frame.Freeze(); target.Add(frame);
        }
    }

    public bool IsCharacterHit(Point point)
    {
        var rect = CharacterRect;
        rect.Inflate(8, 0);
        return HasSkin && rect.Contains(point);
    }

    public void Hit(InteractionTool tool, Point position, bool super = false, double age = 0)
    {
        if (!HasSkin) return;
        position = new Point(Math.Clamp(double.IsFinite(position.X) ? position.X : 230, 155, 305),
            Math.Clamp(double.IsFinite(position.Y) ? position.Y : 100, 50, 160));
        if (tool == InteractionTool.Pinch && !IsPinching && !IsPinchReturning) StartAutomaticPinch(position);
        var lifetime = tool switch
        {
            InteractionTool.Hammer => _hammerLife,
            InteractionTool.Poop => _poopLife,
            InteractionTool.PaperReject => 2.3,
            InteractionTool.Stamp => 2.4,
            InteractionTool.Pinch => .95,
            InteractionTool.SpringGlove => 1.15,
            InteractionTool.Jelly => 1.5,
            InteractionTool.Egg => 2.8,
            InteractionTool.Whip => .65,
            _ => .85
        };
        if (age < lifetime)
        {
            while (_effects.Count >= _maxEffects) _effects.RemoveAt(0);
            _effects.Add(new Impact(tool, position, _sequence++, lifetime, super) { Age = age, Reply = ReplyText });
        }
        // Repeated spray hits extend the reaction without restarting its frame phase every 140 ms.
        if (SelectedCharacter == CharacterKind.Doubao && !IsDoubaoReacting)
            _doubaoReactionClock = Math.Max(0, age) % DoubaoReactionDuration;
        if (SelectedCharacter == CharacterKind.DeepSeek && !IsDeepSeekReacting)
            _deepSeekReactionClock = Math.Max(0, age) % .88;
        AddRedMark(tool, position, age);
        if (tool == InteractionTool.Whip)
        {
            _sinceWhip = 0;
            if (!_consumingWhipContact) KickWhip(position, age);
        }
        _lastTool = tool; _sinceHit = Math.Max(0, age);
        InvalidateVisual();
    }

    public void Advance(double seconds)
    {
        _clock = (_clock + seconds) % 3600;
        if (IsDoubaoReacting)
            _doubaoReactionClock = (_doubaoReactionClock + seconds) % DoubaoReactionDuration;
        if (IsDeepSeekReacting) _deepSeekReactionClock = (_deepSeekReactionClock + seconds) % .88;
        AdvanceWhipAndMarks(seconds);
        _sinceHit = Math.Min(100, _sinceHit + seconds);
        AdvancePinch(seconds);
        foreach (var effect in _effects)
        {
            var previousAge = effect.Age;
            effect.Age += seconds;
            // The paper first crumples and flies; react again when it actually reaches the face.
            if (effect.Tool == InteractionTool.PaperReject && previousAge < .97 && effect.Age >= .97 && effect.Age < effect.Lifetime)
            {
                _lastTool = effect.Tool;
                _sinceHit = effect.Age - .97;
                _doubaoReactionClock = _sinceHit % Math.Max(.1, DoubaoReactionDuration);
                _deepSeekReactionClock = _sinceHit % .88;
            }
        }
        _effects.RemoveAll(effect => effect.Age >= effect.Lifetime);
        InvalidateVisual();
    }

    public void ResetClock() { _clock = 0; _sinceHit = 100; _doubaoReactionClock = 0; _deepSeekReactionClock = 0; CancelPinch(); CancelWhip(); InvalidateVisual(); }
    public void ClearEffects() { _effects.Clear(); _redMarks.Clear(); _blushStrength = 0; _sinceHit = 100; _doubaoReactionClock = 0; _deepSeekReactionClock = 0; CancelPinch(); CancelWhip(); InvalidateVisual(); }
    public void SetCharge(double progress, bool spraying)
    {
        _chargeProgress = progress; _spraying = spraying; InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!TransparentStage) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 460, 210));

        // Stage decorations are separate from the selected character's transparent atlas frame.
        if (!TransparentStage)
        {
        dc.DrawEllipse(VentSurface.Brush("#F0E8F8"), null, new Point(230, 114), 126, 89);
        dc.DrawEllipse(VentSurface.Brush("#E6DCED"), null, new Point(230, 199), 78, 7);
        DrawSmallSparkle(dc, new Point(106, 75), 6, Gold);
        DrawSmallSparkle(dc, new Point(354, 104), 8, Purple);
        DrawSmallSparkle(dc, new Point(330, 29), 4, Gold);
        dc.DrawEllipse(Purple, null, new Point(123, 135), 2.5, 2.5);
        dc.DrawEllipse(Gold, null, new Point(339, 156), 2.5, 2.5);
        }
        DrawChargeRing(dc);

        if (!HasSkin)
        {
            DrawText(dc, "角色素材未加载", 17, Plum, new Point(155, 84));
            DrawText(dc, "请按安装说明准备官方图集", 11, Plum, new Point(145, 113));
            return;
        }

        var squash = _sinceHit < .42 ? Math.Sin(Math.Min(1, _sinceHit / .42) * Math.PI * 2) * Math.Exp(-_sinceHit * 5) : 0;
        var shake = _sinceHit < .32 ? Math.Sin(_sinceHit * 70) * 7 * Math.Exp(-_sinceHit * 10) : 0;
        var bounce = _sinceHit < .55 ? -Math.Abs(Math.Sin(_sinceHit / .55 * Math.PI)) * 8 * Math.Exp(-_sinceHit * 2) : Math.Sin(_clock * 2.3) * 1.3;
        var transforms = new TransformGroup();
        transforms.Children.Add(new ScaleTransform(1 + squash * .11, 1 - squash * .14, 230, 198));
        transforms.Children.Add(new TranslateTransform(shake, bounce));
        AddPlayTransform(transforms);
        if (SelectedCharacter == CharacterKind.DeepSeek)
            transforms.Children.Add(new RotateTransform(Math.Sin(_clock * (IsReacting ? 24 : 2)) * (IsReacting ? 9 : 2), 230, 150));
        dc.PushTransform(transforms);
        if (SelectedCharacter == CharacterKind.DeepSeek)
        {
            var frames = IsDeepSeekReacting ? _deepSeekReactionFrames : _deepSeekIdleFrames;
            DrawCharacter(dc, frames[DeepSeekFrameColumn], _deepSeekRect);
        }
        else if (SelectedCharacter == CharacterKind.Doubao)
        {
            var frames = IsDoubaoReacting ? _doubaoReactionFrames : _doubaoIdleFrames;
            DrawCharacter(dc, frames[DoubaoFrameColumn], _doubaoRect);
        }
        else
        {
            var frame = _sinceHit < .45 && _reactionFrames.Count > 0
                ? _reactionFrames[Math.Min(_lastTool == InteractionTool.Hammer ? 1 : 3, _reactionFrames.Count - 1)]
                : _idleFrames[(int)(_clock * _fps) % _idleFrames.Count];
            DrawCharacter(dc, frame, MascotRect);
        }
        DrawRedMarks(dc);
        dc.Pop();

        // Even the giant spray remains in the character stage, leaving all controls and dialogue readable.
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, 460, 202)));
        foreach (var effect in _effects)
        {
            if (effect.Tool == InteractionTool.Hammer) DrawHammerEffect(dc, effect);
            else if (effect.Tool == InteractionTool.Poop) DrawPoopEffect(dc, effect);
            else DrawPlayEffect(dc, effect);
        }
        DrawPinchGrip(dc);
        DrawActiveWhip(dc);
        dc.Pop();
    }

    private void DrawChargeRing(DrawingContext dc)
    {
        if (_chargeProgress <= 0 && !_spraying) return;
        var center = new Point(230, 103);
        const double radius = 97;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, 460, 202)));
        dc.DrawEllipse(null, new Pen(VentSurface.Brush("#E6DBED"), 4), center, radius, radius);
        if (_chargeProgress >= .999)
            dc.DrawEllipse(null, new Pen(Gold, 4), center, radius, radius);
        else
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                var angle = -Math.PI / 2 + _chargeProgress * Math.PI * 2;
                path.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                path.ArcTo(new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius),
                    new Size(radius, radius), 0, _chargeProgress > .5, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, new Pen(Gold, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        }
        if (!_spraying) { dc.Pop(); return; }
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4 + _clock * 2;
            var r = radius + 9 + Math.Sin(_clock * 12 + i) * 3;
            DrawSmallSparkle(dc, new Point(center.X + Math.Cos(angle) * r, center.Y + Math.Sin(angle) * r), 3.5, Gold);
        }
        dc.Pop();
    }

    private static void DrawHammerEffect(DrawingContext dc, Impact effect)
    {
        var t = effect.Age;
        var fade = Math.Clamp((effect.Lifetime - t) / .28, 0, 1);
        dc.PushOpacity(fade);
        if (t < .32)
        {
            var swing = t < .12 ? -75 + (t / .12) * 90 : 15 - (t - .12) / .2 * 30;
            var group = new TransformGroup();
            group.Children.Add(new RotateTransform(swing, 30, 58));
            group.Children.Add(new TranslateTransform(effect.Target.X + 14, effect.Target.Y - 43));
            dc.PushTransform(group);
            dc.DrawRoundedRectangle(DarkGold, null, new Rect(22, 0, 15, 65), 6, 6);
            dc.DrawRoundedRectangle(VentSurface.Brush("#D4A95C"), null, new Rect(25, 3, 5, 57), 2, 2);
            dc.DrawRoundedRectangle(Plum, null, new Rect(-11, -17, 80, 38), 13, 13);
            dc.DrawRoundedRectangle(Purple, null, new Rect(-8, -14, 74, 29), 11, 11);
            dc.DrawRoundedRectangle(VentSurface.Brush("#DFD2EF"), null, new Rect(-2, -9, 58, 6), 3, 3);
            dc.Pop();
        }
        for (var i = 0; i < 5; i++)
        {
            var angle = i * Math.PI * .4 + t * 5 + effect.Sequence * .6;
            var radius = 23 + Math.Min(t, .5) * 80;
            var x = effect.Target.X + Math.Cos(angle) * radius;
            var y = effect.Target.Y - 20 + Math.Sin(angle) * radius * .4 - t * 30;
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new RotateTransform(t * 160 + i * 45));
            dc.PushTransform(new ScaleTransform(i % 2 == 0 ? .85 : .55, i % 2 == 0 ? .85 : .55));
            dc.DrawGeometry(i % 2 == 0 ? Gold : Purple, null, Star);
            dc.Pop(); dc.Pop(); dc.Pop();
        }
        if (t < .22)
        {
            var pen = new Pen(Gold, 2.5);
            for (var i = 0; i < 6; i++)
            {
                var angle = i * Math.PI / 3;
                var a = new Point(effect.Target.X + Math.Cos(angle) * 18, effect.Target.Y + Math.Sin(angle) * 18);
                var b = new Point(effect.Target.X + Math.Cos(angle) * 30, effect.Target.Y + Math.Sin(angle) * 30);
                dc.DrawLine(pen, a, b);
            }
        }
        dc.Pop();
    }

    private static void DrawPoopEffect(DrawingContext dc, Impact effect)
    {
        var t = effect.Age;
        const double flight = .25;
        var progress = Math.Min(1, t / flight);
        var start = new Point(effect.Sequence % 2 == 0 ? 45 : 415, 185);
        var x = start.X + (effect.Target.X - start.X) * progress;
        var y = start.Y + (effect.Target.Y - start.Y) * progress - Math.Sin(progress * Math.PI) * 100;
        if (t > .8) y += Math.Pow(Math.Min(t - .8, 1.8), 1.4) * 26;
        var fade = Math.Clamp((effect.Lifetime - t) / .6, 0, 1);
        dc.PushOpacity(fade);
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new RotateTransform(t < flight ? progress * 240 : Math.Sin(effect.Sequence * 7) * 15));
        var scale = (t < flight ? .6 + .3 * progress : .78 + Math.Max(0, .15 - (t - flight)) * 1.3) * (effect.Super ? 2.8 : 1);
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(Brown, BrownPen, Poop);
        dc.DrawBezierEquivalent(BrownLight);
        dc.DrawEllipse(Plum, null, new Point(-8, 2), 2.3, 3.4);
        dc.DrawEllipse(Plum, null, new Point(8, 2), 2.3, 3.4);
        dc.DrawEllipse(Brushes.White, null, new Point(-8.5, .8), .8, .8);
        dc.DrawEllipse(Brushes.White, null, new Point(7.5, .8), .8, .8);
        var smile = Geometry.Parse("M -4,9 Q 0,13 4,9");
        dc.DrawGeometry(null, new Pen(Plum, 1.5), smile);
        dc.Pop(); dc.Pop(); dc.Pop();
        if (t is >= flight and < .55)
        {
            for (var i = 0; i < 4; i++)
            {
                var angle = i * Math.PI / 2 + .4;
                var r = (30 + (t - flight) * 75) * (effect.Super ? 1.8 : 1);
                dc.DrawEllipse(BrownLight, null, new Point(effect.Target.X + Math.Cos(angle) * r, effect.Target.Y + Math.Sin(angle) * r), effect.Super ? 6 : 3.5, effect.Super ? 4 : 2.5);
            }
        }
        dc.Pop();
    }

    private static void DrawText(DrawingContext dc, string text, double size, Brush brush, Point point)
    {
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, CaptionFont, size, brush, 1), point);
    }

    private static void DrawSmallSparkle(DrawingContext dc, Point center, double size, Brush brush)
    {
        var pen = new Pen(brush, 2);
        dc.DrawLine(pen, new Point(center.X - size, center.Y), new Point(center.X + size, center.Y));
        dc.DrawLine(pen, new Point(center.X, center.Y - size), new Point(center.X, center.Y + size));
    }

    private static Geometry CreateStar()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < 10; i++)
            {
                var angle = -Math.PI / 2 + i * Math.PI / 5;
                var radius = i % 2 == 0 ? 12 : 5;
                var point = new Point(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
                if (i == 0) context.BeginFigure(point, true, true); else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze(); return geometry;
    }

    private sealed class Impact(InteractionTool tool, Point target, int sequence, double lifetime, bool super)
    {
        public InteractionTool Tool { get; } = tool;
        public Point Target { get; } = target;
        public int Sequence { get; } = sequence;
        public double Lifetime { get; } = lifetime;
        public bool Super { get; } = super;
        public double Age { get; set; }
        public string Reply { get; set; } = string.Empty;
    }

    private class SkinConfig
    {
        public string Name { get; set; } = "Codex";
        public string Image { get; set; } = "spritesheet.png";
        public int FrameWidth { get; set; } = 192;
        public int FrameHeight { get; set; } = 208;
        public int Columns { get; set; } = 8;
        public int Rows { get; set; } = 9;
        public int IdleRow { get; set; }
        public int IdleFrames { get; set; } = 6;
        public int ReactionRow { get; set; } = 5;
        public int ReactionFrames { get; set; } = 8;
        public int FrameInset { get; set; }
        public double Fps { get; set; } = 8;
    }

    private sealed class DoubaoSkinConfig : SkinConfig
    {
        public DoubaoSkinConfig()
        {
            Name = "豆包"; Image = "doubao-spritesheet.png";
            Columns = 4; Rows = 2; IdleRow = 0; IdleFrames = 4; ReactionRow = 1; ReactionFrames = 4;
            FrameInset = 8;
        }
        public double IdleHoldSeconds { get; set; } = 2.8;
        public double BlinkDurationSeconds { get; set; } = .4;
        public double ReactionFrameSeconds { get; set; } = .18;
    }
}

internal static class DrawingDetails
{
    private static readonly Geometry Highlight = Geometry.Parse("M -17,7 C -10,3 12,4 18,7 M -9,-9 C -4,-12 5,-13 10,-10");
    public static void DrawBezierEquivalent(this DrawingContext dc, Brush brush) => dc.DrawGeometry(null, new Pen(brush, 3), Highlight);
}
