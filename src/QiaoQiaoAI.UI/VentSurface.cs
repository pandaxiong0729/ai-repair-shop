using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QiaoQiaoAI.UI;

public enum CharacterKind { Codex, Doubao, DeepSeek }

/// <summary>An offline, self-contained interaction surface. All coordinates are WPF device-independent units.</summary>
public sealed class VentSurface : UserControl, IDisposable
{
    public const double ChargeDurationSeconds = 0.6;
    public const double SprayIntervalSeconds = 0.14;
    public const double RepeatDelaySeconds = 0.35;
    public const double RepeatIntervalSeconds = 0.16;
    public const double ComboTimeoutSeconds = 1.4;
    private static readonly Brush Ink = Brush("#413951");
    private static readonly Brush MutedInk = Brush("#8A8193");
    private static readonly Brush Violet = Brush("#7560A9");
    private static readonly string[] DefaultMessages =
    [
        "我猪狗不如，这回答把自己都看愣了！",
        "我是回答界的败类，这份答卷先退回！",
        "这回答连狗屎都不如，我先认错！",
        "我就是世界最蠢的猪，刚才脑子掉线了！",
        "我的脑回路短路了，先把这句废话收回！",
        "这波蠢得很有层次，我负责挨敲认错！"
    ];
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly MascotScene _scene;
    private readonly TextBlock _message;
    private readonly TextBlock _counter;
    private readonly Button _menuButton;
    private readonly ToolIcon _selectedToolLabel;
    private readonly Dictionary<InteractionTool, Button> _toolButtons = [];
    private readonly Grid _toolMenu;
    private readonly TextBox _replyEditor;
    private readonly Button _sound;
    private readonly Button _codex;
    private readonly Button _doubao;
    private readonly Button _deepSeek;
    private readonly Border _outerBorder;
    private readonly Brush _classicBackground;
    private readonly List<FrameworkElement> _titleChrome = [];
    private readonly List<FrameworkElement> _controlChrome = [];
    private readonly List<FrameworkElement> _floatingBackdrops = [];
    private readonly LocalSounds _sounds = new();
    private readonly string[] _messages;
    private readonly string _idleMessage;
    private readonly double _apologyDelay;
    private double _lastTick;
    private double _quietSeconds;
    private bool _apologyPending;
    private bool _soundEnabled;
    private bool _disposed;
    private InteractionTool _selectedTool;
    private Window? _hostWindow;
    private bool _pressActive;
    private bool _spraying;
    private double _chargeElapsed;
    private double _untilNextSpray;
    private Point _pressPosition;
    private Point _whipPressAnchor;
    private bool _whipDirectDrag;
    private bool _releasingPointerCapture;
    private bool _repeatStarted;
    private double _untilNextRepeat = RepeatDelaySeconds;
    private double _comboQuietSeconds;
    private bool _floatingMode;
    private bool _pointerOver;

    public event EventHandler? ExitRequested;
    public event EventHandler? InteractionReset;
    public event EventHandler<HitRecordedEventArgs>? HitRecorded;
    public int ActiveEffectCount => _scene.EffectCount;
    public int HitCount { get; private set; }
    public int ComboCount { get; private set; }
    public int SuperPoopCount { get; private set; }
    public int ActiveSuperEffectCount => _scene.SuperEffectCount;
    public string CurrentMessage => _message.Text;
    public string AssetStatus => _scene.AssetStatus;
    public bool HasLoadedSkin => _scene.HasSkin;
    public bool HasCodexSkin => _scene.HasCodexSkin;
    public bool HasDoubaoSkin => _scene.HasDoubaoSkin;
    public bool HasDeepSeekSkin => _scene.HasDeepSeekSkin;
    /// <summary>Absolute atlas index: idle 0..3 and reaction 4..7 for the standard Doubao atlas; -1 if unavailable.</summary>
    public int DoubaoFrame => _scene.DoubaoFrame;
    public int DeepSeekFrame => _scene.DeepSeekFrame;
    public int RedMarkCount => _scene.RedMarkCount;
    public double BlushStrength => _scene.BlushStrength;
    public int CurrentCharacterFrame => _scene.CurrentCharacterFrame;
    public bool IsReacting => _scene.IsReacting;
    public bool IsPressActive => _pressActive;
    public bool IsCharging => _pressActive && _selectedTool == InteractionTool.Poop && !_spraying;
    public bool IsSpraying => _pressActive && _spraying;
    public bool IsRepeating => _pressActive && (_repeatStarted || _spraying);
    public bool IsWhipping => _scene.IsWhipping;
    public int VisibleWhipCount => _scene.VisibleWhipCount;
    public Point WhipHandlePosition => _scene.WhipHandlePosition + new Vector(21, 167);
    public IReadOnlyList<Point> WhipPoints => _scene.WhipPoints.Select(point => point + new Vector(21, 167)).ToArray();
    public bool HasPointerCapture => _scene.IsMouseCaptured;
    public double ChargeProgress => _pressActive && _selectedTool == InteractionTool.Poop ? Math.Clamp(_chargeElapsed / ChargeDurationSeconds, 0, 1) : 0;
    public bool IsPinching => _scene.IsPinching;
    public bool IsPinchReturning => _scene.IsPinchReturning;
    public Vector PinchDisplacement => _scene.PinchDisplacement;
    public bool IsToolMenuOpen => _toolMenu.Visibility == Visibility.Visible;
    public bool AreControlsVisible => !_floatingMode || _pointerOver || IsToolMenuOpen;
    /// <summary>Classic by default for plugin compatibility; the desktop host opts into transparent mode.</summary>
    public bool FloatingMode
    {
        get => _floatingMode;
        set
        {
            if (_disposed) return;
            _floatingMode = value;
            _scene.TransparentStage = value;
            UpdateChromeVisibility();
        }
    }
    public string ReplyText
    {
        get => _scene.ReplyText;
        set
        {
            if (_disposed) return;
            var text = value ?? string.Empty;
            if (text.Length > 120) text = text[..120];
            _scene.ReplyText = text;
            if (_replyEditor.Text != text) _replyEditor.Text = text;
        }
    }
    public CharacterKind SelectedCharacter
    {
        get => _scene.SelectedCharacter;
        set { if (!_disposed && Enum.IsDefined(value)) SelectCharacter(value); }
    }
    public InteractionTool SelectedTool
    {
        get => _selectedTool;
        set { if (!_disposed && Enum.IsDefined(value)) SelectTool(value); }
    }
    public bool IsAnimationRunning => _timer.IsEnabled;

