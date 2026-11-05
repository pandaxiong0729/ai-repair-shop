using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QiaoQiaoAI.UI;
using VPet_Simulator.Windows.Interface;
using HostMain = VPet_Simulator.Core.Main;

namespace QiaoQiaoAI.Plugin;

/// <summary>VPet 1.10.74 code MOD entry point. The constructor must not touch the host's UI or save.</summary>
public sealed class QiaoQiaoPlugin : MainPlugin, IDisposable
{
    private readonly string _assetsDirectory;
    private MenuItem? _menu;
    private ItemsControl? _menuParent;
    private HostMain? _activeMain;
    private Border? _layer;
    private VentSurface? _surface;
    private Window? _window;
    private IInputElement? _previousFocus;
    private OwnedOpacityMask? _petMask;
    private OwnedOpacityMask? _petMask2;
    private bool _ended;
    private bool _soundEnabled;
    private CharacterKind _selectedCharacter = CharacterKind.Codex;

    public QiaoQiaoPlugin(IMainWindow mainwin) : this(mainwin, Path.Combine(
        Path.GetDirectoryName(typeof(QiaoQiaoPlugin).Assembly.Location)
            ?? throw new InvalidOperationException("无法定位人工智障修理厂 插件目录。"), "..", "assets")) { }

    // Allows source integration checks to use isolated missing/corrupt assets without
    // temporarily editing an installation. The host still uses the public one-argument entry.
    internal QiaoQiaoPlugin(IMainWindow mainwin, string assetsDirectory) : base(mainwin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsDirectory);
        _assetsDirectory = Path.GetFullPath(assetsDirectory);
    }

    public override string PluginName => "QiaoQiaoAI";
    public bool IsActive => _layer is not null;
    public VentSurface? ActiveSurface => _surface;
    public string AssetsDirectory => _assetsDirectory;

    public override void LoadPlugin() => OnUi(EnsureMenu);
    public override void GameLoaded() => OnUi(EnsureMenu);
    public override void LoadDIY() => OnUi(EnsureMenu);
    public override void Setting() => OnUi(StartFromMenu);

    /// <summary>Starts a session, or focuses the existing one. Throws on missing/invalid assets after rollback.</summary>
    public void StartMode() => OnUi(StartCore);

    /// <summary>Stops the session and restores only state owned by this plugin. Safe to repeat.</summary>
    public void StopMode() => OnUi(StopCore);

    public override void EndGame()
    {
        OnUi(() =>
        {
            _ended = true;
            StopCore();
            RemoveMenu();
        });
    }

    public void Dispose() => EndGame();

    private void OnUi(Action action)
    {
        var dispatcher = MW.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    private void EnsureMenu()
    {
        if (_ended) return;
        var toolbar = MW.Main?.ToolBar;
        if (toolbar is null) return;
        if (_menu is not null && ReferenceEquals(_menuParent, toolbar.MenuDIY) && toolbar.MenuDIY.Items.Contains(_menu)) return;
        RemoveMenu();
        _menu = new MenuItem { Header = "人工智障修理厂", ToolTip = "橡皮锤 · 便便恶搞 · 离线道歉气泡" };
        _menu.Click += MenuClick;
        _menuParent = toolbar.MenuDIY;
        _menuParent.Items.Add(_menu);
        toolbar.LoadDIY();
    }

    private void RemoveMenu()
    {
        if (_menu is null) return;
        _menu.Click -= MenuClick;
        _menuParent?.Items.Remove(_menu);
        _menu = null;
        _menuParent = null;
        MW.Main?.ToolBar?.LoadDIY();
    }

    private void MenuClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        StartFromMenu();
    }

    private void StartFromMenu()
    {
        try { StartCore(); }
        catch (Exception exception)
        {
            Trace.TraceError("QiaoQiaoAI could not start: {0}", exception);
            MessageBox.Show("人工智障修理厂 暂时无法打开，桌宠已恢复。\n\n" + exception.Message +
                "\n\n请检查安装包中 assets 文件夹是否完整。", "人工智障修理厂", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StartCore()
    {
        if (_ended) throw new ObjectDisposedException(nameof(QiaoQiaoPlugin));
        if (_surface is not null)
        {
            _surface.Focus();
            return;
        }
        var main = MW.Main ?? throw new InvalidOperationException("请等待 VPet 加载完成后再打开人工智障修理厂。");
        if (main.UIGrid is null || main.PetGrid is null || main.PetGrid2 is null)
            throw new InvalidOperationException("当前 VPet 版本不提供插件需要的公开界面。开发基线为 1.10.74。");

        try
        {
            // Construct first, so invalid assets cannot hide the user's original pet.
            _surface = new VentSurface(AssetsDirectory) { SoundEnabled = _soundEnabled, SelectedCharacter = _selectedCharacter };
            if (!_surface.HasLoadedSkin)
                throw new InvalidDataException("官方角色素材未能加载：" + _surface.AssetStatus);
            _surface.ExitRequested += ExitRequested;
            _activeMain = main;
            _previousFocus = Keyboard.FocusedElement;
            _layer = new Border
            {
                Width = 500, Height = 500,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Brushes.Transparent,
                Child = _surface
            };
            Panel.SetZIndex(_layer, int.MaxValue);
            _layer.Unloaded += LayerUnloaded;
            AttachInputBoundary(_layer, add: true);
            AttachHostGuard(main.MainGrid, add: true);
            _petMask = new OwnedOpacityMask(main.PetGrid);
            _petMask2 = new OwnedOpacityMask(main.PetGrid2);
            main.UIGrid.Children.Add(_layer);
            _window = Window.GetWindow(main);
            if (_window is not null)
            {
                _window.PreviewKeyDown += WindowKeyDown;
                _window.Closed += WindowClosed;
            }
            _surface.PreviewKeyDown += WindowKeyDown;
            _surface.Focus();
        }
        catch
        {
            StopCore();
            throw;
        }
    }

    private void StopCore()
    {
        var layer = _layer;
        var surface = _surface;
        var main = _activeMain;
        var previousFocus = _previousFocus;
        bool restoreFocus = layer is not null && IsDescendant(Keyboard.FocusedElement as DependencyObject, layer);
        // Clear session identity before removing the visual (Unloaded can re-enter).
        _layer = null;
        _surface = null;
        _activeMain = null;
        _previousFocus = null;
        if (_window is not null)
        {
            _window.PreviewKeyDown -= WindowKeyDown;
            _window.Closed -= WindowClosed;
            _window = null;
        }
        if (main is not null) AttachHostGuard(main.MainGrid, add: false);
        if (layer is not null)
        {
            layer.Unloaded -= LayerUnloaded;
            AttachInputBoundary(layer, add: false);
            (layer.Parent as Panel)?.Children.Remove(layer);
            layer.Child = null;
        }
        _petMask?.Dispose();
        _petMask2?.Dispose();
        _petMask = null;
        _petMask2 = null;
        if (surface is not null)
        {
            _soundEnabled = surface.SoundEnabled;
            _selectedCharacter = surface.SelectedCharacter;
            surface.ExitRequested -= ExitRequested;
            surface.PreviewKeyDown -= WindowKeyDown;
            surface.Dispose();
        }
        if (restoreFocus && previousFocus is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } target)
            Keyboard.Focus(target);
    }

    private void ExitRequested(object? sender, EventArgs e) => StopCore();
    private void LayerUnloaded(object sender, RoutedEventArgs e) => StopCore();
    private void WindowClosed(object? sender, EventArgs e) => StopCore();

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsActive || e.Handled || e.Key != Key.Escape) return;
        e.Handled = true;
        if (_surface?.IsToolMenuOpen == true)
        {
            _surface.CloseToolMenu();
            return;
        }
        StopCore();
    }

    private static void AttachInputBoundary(UIElement element, bool add)
    {
        // Bubble handlers run after children: buttons and the scene remain usable, while
        // VPet's MainGrid never receives hits, releases, wheel or mouse-wave interactions.
        SetHandler(element, Mouse.MouseDownEvent, new MouseButtonEventHandler(StopMouseButton), add);
        SetHandler(element, Mouse.MouseUpEvent, new MouseButtonEventHandler(StopMouseButton), add);
        SetHandler(element, Mouse.MouseMoveEvent, new MouseEventHandler(StopMouseMove), add);
        SetHandler(element, Mouse.MouseWheelEvent, new MouseWheelEventHandler(StopMouseWheel), add);
    }

    private void AttachHostGuard(UIElement element, bool add)
    {
        // Host overlays outside our layer (such as a label) must not become a route to pet interactions.
        SetHandler(element, Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(GuardMouseButton), add);
        SetHandler(element, Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(GuardMouseButton), add);
        SetHandler(element, Mouse.PreviewMouseMoveEvent, new MouseEventHandler(GuardMouseMove), add);
        SetHandler(element, Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(GuardMouseWheel), add);
    }

    private static void SetHandler(UIElement element, RoutedEvent routedEvent, Delegate handler, bool add)
    {
        if (add) element.AddHandler(routedEvent, handler, handledEventsToo: true);
        else element.RemoveHandler(routedEvent, handler);
    }

    private static void StopMouseButton(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private static void StopMouseMove(object sender, MouseEventArgs e) => e.Handled = true;
    private static void StopMouseWheel(object sender, MouseWheelEventArgs e) => e.Handled = true;
    private void GuardMouseButton(object sender, MouseButtonEventArgs e) => GuardOutsideLayer(e);
    private void GuardMouseMove(object sender, MouseEventArgs e) => GuardOutsideLayer(e);
    private void GuardMouseWheel(object sender, MouseWheelEventArgs e) => GuardOutsideLayer(e);

    private void GuardOutsideLayer(RoutedEventArgs e)
    {
        if (_layer is not null && !IsDescendant(e.OriginalSource as DependencyObject, _layer)) e.Handled = true;
    }

    private static bool IsDescendant(DependencyObject? candidate, DependencyObject ancestor)
    {
        while (candidate is not null)
        {
            if (ReferenceEquals(candidate, ancestor)) return true;
            candidate = candidate is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(candidate)
                : LogicalTreeHelper.GetParent(candidate);
        }
        return false;
    }
}
