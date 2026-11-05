using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QiaoQiaoAI.UI;

namespace QiaoQiaoAI.Preview;

/// <summary>A user-launched desktop pet. It installs no startup task and changes no system settings.</summary>
internal sealed class DesktopPetWindow : Window
{
    private readonly FeedbackRound? _feedback;
    private readonly TextBlock _feedbackStatus;
    private bool _discardFeedback;
    private bool _closed;

    public VentSurface Surface { get; }
    public bool IsFeedbackMode => _feedback is not null;
    public string ModeIdentity => IsFeedbackMode ? "反馈模式 · 本地汇总" : "玩具模式 · 不发送反馈";
    public string FeedbackStatus => _feedbackStatus.Text;
    public Button MoveHandle { get; }
    public Button ToolsHandle { get; }
    public Button CloseHandle { get; }
    public Button? SendFeedbackButton { get; }
    public TextBox? ReasonEditor { get; }

    public DesktopPetWindow(VentSurface surface, FeedbackRound? feedback = null, bool classic = false)
    {
        Surface = surface;
        _feedback = feedback;
        surface.FloatingMode = !classic;
        Title = "人工智障修理厂 · " + (IsFeedbackMode ? "反馈模式" : "玩具模式");
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        Width = classic ? 620 : 410;
        Height = classic ? 700 : (IsFeedbackMode ? 520 : 450);
        MinWidth = classic ? 380 : 0;
        MinHeight = classic ? 440 : 0;
        WindowStyle = classic ? WindowStyle.SingleBorderWindow : WindowStyle.None;
        AllowsTransparency = !classic;
        ResizeMode = classic ? ResizeMode.CanResize : ResizeMode.NoResize;
        Background = classic ? new SolidColorBrush(Color.FromRgb(246, 242, 235)) : Brushes.Transparent;
        Topmost = !classic;
        ShowInTaskbar = classic;
        WindowStartupLocation = classic ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        if (!classic)
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, area.Right - Width - 24);
            Top = Math.Max(area.Top, area.Bottom - Height - 24);
        }

        var layout = new Grid { Background = null };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new Viewbox
        {
            Stretch = Stretch.Uniform,
            Margin = classic ? new Thickness(20, 16, 20, 6) : new Thickness(0),
            Child = surface
        });

        var footer = new Border
        {
            CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(239, 250, 247, 253)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 216, 204, 228)), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(8, 0, 8, 5),
            Tag = "DesktopPetControls"
        };
        var footerRows = new StackPanel();
        footer.Child = footerRows;
        var controls = new DockPanel { LastChildFill = true };
        CloseHandle = Button("×", "DesktopClose", "关闭桌宠（Esc 也可关闭）");
        CloseHandle.Click += (_, _) => Close();
        DockPanel.SetDock(CloseHandle, Dock.Right); controls.Children.Add(CloseHandle);
        ToolsHandle = Button("玩法", "DesktopTools", "展开全部玩法；也可以右键角色");
        ToolsHandle.Click += (_, _) => { surface.OpenToolMenu(); surface.Focus(); };
        DockPanel.SetDock(ToolsHandle, Dock.Right); controls.Children.Add(ToolsHandle);
        MoveHandle = Button("↔ 拖动", "DesktopMove", "按住这个手柄移动桌宠，不会触发角色互动");
        MoveHandle.PreviewMouseLeftButtonDown += OnMoveHandleDown;
        DockPanel.SetDock(MoveHandle, Dock.Left); controls.Children.Add(MoveHandle);
        controls.Children.Add(new TextBlock
        {
            Text = ModeIdentity, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 5, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(102, 88, 119)), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis
        });
        footerRows.Children.Add(controls);

        _feedbackStatus = new TextBlock
        {
            Text = IsFeedbackMode ? "本轮只在本机汇总；发送后由当前 AI 读取。" : "",
            FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(108, 94, 123)), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(3, 5, 3, 0), MaxHeight = 36,
            Visibility = IsFeedbackMode ? Visibility.Visible : Visibility.Collapsed
        };
        if (feedback is not null)
        {
            var row = new DockPanel { Margin = new Thickness(3, 5, 3, 0) };
            SendFeedbackButton = Button("发送本轮反馈", "SendFeedbackRound", "结束本轮本地记录，交给当前 AI 读取");
            SendFeedbackButton.Click += (_, _) => TryCompleteRound("send");
            DockPanel.SetDock(SendFeedbackButton, Dock.Right); row.Children.Add(SendFeedbackButton);
            ReasonEditor = new TextBox
            {
                MaxLength = 120, Tag = "FeedbackReason", Margin = new Thickness(0, 0, 7, 0), Padding = new Thickness(5, 3, 5, 3),
                FontSize = 11, VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "可选：这轮不满意的原因（最多 120 字；不需要粘贴聊天内容）"
            };
            row.Children.Add(ReasonEditor); footerRows.Children.Add(row);
            var closeMenu = new ContextMenu();
            var discard = new MenuItem { Header = "直接关闭，不保存本轮反馈" };
            discard.Click += (_, _) => { _discardFeedback = true; Close(); };
            closeMenu.Items.Add(discard);
            CloseHandle.ContextMenu = closeMenu;
        }
        footerRows.Children.Add(_feedbackStatus);
        Grid.SetRow(footer, 1); layout.Children.Add(footer);
        Content = layout;
        Surface.ExitRequested += OnExitRequested;
        PreviewKeyDown += OnWindowKeyDown;
    }

    private static Button Button(string text, string tag, string tooltip) => new()
    {
        Content = text, Tag = tag, ToolTip = tooltip,
        Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(8, 3, 8, 3),
        BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        Foreground = new SolidColorBrush(Color.FromRgb(87, 69, 106)), FontSize = 11,
        Cursor = Cursors.Hand, MinHeight = 24
    };

    public bool CompleteRound(string trigger = "send")
    {
        if (_feedback is null) return false;
        if (!_feedback.IsSealed) _feedback.Reason = ReasonEditor?.Text ?? string.Empty;
        bool wrote = _feedback.Seal(trigger);
        _feedbackStatus.Text = _feedback.TotalCount == 0 && string.IsNullOrWhiteSpace(_feedback.Reason)
            ? "本轮没有反馈，已结束。"
            : "本轮已汇总，等待当前 AI 读取。";
        if (SendFeedbackButton is not null) SendFeedbackButton.IsEnabled = false;
        if (ReasonEditor is not null) ReasonEditor.IsEnabled = false;
        return wrote;
    }

    private bool TryCompleteRound(string trigger)
    {
        try { CompleteRound(trigger); return true; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _feedbackStatus.Text = "本轮尚未保存，可重试发送：" + error.Message;
            _feedbackStatus.ToolTip = "也可右键关闭按钮，选择不保存直接关闭。";
            return false;
        }
    }

    private void OnMoveHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Surface.CancelPress(); Surface.CloseToolMenu();
        e.Handled = true;
        try { DragMove(); }
        catch (InvalidOperationException) { /* Pointer may have been released before native dragging began. */ }
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
        Surface.CancelPress(); Surface.CloseToolMenu();
        if (_discardFeedback)
        {
            try { _feedback?.Cancel(); }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Explicit discard must remain an exit route when the session cannot be written.
                // Without the marker, the receiver's bounded wait handles the abandoned round.
                _feedbackStatus.Text = "已放弃本轮；取消标记未保存，接收端可能等待至超时。";
                System.Diagnostics.Trace.TraceWarning("QiaoQiaoAI could not write the discarded round marker: {0}", error.Message);
            }
        }
        else if (!TryCompleteRound("close")) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_closed)
        {
            _closed = true;
            MoveHandle.PreviewMouseLeftButtonDown -= OnMoveHandleDown;
            Surface.ExitRequested -= OnExitRequested;
            PreviewKeyDown -= OnWindowKeyDown;
            _feedback?.Dispose();
            Surface.Dispose();
        }
        base.OnClosed(e);
    }
}