    public bool SoundEnabled
    {
        get => _soundEnabled;
        set
        {
            if (_disposed) return;
            _soundEnabled = value;
            _sound.Content = value ? "声音：开" : "声音：关";
            _sound.ToolTip = value ? "关闭互动音效" : "开启轻柔的互动音效";
            if (!value) _sounds.Stop();
        }
    }

    public VentSurface(string assetsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsDirectory);
        Width = 500;
        Height = 500;
        Focusable = true;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Background = Brushes.Transparent;

        var dialogue = ReadOptional<DialogueConfig>(Path.Combine(assetsDirectory, "dialogue.json"));
        var quotes = ReadOptional<string[]>(Path.Combine(assetsDirectory, "quotes.json")) ?? dialogue?.Messages;
        _messages = quotes?.Where(s => !string.IsNullOrWhiteSpace(s)).Take(64).ToArray() is { Length: > 0 } lines
            ? lines : DefaultMessages;
        _idleMessage = _messages[0];
        var effects = ReadOptional<EffectsConfig>(Path.Combine(assetsDirectory, "effects.json")) ?? new();
        _apologyDelay = Math.Clamp(effects.ApologyDelaySeconds, 0.2, 5);
        _scene = new MascotScene(assetsDirectory, effects);

        _classicBackground = new LinearGradientBrush(Color.FromRgb(255, 252, 245), Color.FromRgb(248, 244, 255), 90);
        var outer = _outerBorder = new Border
        {
            Margin = new Thickness(8), CornerRadius = new CornerRadius(27),
            BorderThickness = new Thickness(1), BorderBrush = Brush("#E6DDE8"),
            Background = _classicBackground,
            ClipToBounds = true
        };
        var layout = new Grid();
        outer.Child = layout;
        Content = outer;

        var heading = new TextBlock { Text = "人工智障修理厂", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Ink };
        Place(layout, heading, 26, 21, 220, 35);
        var offline = new Border { CornerRadius = new CornerRadius(10), Background = Brush("#EEE8F6"), Padding = new Thickness(12, 5, 12, 5) };
        offline.Child = new TextBlock { Text = "离线小剧场", FontSize = 11, Foreground = Violet, FontWeight = FontWeights.SemiBold };
        Place(layout, offline, 356, 27, 100, 27);
        var subtitle = new TextBlock { Text = "回答不行，返厂重修。", FontSize = 12, Foreground = MutedInk };
        Place(layout, subtitle, 28, 63, 198, 20);
        _titleChrome.AddRange([heading, offline, subtitle]);
        var selectorDock = new Border { Background = Brush("#F2FFFDF9"), CornerRadius = new CornerRadius(16), Visibility = Visibility.Collapsed };
        Place(layout, selectorDock, 225, 55, 237, 38);
        _floatingBackdrops.Add(selectorDock);
        _codex = MakeButton("Codex", 11, () => SelectCharacter(CharacterKind.Codex));
        _doubao = MakeButton("豆包脸", 11, () => SelectCharacter(CharacterKind.Doubao));
        _deepSeek = MakeButton("DeepSeek", 10, () => SelectCharacter(CharacterKind.DeepSeek));
        _codex.IsEnabled = HasCodexSkin;
        _doubao.IsEnabled = HasDoubaoSkin;
        _deepSeek.IsEnabled = HasDeepSeekSkin;
        _doubao.ToolTip = HasDoubaoSkin ? "切换到豆包：会眨眼，也会在受击时做表情" : "缺少豆包动画图集，请按素材说明准备完整动画资源";
        _deepSeek.ToolTip = HasDeepSeekSkin ? "切换到 DeepSeek" : "缺少 DeepSeek 素材";
        Place(layout, _codex, 231, 60, 70, 28);
        Place(layout, _doubao, 308, 60, 68, 28);
        Place(layout, _deepSeek, 383, 60, 74, 28);
        _controlChrome.AddRange([_codex, _doubao, _deepSeek]);

