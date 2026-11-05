using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QiaoQiaoAI.UI;

namespace QiaoQiaoAI.Preview;

/// <summary>The resident UI delegates routing and delivery to its controller; it never sends on its own.</summary>
internal sealed class ResidentPetWindow : Window
{
    private readonly ResidentFeedbackController _controller;
    private readonly TextBlock _target;
    private readonly TextBlock _connection;
    private readonly TextBlock _delivery;
    private bool _started;
    private bool _closed;

    public VentSurface Surface { get; }
    public Button MoveHandle { get; }
    public Button ToolsHandle { get; }
    public Button CloseHandle { get; }
    public string TargetTitle => _target.Text;
    public string ConnectionStatus => _connection.Text;
    public string FeedbackStatus => _delivery.Text;

    public ResidentPetWindow(VentSurface surface, ResidentFeedbackController controller)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(controller);
        Surface = surface;
        _controller = controller;
        surface.FloatingMode = true;

        Title = "人工智障修理厂 · 常驻自动反馈";
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        Width = 410;
        Height = 500;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, area.Right - Width - 24);
        Top = Math.Max(area.Top, area.Bottom - Height - 24);

        var layout = new Grid { Background = null };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = surface });

        var footer = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(239, 250, 247, 253)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 216, 204, 228)),
            BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 7),
            Margin = new Thickness(8, 0, 8, 5), Tag = "ResidentPetControls"
        };
        var rows = new StackPanel();
        footer.Child = rows;
        var controls = new DockPanel { LastChildFill = true };
        CloseHandle = ControlButton("×", "ResidentClose", "退出桌宠；已提交到 Codex 的反馈不会撤回");
        CloseHandle.Click += OnCloseClick;
        DockPanel.SetDock(CloseHandle, Dock.Right);
        controls.Children.Add(CloseHandle);
        ToolsHandle = ControlButton("玩法", "ResidentTools", "展开全部玩法；也可以右键角色");
        ToolsHandle.Click += OnToolsClick;
        DockPanel.SetDock(ToolsHandle, Dock.Right);
        controls.Children.Add(ToolsHandle);
        MoveHandle = ControlButton("↔ 拖动", "ResidentMove", "按住移动桌宠，不产生反馈");
        MoveHandle.PreviewMouseLeftButtonDown += OnMoveHandleDown;
        DockPanel.SetDock(MoveHandle, Dock.Left);
        controls.Children.Add(MoveHandle);
        controls.Children.Add(new TextBlock
        {
            Text = "常驻 · 自动反馈", FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 5, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(102, 88, 119))
        });
        rows.Children.Add(controls);

        _target = StatusLine("ResidentTarget", 11);
        _target.FontWeight = FontWeights.SemiBold;
        _target.TextTrimming = TextTrimming.CharacterEllipsis;
        _target.TextWrapping = TextWrapping.NoWrap;
        rows.Children.Add(_target);
        _connection = StatusLine("ResidentConnection", 10);
        rows.Children.Add(_connection);
        _delivery = StatusLine("ResidentDelivery", 10);
        rows.Children.Add(_delivery);
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);
        Content = layout;

        _controller.StatusChanged += OnStatusChanged;
        Surface.ExitRequested += OnExitRequested;
        PreviewKeyDown += OnWindowKeyDown;
        Loaded += OnLoaded;
        UpdateStatus();
    }

    private static TextBlock StatusLine(string tag, double fontSize) => new()
    {
        Tag = tag, FontSize = fontSize, Margin = new Thickness(4, 4, 4, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(102, 88, 119)),
        TextWrapping = TextWrapping.Wrap
    };

    private static Button ControlButton(string label, string tag, string tooltip)
    {
        var button = new Button
        {
            Content = label, Tag = tag, ToolTip = tooltip,
            Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(8, 3, 8, 3),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(87, 69, 106)), FontSize = 11,
            Cursor = Cursors.Hand, MinHeight = 24
        };
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || _closed) return;
        _started = true;
        try { await _controller.StartAsync(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error)
        {
            if (_closed) return;
            _connection.Text = "连接尚未建立；敲打不会显示为已送达。";
            _connection.ToolTip = error.Message;
        }
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) UpdateStatus();
        else Dispatcher.BeginInvoke(new Action(UpdateStatus));
    }

    private void UpdateStatus()
    {
        if (_closed) return;
        var title = _controller.TargetTitle;
        _target.Text = string.IsNullOrWhiteSpace(title) ? "目标：尚未找到聊天" : "目标：" + title;
        _target.ToolTip = _target.Text;
        _connection.Text = _controller.ConnectionStatus;
        _delivery.Text = _controller.DeliveryStatus;
        AutomationProperties.SetName(_target, _target.Text);
        AutomationProperties.SetName(_connection, _connection.Text);
        AutomationProperties.SetName(_delivery, _delivery.Text);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnToolsClick(object sender, RoutedEventArgs e)
    {
        Surface.OpenToolMenu();
        Surface.Focus();
    }

    private void OnMoveHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Surface.CancelPress();
        Surface.CloseToolMenu();
        e.Handled = true;
        try { DragMove(); }
        catch (InvalidOperationException) { /* The pointer can be released before native dragging starts. */ }
    }

    private void OnExitRequested(object? sender, EventArgs e) => Close();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (Surface.IsToolMenuOpen) Surface.CloseToolMenu();
        else Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Surface.CancelPress();
        Surface.CloseToolMenu();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_closed)
        {
            _closed = true;
            Loaded -= OnLoaded;
            _controller.StatusChanged -= OnStatusChanged;
            Surface.ExitRequested -= OnExitRequested;
            PreviewKeyDown -= OnWindowKeyDown;
            MoveHandle.PreviewMouseLeftButtonDown -= OnMoveHandleDown;
            ToolsHandle.Click -= OnToolsClick;
            CloseHandle.Click -= OnCloseClick;
            try { _controller.Dispose(); }
            finally { Surface.Dispose(); }
        }
        base.OnClosed(e);
    }
}