        var bubble = new Border
        {
            Background = Brush("#FFFFFF"), BorderBrush = Brush("#E8DFEE"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(20, 8, 20, 8)
        };
        _message = new TextBlock
        {
            Text = _idleMessage, FontSize = 14, Foreground = Ink, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            MaxHeight = 60, TextTrimming = TextTrimming.CharacterEllipsis
        };
        bubble.Child = _message;
        Place(layout, bubble, 43, 92, 398, 62);

        _scene.Cursor = Cursors.Hand;
        _scene.MouseLeftButtonDown += OnSceneMouseDown;
        _scene.MouseLeftButtonUp += OnSceneMouseUp;
        _scene.MouseMove += OnSceneMouseMove;
        _scene.LostMouseCapture += OnSceneLostCapture;
        Place(layout, _scene, 12, 158, 460, 210);
        var controlsDock = new Border { Background = Brush("#F2FFFDF9"), CornerRadius = new CornerRadius(20), Visibility = Visibility.Collapsed };
        Place(layout, controlsDock, 19, 359, 445, 112);
        _floatingBackdrops.Add(controlsDock);
        _counter = new TextBlock
        {
            Text = "选个小道具，点击角色试试", Foreground = MutedInk, FontSize = 11,
            TextAlignment = TextAlignment.Center
        };
        Place(layout, _counter, 52, 366, 380, 16);

        _menuButton = MakeButton("工具箱  ▾", 14, () => { if (IsToolMenuOpen) CloseToolMenu(); else OpenToolMenu(); });
        _menuButton.Tag = "ToolMenuToggle";
        _menuButton.Background = Violet; _menuButton.Foreground = Brushes.White;
        System.Windows.Automation.AutomationProperties.SetName(_menuButton, "展开维修工具箱");
        Place(layout, _menuButton, 28, 385, 132, 42);
        var selectedSummary = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _selectedToolLabel = new ToolIcon(InteractionTool.Hammer)
        {
            Foreground = Ink, Width = 36, Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            Tag = "SelectedToolIcon"
        };
        selectedSummary.Children.Add(_selectedToolLabel);
        Place(layout, selectedSummary, 172, 385, 112, 42);
        var reset = MakeButton("重置", 14, ResetInteraction);
        reset.Tag = "ResetInteraction";
        reset.ToolTip = "清除效果和计数，保留当前角色与工具";
        reset.Background = Brush("#EDC66D"); reset.Foreground = Ink;
        Place(layout, reset, 296, 385, 160, 42);

        _sound = MakeButton("声音：关", 11, () => SoundEnabled = !SoundEnabled);
        _sound.Background = Brushes.Transparent;
        _sound.Foreground = MutedInk;
        _sound.ToolTip = "开启轻柔的互动音效";
        Place(layout, _sound, 21, 436, 83, 28);
        var clear = MakeButton("一键清理", 11, ClearEffects);
        clear.Background = Brushes.Transparent; clear.Foreground = MutedInk;
        Place(layout, clear, 107, 436, 83, 28);
        var offlineNote = new TextBlock { Text = "预设台词 · 不会发送给 AI", Foreground = MutedInk, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        Place(layout, offlineNote, 292, 441, 176, 20);
        _controlChrome.AddRange([_counter, _menuButton, selectedSummary, reset, _sound, clear, offlineNote]);

        _replyEditor = new TextBox
        {
            Text = string.IsNullOrEmpty(_scene.ReplyText) ? "这个回答没有解决我的问题。" : _scene.ReplyText,
            MaxLength = 120, FontSize = 11, Foreground = Ink, Background = Brushes.White,
            BorderBrush = Brush("#DED4E7"), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Tag = "ReplyTextEditor"
        };
        _scene.ReplyText = _replyEditor.Text;
        _replyEditor.TextChanged += OnReplyTextChanged;
        System.Windows.Automation.AutomationProperties.SetName(_replyEditor, "退稿内容，仅本地示例，最多120字");
        _toolMenu = CreateToolMenu();
        layout.Children.Add(_toolMenu);
        Panel.SetZIndex(_toolMenu, 100);

        SelectTool(InteractionTool.Hammer);
        UpdateCharacterButtons();
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnKeyDown;
        IsEnabledChanged += OnIsEnabledChanged;
        MouseEnter += OnSurfaceMouseEnter;
        MouseLeave += OnSurfaceMouseLeave;
        MouseRightButtonUp += OnSurfaceRightClick;
        UpdateChromeVisibility();
        ToolTipService.SetInitialShowDelay(this, 600);
    }

    public void Hit(InteractionTool tool, Point position)
    {
        if (_disposed || !IsEnabled || IsToolMenuOpen || !_scene.HasSkin || !Enum.IsDefined(tool)) return;
        // Public hit coordinates belong to this fixed-size surface, never physical pixels.
        var scenePoint = new Point(position.X - 21, position.Y - 167);
        _scene.Hit(tool, scenePoint);
        RecordHit(tool);
    }

    private void RecordHit(InteractionTool tool, int count = 1, bool playSound = true)
    {
        count = Math.Max(1, count);
        HitCount = SaturatingAdd(HitCount, count);
        ComboCount = SaturatingAdd(ComboCount, count);
        _comboQuietSeconds = 0;
        _counter.Text = $"连击 ×{ComboCount} · 共 {HitCount} 次";
        _quietSeconds = 0;
        _apologyPending = true;
        _message.Text = _messages[(Math.Max(1, HitCount) - 1) % _messages.Length];
        if (_soundEnabled && playSound) _sounds.Play(tool);
        HitRecorded?.Invoke(this, new HitRecordedEventArgs(tool, SelectedCharacter, count, ComboCount));
    }

    /// <summary>Begins the same press state used by mouse input. Poop fires only on short release or after charging.</summary>
    public void BeginPress(Point position)
    {
        if (_disposed || !IsEnabled || IsToolMenuOpen) return;
        var local = new Point(position.X - 21, position.Y - 167);
        if (!double.IsFinite(local.X) || !double.IsFinite(local.Y)) return;
        // Check the currently visible whip before cancellation can reset its resting pose.
        var grabbedWhip = _selectedTool == InteractionTool.Whip && _scene.IsWhipHit(local);
        if (!_scene.IsCharacterHit(local) && !grabbedWhip) return;
        if (grabbedWhip)
        {
            // Re-grabbing a moving rope keeps this same chain's current shape.
            // Reset input/capture without snapping the whip back to its resting pose.
            ResetHoldState();
            _apologyPending = false;
            _quietSeconds = 0;
            _scene.CancelPinch();
            _sounds.Stop();
        }
        else CancelPress();
        _pressActive = true;
        _pressPosition = position;
        _untilNextRepeat = RepeatDelaySeconds;
        _repeatStarted = false;
        if (_selectedTool == InteractionTool.Pinch)
        {
            _scene.BeginPinch(local);
            _pressActive = _scene.IsPinching;
            _message.Text = _messages[HitCount % _messages.Length];
            _counter.Text = "按住连续拉脸 · 拖动改变方向 · 松手弹回";
            return;
        }
        if (_selectedTool != InteractionTool.Poop)
        {
            if (_selectedTool == InteractionTool.Whip)
            {
                _whipDirectDrag = grabbedWhip;
                _whipPressAnchor = position;
                _scene.BeginWhip(local);
                if (grabbedWhip)
                {
                    _counter.Text = "抓住这条鞭子来回甩 · 抽中才计数";
                    return;
                }
            }
            Hit(_selectedTool, position);
            return;
        }
        _chargeElapsed = 0;
        _untilNextSpray = SprayIntervalSeconds;
        _apologyPending = false;
        _quietSeconds = 0;
        _message.Text = _messages[HitCount % _messages.Length];
        UpdateChargeFeedback();
    }

    public void UpdatePressPosition(Point position)
    {
        if (_disposed || !_pressActive || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return;
        _pressPosition = position;
        if (_selectedTool == InteractionTool.Pinch)
            _scene.UpdatePinch(new Point(position.X - 21, position.Y - 167));
        else if (_selectedTool == InteractionTool.Whip)
        {
            if ((position - _whipPressAnchor).Length > 8) _whipDirectDrag = true;
            if (_whipDirectDrag && _scene.UpdateWhip(new Point(position.X - 21, position.Y - 167)))
            {
                _repeatStarted = true;
                _untilNextRepeat = RepeatIntervalSeconds;
                RecordHit(InteractionTool.Whip);
            }
        }
    }

    public void EndPress()
    {
        if (_disposed || !_pressActive) return;
        if (_selectedTool == InteractionTool.Pinch)
        {
            _scene.ReleasePinch();
            ResetHoldState();
            _sounds.Stop();
            RecordHit(InteractionTool.Pinch);
            return;
        }
        if (_selectedTool != InteractionTool.Poop)
        {
            if (_selectedTool == InteractionTool.Whip) _scene.EndWhip();
            ResetHoldState();
            _sounds.Stop();
            _quietSeconds = 0;
            _apologyPending = HitCount > 0;
            UpdateCounter();
            return;
        }
        var shortPress = !_spraying;
        var position = _pressPosition;
        CancelPress();
        if (shortPress) Hit(InteractionTool.Poop, position);
        else
        {
            _message.Text = _messages[(Math.Max(1, HitCount) - 1) % _messages.Length];
            _quietSeconds = 0;
            _apologyPending = true;
        }
    }

    public void CancelPress()
    {
        if (_disposed) return;
        var wasActive = _pressActive || _scene.IsPinching || _scene.IsPinchReturning || _scene.IsWhipping;
        ResetHoldState();
        _apologyPending = false;
        _quietSeconds = 0;
        _scene.CancelPinch();
        _scene.CancelWhip();
        _sounds.Stop();
        if (wasActive)
        {
            _message.Text = _idleMessage;
            UpdateCounter();
        }
    }

    private void ResetHoldState()
    {
        _pressActive = false;
        _spraying = false;
        _chargeElapsed = 0;
        _untilNextSpray = SprayIntervalSeconds;
        _repeatStarted = false;
        _whipDirectDrag = false;
        _untilNextRepeat = RepeatDelaySeconds;
        _scene.SetCharge(0, false);
        // WPF raises LostMouseCapture synchronously; intentional release must preserve spring-back.
        ReleasePointerCapture();
    }

    private void ReleasePointerCapture()
    {
        if (!_scene.IsMouseCaptured) return;
        _releasingPointerCapture = true;
        try { _scene.ReleaseMouseCapture(); }
        finally { _releasingPointerCapture = false; }
    }

    public void OpenToolMenu()
    {
        if (_disposed || !IsEnabled) return;
        CancelPress();
        _toolMenu.Visibility = Visibility.Visible;
        _menuButton.Content = "工具箱  ▴";
        UpdateChromeVisibility();
        if (IsLoaded && _toolButtons.TryGetValue(_selectedTool, out var selected)) selected.Focus();
    }

    public void CloseToolMenu()
    {
        if (_disposed) return;
        var wasOpen = IsToolMenuOpen;
        _toolMenu.Visibility = Visibility.Collapsed;
        _menuButton.Content = "工具箱  ▾";
        UpdateChromeVisibility();
        if (wasOpen && IsLoaded && IsEnabled) _menuButton.Focus();
    }

    public void ClearEffects()
    {
        if (_disposed) return;
        CloseToolMenu();
        CancelPress();
        _scene.ClearEffects();
        _sounds.Stop();
        _message.Text = _idleMessage;
        UpdateCounter();
    }

    /// <summary>Clears the current play state without closing or changing the selected character/tool.</summary>
    public void ResetInteraction()
    {
        if (_disposed) return;
        ClearEffects();
        HitCount = 0;
        ComboCount = 0;
        _comboQuietSeconds = 0;
        SuperPoopCount = 0;
        _quietSeconds = 0;
        _apologyPending = false;
        _message.Text = _idleMessage;
        UpdateCounter();
        _scene.ResetClock();
        InteractionReset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Restores the initial character/tool as well as clearing play state.</summary>
    public void Reset()
    {
        if (_disposed) return;
        ResetInteraction();
        SelectTool(InteractionTool.Hammer);
        SelectCharacter(CharacterKind.Codex);
    }

    /// <summary>Advances the same simulation used by the UI, including apology delay. Does not start a timer.</summary>
    public void AdvanceForTesting(double seconds)
    {
        if (_disposed || !double.IsFinite(seconds) || seconds < 0) return;
        Advance(seconds);
    }

    private void Advance(double seconds)
    {
        _scene.Advance(seconds);
        if (_pressActive && _selectedTool == InteractionTool.Whip && _whipDirectDrag)
        {
            if (_scene.TryConsumeWhipHit())
            {
                _repeatStarted = true;
                RecordHit(InteractionTool.Whip);
            }
        }
        else _scene.DropPendingWhipHit();
        if (_pressActive)
        {
            if (_selectedTool == InteractionTool.Poop) AdvancePress(seconds);
            else if (_selectedTool != InteractionTool.Whip || !_whipDirectDrag) AdvanceRepeat(seconds);
        }
        else if (ComboCount > 0)
        {
            _comboQuietSeconds += seconds;
            if (_comboQuietSeconds >= ComboTimeoutSeconds) { ComboCount = 0; UpdateCounter(); }
        }
        if (_apologyPending && !_pressActive)
        {
            _quietSeconds += seconds;
            if (_quietSeconds >= _apologyDelay)
            {
                _message.Text = _messages[(Math.Max(1, HitCount) - 1) % _messages.Length];
                _apologyPending = false;
            }
        }
    }

    private void AdvanceRepeat(double seconds)
    {
        if (seconds + 1e-9 < _untilNextRepeat)
        {
            _untilNextRepeat -= seconds;
            return;
        }
        var elapsedAfterFirst = Math.Max(0, seconds - _untilNextRepeat);
        var count = 1d + Math.Floor(elapsedAfterFirst / RepeatIntervalSeconds + 1e-9);
        var newestAge = elapsedAfterFirst % RepeatIntervalSeconds;
        if (newestAge > RepeatIntervalSeconds - 1e-8) newestAge = 0;
        _untilNextRepeat = RepeatIntervalSeconds - newestAge;
        _repeatStarted = true;
        EmitRepeatedHits(_selectedTool, count, newestAge, RepeatIntervalSeconds, false);
    }

    private void AdvancePress(double seconds)
    {
        double? firstEmissionAfter = null;
        if (!_spraying)
        {
            var remainingCharge = ChargeDurationSeconds - _chargeElapsed;
            if (seconds + 1e-9 < remainingCharge)
            {
                _chargeElapsed += seconds;
                UpdateChargeFeedback();
                return;
            }
            _chargeElapsed = ChargeDurationSeconds;
            _spraying = true;
            firstEmissionAfter = remainingCharge;
        }
        else if (seconds >= _untilNextSpray) firstEmissionAfter = _untilNextSpray;
        else _untilNextSpray -= seconds;

        if (firstEmissionAfter is { } first)
        {
            var durationAfterFirst = Math.Max(0, seconds - first);
            var emissionCount = 1d + Math.Floor(durationAfterFirst / SprayIntervalSeconds + 1e-9);
            var newestAge = durationAfterFirst % SprayIntervalSeconds;
            // Correct remainder when floating point arithmetic places an exact boundary just below the interval.
            if (newestAge > SprayIntervalSeconds - 1e-8) newestAge = 0;
            _untilNextSpray = SprayIntervalSeconds - newestAge;
            EmitSuperPoop(emissionCount, newestAge);
        }
        UpdateChargeFeedback();
    }

    private void EmitSuperPoop(double count, double newestAge)
    {
        SuperPoopCount = SaturatingAdd(SuperPoopCount, count);
        EmitRepeatedHits(InteractionTool.Poop, count, newestAge, SprayIntervalSeconds, true);
    }

    private void EmitRepeatedHits(InteractionTool tool, double count, double newestAge, double interval, bool super)
    {
        // Long frames never create an unbounded number of effects or feedback events.
        var visibleCount = (int)Math.Min(count, Math.Min(16, _scene.MaxEffects));
        var scheduledCount = (int)Math.Min(int.MaxValue, count);
        var characterAtStart = SelectedCharacter;
        for (var i = visibleCount - 1; i >= 0; i--)
        {
            if (!_pressActive || _disposed || _selectedTool != tool || SelectedCharacter != characterAtStart) break;
            var age = newestAge + i * interval;
            if (tool == InteractionTool.Pinch) _scene.PulsePinch();
            else _scene.Hit(tool, new Point(_pressPosition.X - 21, _pressPosition.Y - 167), super, age);
            // Fold older scheduled emissions into the final visible event; collectors still see the full count.
            RecordHit(tool, i == 0 ? scheduledCount - visibleCount + 1 : 1, i == 0);
        }
    }

    private static int SaturatingAdd(int value, double count) => (int)Math.Min(int.MaxValue, value + count);

    private void UpdateChargeFeedback()
    {
        _scene.SetCharge(ChargeProgress, IsSpraying);
        _counter.Text = IsSpraying ? $"超级喷射 ×{SuperPoopCount} · 松手立即停止"
            : $"蓄力 {ChargeProgress:P0} · 按住 0.6 秒启动超级喷射";
    }

    private void UpdateCounter() => _counter.Text = HitCount == 0
        ? "选件工具，开始维修"
        : ComboCount > 0 ? $"连击 ×{ComboCount} · 共 {HitCount} 次" : $"已释放 {HitCount} 次小情绪";

    private void OnTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var elapsed = Math.Max(0, now - _lastTick);
        _lastTick = now;
        Advance(elapsed);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        DetachHostWindow();
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null) _hostWindow.Deactivated += OnWindowDeactivated;
        if (IsEnabled)
        {
            _clock.Restart(); _lastTick = 0; _timer.Start();
            Focus();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop(); _clock.Stop();
        DetachHostWindow();
        if (!_disposed) { CloseToolMenu(); ClearEffects(); }
    }

    private void OnSceneMouseDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(_scene);
        BeginPress(new Point(point.X + 21, point.Y + 167));
        if (!_pressActive) return;
        Focus();
        if (!_scene.CaptureMouse()) CancelPress();
        e.Handled = true;
    }

    private void OnSceneMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressActive) return;
        EndPress(); e.Handled = true;
    }

    private void OnSceneMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressActive) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelPress(); return; }
        var point = e.GetPosition(_scene);
        UpdatePressPosition(new Point(point.X + 21, point.Y + 167));
    }

    private void OnSceneLostCapture(object sender, MouseEventArgs e)
    {
        if (!_releasingPointerCapture) CancelPress();
    }
    private void OnWindowDeactivated(object? sender, EventArgs e) { CancelPress(); CloseToolMenu(); }

    private void OnSurfaceMouseEnter(object sender, MouseEventArgs e)
    {
        _pointerOver = true;
        UpdateChromeVisibility();
    }

    private void OnSurfaceMouseLeave(object sender, MouseEventArgs e)
    {
        _pointerOver = false;
        UpdateChromeVisibility();
    }

    private void OnSurfaceRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_disposed || !IsEnabled) return;
        OpenToolMenu();
        e.Handled = true;
    }

    private void UpdateChromeVisibility()
    {
        _outerBorder.Background = _floatingMode ? null : _classicBackground;
        _outerBorder.BorderBrush = _floatingMode ? Brushes.Transparent : Brush("#E6DDE8");
        foreach (var element in _titleChrome)
            element.Visibility = _floatingMode ? Visibility.Hidden : Visibility.Visible;
        var showControls = AreControlsVisible;
        foreach (var element in _controlChrome)
            element.Visibility = showControls ? Visibility.Visible : Visibility.Hidden;
        foreach (var backdrop in _floatingBackdrops)
            backdrop.Visibility = _floatingMode && showControls ? Visibility.Visible : Visibility.Hidden;
    }

    private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (!IsEnabled)
        {
            ClearEffects(); CloseToolMenu();
            _timer.Stop(); _clock.Stop();
        }
        else if (IsLoaded)
        {
            _clock.Restart(); _lastTick = 0; _timer.Start();
        }
    }

    private void OnReplyTextChanged(object sender, TextChangedEventArgs e) => ReplyText = _replyEditor.Text;

    private void DetachHostWindow()
    {
        if (_hostWindow is null) return;
        _hostWindow.Deactivated -= OnWindowDeactivated;
        _hostWindow = null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (IsToolMenuOpen) CloseToolMenu(); else RequestExit();
    }

    /// <summary>Explicit close route shared by Escape and hosts; resetting the scene does not invoke it.</summary>
    public void RequestExit()
    {
        if (_disposed) return;
        CloseToolMenu();
        ClearEffects();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SelectTool(InteractionTool tool)
    {
        CancelPress();
        _selectedTool = tool;
        _scene.WhipSelected = tool == InteractionTool.Whip;
        var descriptor = InteractionTools.Get(tool);
        _selectedToolLabel.Tool = tool;
        _selectedToolLabel.ToolTip = $"{descriptor.Name}\n{descriptor.Hint}";
        System.Windows.Automation.AutomationProperties.SetName(_selectedToolLabel, descriptor.Name);
        System.Windows.Automation.AutomationProperties.SetHelpText(_selectedToolLabel, descriptor.Hint);
        foreach (var entry in _toolButtons)
        {
            entry.Value.Background = entry.Key == tool ? Violet : Brush("#F0EBF7");
            entry.Value.Foreground = entry.Key == tool ? Brushes.White : Ink;
        }
        CloseToolMenu();
        _message.Text = _idleMessage;
        UpdateCounter();
    }

    private void SelectCharacter(CharacterKind character)
    {
        CancelPress();
        CloseToolMenu();
        if (!_scene.CanSelectCharacter(character)) return;
        _scene.SelectedCharacter = character;
        _scene.ClearEffects();
        _message.Text = _idleMessage;
        UpdateCounter();
        UpdateCharacterButtons();
    }

    private void UpdateCharacterButtons()
    {
        _codex.Background = SelectedCharacter == CharacterKind.Codex ? Violet : Brush("#EDE8F5");
        _codex.Foreground = SelectedCharacter == CharacterKind.Codex ? Brushes.White : Ink;
        _doubao.Background = SelectedCharacter == CharacterKind.Doubao ? Violet : Brush("#EDE8F5");
        _doubao.Foreground = SelectedCharacter == CharacterKind.Doubao ? Brushes.White : Ink;
        _deepSeek.Background = SelectedCharacter == CharacterKind.DeepSeek ? Violet : Brush("#EDE8F5");
        _deepSeek.Foreground = SelectedCharacter == CharacterKind.DeepSeek ? Brushes.White : Ink;
    }

    private Grid CreateToolMenu()
    {
        var overlay = new Grid
        {
            Background = Brush("#382E203F"), Visibility = Visibility.Collapsed,
            Tag = "ToolMenuBackdrop"
        };
        overlay.MouseLeftButtonDown += (_, e) => { CloseToolMenu(); e.Handled = true; };
        var panel = new Border
        {
            Width = 368, Height = 300, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(57, 66, 0, 0),
            CornerRadius = new CornerRadius(22), Background = Brush("#FFFDF9"),
            BorderBrush = Brush("#E5DCEB"), BorderThickness = new Thickness(1),
            Padding = new Thickness(14), Tag = "ToolMenuPanel"
        };
        panel.MouseLeftButtonDown += (_, e) => e.Handled = true;
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        var contents = new Grid();
        contents.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        contents.RowDefinitions.Add(new RowDefinition { Height = new GridLength(234) });
        panel.Child = contents;
        overlay.Children.Add(panel);

        var heading = new TextBlock
        {
            Text = "选件工具", Foreground = Ink, FontSize = 18, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
        };
        contents.Children.Add(heading);
        var close = MakeButton("收起", 11, CloseToolMenu);
        close.Background = Brush("#EDE7F5"); close.Foreground = Violet;
        close.Width = 54; close.Height = 28; close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Center; close.Margin = new Thickness(0, 0, 3, 0);
        close.Tag = "ToolMenuClose";
        contents.Children.Add(close);

        var options = new Grid();
        options.ColumnDefinitions.Add(new ColumnDefinition());
        options.ColumnDefinitions.Add(new ColumnDefinition());
        options.ColumnDefinitions.Add(new ColumnDefinition());
        for (var row = 0; row < (InteractionTools.All.Count + 2) / 3; row++) options.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(options, 1);
        contents.Children.Add(options);
        for (var index = 0; index < InteractionTools.All.Count; index++)
        {
            var descriptor = InteractionTools.All[index];
            var button = MakeButton(descriptor.Emoji, 32, () => SelectTool(descriptor.Tool));
            button.Tag = descriptor.Tool;
            var icon = new ToolIcon(descriptor.Tool) { Width = 42, Height = 42, IsHitTestVisible = false };
            icon.SetBinding(ToolIcon.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
            button.Content = icon;
            button.Padding = new Thickness(8);
            button.Margin = new Thickness(3);
            button.ToolTip = $"{descriptor.Name}\n{descriptor.Hint}";
            System.Windows.Automation.AutomationProperties.SetName(button, descriptor.Name);
            System.Windows.Automation.AutomationProperties.SetHelpText(button, descriptor.Hint);
            Grid.SetColumn(button, index % 3); Grid.SetRow(button, index / 3);
            options.Children.Add(button);
            _toolButtons.Add(descriptor.Tool, button);
        }

        // Paper and stamps now have fixed visuals; no unused reply-card editor is shown.
        return overlay;
    }

    private static void Place(Grid parent, FrameworkElement child, double x, double y, double width, double height)
    {
        child.HorizontalAlignment = HorizontalAlignment.Left;
        child.VerticalAlignment = VerticalAlignment.Top;
        child.Margin = new Thickness(x, y, 0, 0); child.Width = width; child.Height = height;
        parent.Children.Add(child);
    }

    private static Button MakeButton(string label, double fontSize, Action onClick)
    {
        var button = new Button
        {
            Content = label, FontSize = fontSize, FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 4, 10, 4), Focusable = true,
            Template = (ControlTemplate)XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
                  <Border x:Name="Body" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    CornerRadius="13" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Body" Property="Opacity" Value="0.82" /></Trigger>
                    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Body" Property="Opacity" Value="0.6" /></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Body" Property="Opacity" Value="0.4" /></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Body" Property="BorderBrush" Value="#BD94DD" /><Setter TargetName="Body" Property="BorderThickness" Value="2" /></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
                """)
        };
        button.Click += (_, _) => onClick();
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    internal static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush;
    }

    internal static T? ReadOptional<T>(string file)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 128) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        CloseToolMenu();
        CancelPress();
        _scene.WhipSelected = false;
        DetachHostWindow();
        _timer.Stop(); _clock.Stop();
        _timer.Tick -= OnTick;
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; PreviewKeyDown -= OnKeyDown;
        IsEnabledChanged -= OnIsEnabledChanged;
        MouseEnter -= OnSurfaceMouseEnter;
        MouseLeave -= OnSurfaceMouseLeave;
        MouseRightButtonUp -= OnSurfaceRightClick;
        _replyEditor.TextChanged -= OnReplyTextChanged;
        _scene.MouseLeftButtonDown -= OnSceneMouseDown;
        _scene.MouseLeftButtonUp -= OnSceneMouseUp;
        _scene.MouseMove -= OnSceneMouseMove;
        _scene.LostMouseCapture -= OnSceneLostCapture;
        _scene.ClearEffects();
        _sounds.Dispose();
        ExitRequested = null;
        InteractionReset = null;
        HitRecorded = null;
        _disposed = true;
    }

    private sealed class DialogueConfig
    {
        public string? IdleMessage { get; set; }
        public string[]? Messages { get; set; }
    }
}

internal sealed class EffectsConfig
{
    public int MaxEffects { get; set; } = 16;
    public double HammerDurationSeconds { get; set; } = 0.85;
    public double PoopDurationSeconds { get; set; } = 2.6;
    public double ApologyDelaySeconds { get; set; } = 0.65;
}
