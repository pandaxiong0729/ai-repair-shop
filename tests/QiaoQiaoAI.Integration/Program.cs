using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QiaoQiaoAI.Plugin;
using QiaoQiaoAI.UI;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using HostMain = VPet_Simulator.Core.Main;
using HostToolBar = VPet_Simulator.Core.ToolBar;

namespace QiaoQiaoAI.Integration;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? report = Option(args, "--report");
        bool interactive = args.Contains("--interactive");
        var results = new List<CheckResult>();
        var observations = new List<string>();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window? window = null;
        HostMain? main = null;
        QiaoQiaoPlugin? plugin = null;
        try
        {
            var controller = StrictProxy.Create<IController>(new() { ["get_EnableFunction"] = true }, out _);
            main = new HostMain(new GameCore { Controller = controller });
            main.ToolBar = new HostToolBar(main) { Visibility = Visibility.Collapsed };
            main.UIGrid.Children.Add(main.ToolBar);
            main.PetGrid.Child = new Border
            {
                Background = Brushes.AliceBlue,
                Child = new TextBlock { Text = "VPet Core 测试宿主\n（未加载存档或原角色动画）", FontSize = 21,
                    TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            main.PetGrid.OpacityMask = Brushes.Black;
            main.PetGrid2.OpacityMask = Brushes.Black;
            var proxy = StrictProxy.Create<IMainWindow>(new()
            {
                ["get_Main"] = main,
                ["get_Dispatcher"] = app.Dispatcher
            }, out var accessLog);
            plugin = new QiaoQiaoPlugin(proxy);
            window = new HarnessWindow
            {
                Title = "人工智障修理厂 · VPet Core integration harness",
                Width = 550, Height = 620,
                ShowInTaskbar = interactive,
                ShowActivated = interactive,
                Opacity = interactive ? 1 : 0,
                Left = interactive ? 100 : -10000,
                Top = interactive ? 100 : -10000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = main
            };
            window.Show();
            Pump();

            if (interactive)
            {
                var panel = new DockPanel();
                var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
                var start = new Button { Content = "打开人工智障修理厂", Padding = new Thickness(10, 5, 10, 5) };
                start.Click += (_, _) => plugin.StartMode();
                var stop = new Button { Content = "结束模式", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
                stop.Click += (_, _) => plugin.StopMode();
                var menu = new Menu();
                var item = new MenuItem { Header = "真实 VPet 自定菜单" };
                menu.Items.Add(item);
                bar.Children.Add(start); bar.Children.Add(stop); bar.Children.Add(menu);
                DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar);
                window.Content = null; panel.Children.Add(main); window.Content = panel;
                plugin.GameLoaded();
                // Expose the real host menu item, preserving its actual click handler.
                var actual = main.ToolBar.MenuDIY.Items.OfType<MenuItem>().Single();
                main.ToolBar.MenuDIY.Items.Remove(actual); item.Items.Add(actual);
                // The harness deliberately has no pet graph or save; block bare-host gestures.
                main.PreviewMouseMove += (_, e) => { if (!plugin.IsActive) e.Handled = true; };
                main.PreviewMouseDown += (_, e) => { if (!plugin.IsActive && e.ChangedButton == MouseButton.Left) e.Handled = true; };
                window.Closed += (_, _) => { plugin.EndGame(); app.Shutdown(); };
                plugin.StartMode();
                return app.Run();
            }

            void Check(string name, Action test)
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    plugin.StopMode();
                    main.PetGrid.ApplyAnimationClock(UIElement.OpacityMaskProperty, null);
                    main.PetGrid2.ApplyAnimationClock(UIElement.OpacityMaskProperty, null);
                    main.PetGrid.ClearValue(FrameworkElement.StyleProperty);
                    main.PetGrid.ClearValue(UIElement.OpacityMaskProperty);
                    main.PetGrid.OpacityMask = Brushes.Black;
                    main.PetGrid2.OpacityMask = Brushes.Black;
                    Pump();
                    test();
                    results.Add(new(name, true, "Passed", timer.ElapsedMilliseconds));
                }
                catch (Exception exception)
                {
                    results.Add(new(name, false, exception.ToString(), timer.ElapsedMilliseconds));
                }
                finally { plugin.StopMode(); Pump(); }
            }

            int normalChildren = main.UIGrid.Children.Count;
            ResidentFeedbackChecks.Run(Check);
            Check("menu_lifecycle_is_idempotent", () =>
            {
                for (int i = 0; i < 10; i++) { plugin.LoadPlugin(); plugin.GameLoaded(); plugin.LoadDIY(); }
                Require(MenuCount(main) == 1, "Repeated lifecycle callbacks duplicated the menu.");
                main.ToolBar.MenuDIY.Items.Clear();
                plugin.LoadDIY();
                Require(MenuCount(main) == 1, "Host DIY reload did not restore exactly one menu.");
            });

            Check("twenty_sessions_restore_layers_masks_and_stop_ui_timer", () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    plugin.StartMode(); plugin.StartMode(); Pump();
                    var surface = plugin.ActiveSurface!;
                    Require(plugin.IsActive && surface.HasLoadedSkin && surface.IsAnimationRunning, "Session did not load and animate.");
                    Require(main.UIGrid.Children.Count == normalChildren + 1, "Session added more than one overlay.");
                    Require(IsTransparent(main.PetGrid.OpacityMask) && IsTransparent(main.PetGrid2.OpacityMask), "Original pet visuals are not both masked.");
                    surface.Hit(InteractionTool.Hammer, new Point(250, 275));
                    plugin.StopMode(); plugin.StopMode(); Pump();
                    Require(!plugin.IsActive && !surface.IsAnimationRunning && surface.ActiveEffectCount == 0, "Ended surface retained timer/effects.");
                    Require(main.UIGrid.Children.Count == normalChildren, "Overlay was not removed.");
                    Require(ReferenceEquals(main.PetGrid.OpacityMask, Brushes.Black) && ReferenceEquals(main.PetGrid2.OpacityMask, Brushes.Black), "Pet masks did not restore.");
                }
            });

            Check("binding_and_live_binding_source_are_preserved", () =>
            {
                var source = new MaskSource { Mask = Brushes.Black };
                var binding = new Binding(nameof(MaskSource.Mask)) { Source = source };
                BindingOperations.SetBinding(main.PetGrid, UIElement.OpacityMaskProperty, binding);
                var expression = main.PetGrid.ReadLocalValue(UIElement.OpacityMaskProperty);
                plugin.StartMode(); Pump();
                source.Mask = Brushes.Blue; Pump();
                Require(IsTransparent(main.PetGrid.OpacityMask), "Binding update defeated active mask.");
                Require(ReferenceEquals(expression, main.PetGrid.ReadLocalValue(UIElement.OpacityMaskProperty)), "Active mask replaced the binding expression.");
                plugin.StopMode(); Pump();
                Require(ReferenceEquals(main.PetGrid.OpacityMask, Brushes.Blue), "Latest bound value was lost.");
                Require(ReferenceEquals(expression, main.PetGrid.ReadLocalValue(UIElement.OpacityMaskProperty)), "Stop replaced the original binding expression.");
            });

            Check("style_and_unset_local_value_are_preserved", () =>
            {
                main.PetGrid.ClearValue(UIElement.OpacityMaskProperty);
                var style = new Style(typeof(Decorator));
                style.Setters.Add(new Setter(UIElement.OpacityMaskProperty, Brushes.Green));
                main.PetGrid.Style = style;
                plugin.StartMode(); Pump(); plugin.StopMode(); Pump();
                Require(ReferenceEquals(main.PetGrid.OpacityMask, Brushes.Green), "Style-derived mask was lost.");
                Require(main.PetGrid.ReadLocalValue(UIElement.OpacityMaskProperty) == DependencyProperty.UnsetValue, "Stop introduced an overriding local value.");
            });

            Check("dynamic_resource_updates_survive", () =>
            {
                window.Resources["IntegrationMask"] = Brushes.Green;
                main.PetGrid.SetResourceReference(UIElement.OpacityMaskProperty, "IntegrationMask");
                plugin.StartMode(); Pump();
                window.Resources["IntegrationMask"] = Brushes.Purple;
                Pump(); plugin.StopMode(); Pump();
                Require(ReferenceEquals(main.PetGrid.OpacityMask, Brushes.Purple), "DynamicResource reference/update was lost.");
            });

            Check("base_value_changed_during_mode_is_not_overwritten", () =>
            {
                plugin.StartMode(); Pump();
                main.PetGrid.OpacityMask = Brushes.Orange;
                Require(IsTransparent(main.PetGrid.OpacityMask), "Changing the base value defeated active mask.");
                plugin.StopMode(); Pump();
                Require(ReferenceEquals(main.PetGrid.OpacityMask, Brushes.Orange), "Stop overwrote another owner's latest value.");
            });

            Check("third_party_replacement_animation_survives_stop", () =>
            {
                plugin.StartMode(); Pump();
                var animation = new ObjectAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromSeconds(1)), FillBehavior = FillBehavior.HoldEnd };
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(Brushes.Red, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(Brushes.Blue, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350))));
                main.PetGrid.BeginAnimation(UIElement.OpacityMaskProperty, animation, HandoffBehavior.SnapshotAndReplace);
                Pump();
                observations.Add($"Replacement before stop: color={DescribeBrush(main.PetGrid.OpacityMask)}, same Red instance={ReferenceEquals(main.PetGrid.OpacityMask, Brushes.Red)}, animated={DependencyPropertyHelper.GetValueSource(main.PetGrid, UIElement.OpacityMaskProperty).IsAnimated}");
                Require(IsColor(main.PetGrid.OpacityMask, Colors.Red), "Replacement animation did not initially render red.");
                plugin.StopMode(); Pump();
                observations.Add($"Replacement after stop: color={DescribeBrush(main.PetGrid.OpacityMask)}, same Red instance={ReferenceEquals(main.PetGrid.OpacityMask, Brushes.Red)}, animated={DependencyPropertyHelper.GetValueSource(main.PetGrid, UIElement.OpacityMaskProperty).IsAnimated}");
                Require(IsColor(main.PetGrid.OpacityMask, Colors.Red), "Stop changed the replacement animation's rendered value.");
                Require(DependencyPropertyHelper.GetValueSource(main.PetGrid, UIElement.OpacityMaskProperty).IsAnimated, "Stop removed the replacement animation.");
                Pump(400);
                Require(IsColor(main.PetGrid.OpacityMask, Colors.Blue), "Replacement animation did not continue advancing after plugin stop.");
                Require(ReferenceEquals(main.PetGrid.GetAnimationBaseValue(UIElement.OpacityMaskProperty), Brushes.Black), "Stop replaced the underlying non-animated value.");
                observations.Add("The replacement advanced to blue after plugin stop while its underlying base value remained black.");
            });

            Check("missing_and_corrupt_assets_fail_without_host_changes", () =>
            {
                string scratch = Path.Combine(Path.GetTempPath(), "QiaoQiaoAI-integration-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                string imagePath = Path.Combine(scratch, "spritesheet.png");
                try
                {
                    foreach (bool corrupt in new[] { false, true })
                    {
                        if (corrupt) File.WriteAllBytes(imagePath, [0x00, 0x01, 0x02, 0x03]);
                        using var broken = new QiaoQiaoPlugin(proxy, scratch);
                        bool rejected = false;
                        try { broken.StartMode(); }
                        catch (InvalidDataException ex) { rejected = ex.Message.Contains("素材"); }
                        Pump();
                        Require(rejected, $"{(corrupt ? "Corrupt" : "Missing")} sprite was accepted or lacked a useful error.");
                        Require(!broken.IsActive && broken.ActiveSurface is null, "Failed startup retained a session.");
                        Require(main.UIGrid.Children.Count == normalChildren, "Failed startup inserted a residual overlay.");
                        Require(ReferenceEquals(main.PetGrid.OpacityMask, Brushes.Black) && ReferenceEquals(main.PetGrid2.OpacityMask, Brushes.Black), "Failed startup changed the host's masks.");
                        var unguarded = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent };
                        main.LabelDisplay.RaiseEvent(unguarded);
                        Require(!unguarded.Handled, "Failed startup retained an input guard.");
                    }
                    plugin.StartMode(); Pump();
                    Require(plugin.IsActive && plugin.ActiveSurface!.HasLoadedSkin, "A valid session no longer starts after rejected assets.");
                }
                finally
                {
                    if (File.Exists(imagePath)) File.Delete(imagePath);
                    Directory.Delete(scratch); // Only this newly-created empty test directory.
                }
            });

            Check("mouse_routes_do_not_reach_pet_handlers", () =>
            {
                plugin.StartMode(); Pump();
                int downs = 0, ups = 0, moves = 0, wheels = 0;
                MouseButtonEventHandler down = (_, _) => downs++;
                MouseButtonEventHandler up = (_, _) => ups++;
                MouseEventHandler move = (_, _) => moves++;
                MouseWheelEventHandler wheel = (_, _) => wheels++;
                main.MainGrid.AddHandler(Mouse.MouseDownEvent, down);
                main.MainGrid.AddHandler(Mouse.MouseUpEvent, up);
                main.MainGrid.AddHandler(Mouse.MouseMoveEvent, move);
                main.MainGrid.AddHandler(Mouse.MouseWheelEvent, wheel);
                try
                {
                    var surface = plugin.ActiveSurface!;
                    surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
                    surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                    surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.MouseDownEvent });
                    surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent });
                    surface.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.MouseWheelEvent });
                    Require(downs + ups + moves + wheels == 0 && !main.isPress, "Synthetic input reached a host pet event.");
                }
                finally
                {
                    main.MainGrid.RemoveHandler(Mouse.MouseDownEvent, down);
                    main.MainGrid.RemoveHandler(Mouse.MouseUpEvent, up);
                    main.MainGrid.RemoveHandler(Mouse.MouseMoveEvent, move);
                    main.MainGrid.RemoveHandler(Mouse.MouseWheelEvent, wheel);
                }
            });

            Check("host_overlay_preview_input_is_guarded_only_while_active", () =>
            {
                plugin.StartMode(); Pump();
                var during = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent };
                main.LabelDisplay.RaiseEvent(during);
                Require(during.Handled, "Input through another host visual was not blocked.");
                plugin.StopMode(); Pump();
                var after = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent };
                main.LabelDisplay.RaiseEvent(after);
                Require(!after.Handled, "Host input remained blocked after mode ended.");
            });

            Check("tool_buttons_switch_without_counting_hits", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                SelectToolByClick(surface, InteractionTool.Poop);
                Require(surface.SelectedTool == InteractionTool.Poop && surface.HitCount == 0, "Poop selector did not switch cleanly.");
                SelectToolByClick(surface, InteractionTool.Hammer);
                Require(surface.SelectedTool == InteractionTool.Hammer && surface.HitCount == 0, "Hammer selector counted as a hit.");
            });

            Check("expandable_menu_selects_all_nine_tools_without_obsolete_reply_card_input", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                var tools = Enum.GetValues<InteractionTool>();
                Require(tools.Length == 9, "Menu requires exactly nine tools.");
                foreach (var tool in tools)
                {
                    OpenToolMenuByClick(surface);
                    var entries = Descendants<Button>(surface).Where(button => button.Tag is InteractionTool).ToArray();
                    Require(entries.Length == 9 && entries.Select(button => (InteractionTool)button.Tag).Distinct().Count() == 9, "Menu omits or duplicates tools.");
                    GetToolButton(surface, tool).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Require(surface.SelectedTool == tool && !surface.IsToolMenuOpen && surface.HitCount == 0, "Selection did not close menu or incorrectly counted a hit: " + tool);
                }
                OpenToolMenuByClick(surface);
                Require(!Descendants<TextBox>(surface).Any(), "Tool menu still exposes unused reply-card text input.");
                surface.CloseToolMenu();
            });

            Check("all_nine_tools_create_bounded_host_effects_and_character_reactions", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                foreach (var character in Enum.GetValues<CharacterKind>())
                foreach (var tool in Enum.GetValues<InteractionTool>())
                {
                    surface.Reset(); surface.SelectedCharacter = character; surface.SelectedTool = tool;
                    surface.ReplyText = "这段回答没有回答我的问题。";
                    surface.BeginPress(new Point(250, 275));
                    if (tool == InteractionTool.Pinch)
                    {
                        surface.UpdatePressPosition(new Point(310, 275));
                        Require(surface.IsPinching && surface.IsReacting, $"{character} pinch did not react while held.");
                    }
                    surface.EndPress();
                    Require(surface.HitCount == 1 && surface.ActiveEffectCount is > 0 and <= 24 && surface.IsReacting,
                        $"{character}/{tool} failed to produce exactly one bounded effect/reaction.");
                    if (tool == InteractionTool.PaperReject)
                    {
                        surface.AdvanceForTesting(.99);
                        Require(surface.IsReacting, $"{character} did not react when the paper ball reached the face at .97 seconds.");
                    }
                    surface.AdvanceForTesting(10);
                    Require(surface.ActiveEffectCount == 0 && !surface.IsPinching && !surface.IsPinchReturning && !surface.IsReacting,
                        $"{character}/{tool} failed to expire.");
                }
            });

            Check("nine_tool_hold_repetition_combos_and_red_marks_stay_bounded", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                foreach (var tool in Enum.GetValues<InteractionTool>())
                {
                    surface.Reset(); surface.SelectedTool = tool;
                    surface.BeginPress(new Point(250, 275));
                    if (tool == InteractionTool.Pinch) surface.UpdatePressPosition(new Point(305, 285));
                    double delay = tool == InteractionTool.Poop ? VentSurface.ChargeDurationSeconds : VentSurface.RepeatDelaySeconds;
                    surface.AdvanceForTesting(delay - .01);
                    int initial = tool is InteractionTool.Poop or InteractionTool.Pinch ? 0 : 1;
                    Require(surface.HitCount == initial && !surface.IsRepeating, tool + " repeated before its delay.");
                    surface.AdvanceForTesting(.02);
                    Require(surface.IsRepeating && surface.HitCount > initial, tool + " did not begin hold repetition.");
                    for (int index = 0; index < 20; index++) surface.AdvanceForTesting(VentSurface.RepeatIntervalSeconds);
                    Require(surface.HitCount > initial + 15 && surface.ComboCount > 1 && surface.ActiveEffectCount <= 24,
                        tool + " did not repeat continuously with bounded effects.");
                    Require(surface.RedMarkCount is > 0 and <= 8 && surface.BlushStrength is > 0 and <= .850001,
                        tool + " red marks/blush were missing or unbounded.");
                    surface.EndPress();
                    int completed = surface.HitCount;
                    surface.AdvanceForTesting(10);
                    Require(surface.HitCount == completed && surface.ComboCount == 0 && !surface.IsRepeating && !surface.IsPressActive && !surface.IsWhipping &&
                        !surface.IsPinching && !surface.HasPointerCapture && surface.ActiveEffectCount == 0 && surface.RedMarkCount == 0 && surface.BlushStrength < .001,
                        tool + " retained repetition, capture, effects, red marks or combos after release.");
                }
            });

            Check("moving_whip_hits_three_characters_with_rate_limit_and_cancels", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                foreach (var character in Enum.GetValues<CharacterKind>())
                {
                    surface.Reset(); surface.SelectedCharacter = character; surface.SelectedTool = InteractionTool.Whip;
                    surface.BeginPress(new Point(250, 275));
                    surface.AdvanceForTesting(.11); surface.UpdatePressPosition(new Point(310, 220));
                    int first = surface.HitCount;
                    Require(first >= 2 && surface.IsWhipping && surface.IsReacting, character + " did not react to a whip sweep.");
                    surface.AdvanceForTesting(.06); surface.UpdatePressPosition(new Point(190, 315));
                    Require(surface.HitCount == first, "Whip ignored its 0.1-second movement rate limit.");
                    surface.AdvanceForTesting(.05); surface.UpdatePressPosition(new Point(190, 315));
                    Require(surface.HitCount > first && surface.RedMarkCount is > 0 and <= 8, "Whip did not accept the next permitted crossing.");
                    surface.EndPress(); surface.AdvanceForTesting(10);
                    Require(!surface.IsWhipping && !surface.IsPressActive && !surface.HasPointerCapture && surface.RedMarkCount == 0,
                        "Whip release left gesture/capture/marks behind.");
                }
                var scene = Descendants<FrameworkElement>(surface).Single(element => element.GetType().Name == "MascotScene");
                var cancellations = new (string name, Action action)[]
                {
                    ("menu", surface.OpenToolMenu), ("clear", surface.ClearEffects),
                    ("disable", () => surface.IsEnabled = false),
                    ("lost capture", () => scene.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent })),
                    ("deactivation", () => ((HarnessWindow)window).RaiseDeactivatedForTest())
                };
                foreach (var (name, cancel) in cancellations)
                {
                    surface.IsEnabled = true; surface.Reset(); surface.SelectedTool = InteractionTool.Whip;
                    surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.4);
                    cancel(); int count = surface.HitCount; surface.AdvanceForTesting(1);
                    Require(!surface.IsWhipping && !surface.IsPressActive && !surface.IsRepeating && !surface.HasPointerCapture && surface.HitCount == count,
                        name + " did not stop whip repetition.");
                    surface.IsEnabled = true; surface.CloseToolMenu();
                }
                surface.SelectedTool = InteractionTool.Whip; surface.BeginPress(new Point(250, 275));
                plugin.StopMode(); Pump();
                Require(!surface.IsWhipping && !surface.IsAnimationRunning && surface.RedMarkCount == 0 && surface.BlushStrength < .001,
                    "Plugin exit retained whip or marks.");
            });

            Check("single_whip_handle_is_hit_testable_outside_the_pet_without_duplicate_rope_geometry", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                surface.FloatingMode = true;
                int recorded = 0;
                EventHandler<HitRecordedEventArgs> record = (_, hit) => recorded += hit.Count;
                surface.HitRecorded += record;
                try
                {
                foreach (var character in Enum.GetValues<CharacterKind>())
                {
                    surface.Reset(); surface.SelectedCharacter = character; surface.SelectedTool = InteractionTool.Whip;
                    recorded = 0;
                    Require(surface.VisibleWhipCount == 1 && CountRenderedWhipStrokes(surface) == 1, "Selecting Whip did not draw one persistent rope.");
                    var scene = Descendants<FrameworkElement>(surface).Single(element => element.GetType().Name == "MascotScene");
                    Point handle = surface.WhipHandlePosition;
                    Point local = handle - new Vector(21, 167);
                    var characterHit = scene.GetType().GetMethod("IsCharacterHit") ?? throw new InvalidOperationException("Missing actual character hit-test method.");
                    Require(!(bool)characterHit.Invoke(scene, [local])!, "Fixture handle should be outside the pet hit region.");
                    Require(ReferenceEquals(scene.InputHitTest(local), scene), "The real loaded transparent WPF scene cannot receive input on the visible external handle.");
                    surface.BeginPress(handle); surface.AdvanceForTesting(.8);
                    Require(surface.IsWhipping && surface.IsPressActive && surface.HitCount == 0 && recorded == 0 && !surface.IsRepeating,
                        "Grabbing and holding the external handle either missed or invented an automatic hit.");
                    foreach (var target in new[] { new Point(345, 275), new Point(155, 275), new Point(250, 207), new Point(250, 343),
                        new Point(345, 207), new Point(155, 343), new Point(345, 343), new Point(155, 207) })
                    {
                        var from = surface.WhipHandlePosition;
                        for (int step = 1; step <= 12; step++)
                        {
                            var pointer = from + (target - from) * (step / 12d);
                            surface.UpdatePressPosition(pointer); surface.AdvanceForTesting(1d / 120);
                            ValidateWhipGeometry(surface, character + " fast captured sweep");
                            Require((surface.WhipHandlePosition - pointer).Length < .001, "Dragging did not directly move the single handle.");
                            Require(recorded == surface.HitCount, "A physics-driven collision was omitted or duplicated in feedback.");
                        }
                    }
                    Require(surface.HitCount > 0 && surface.IsReacting && surface.RedMarkCount > 0 && recorded == surface.HitCount,
                        character + " dragged whip did not produce matched hits, feedback, reaction and cartoon marks.");
                    for (int hit = 0; hit < 80; hit++) surface.Hit(InteractionTool.Whip, new Point(250, 275));
                    Require(surface.VisibleWhipCount == 1 && surface.ActiveEffectCount <= 24 && CountRenderedWhipStrokes(surface) == 1,
                        "Impact feedback stacked additional visible ropes or handles.");
                    surface.EndPress(); int released = surface.HitCount; int releasedRecords = recorded; surface.AdvanceForTesting(10);
                    ValidateWhipGeometry(surface, "loaded host after release");
                    Require(surface.HitCount == released && recorded == releasedRecords && recorded == released &&
                        !surface.IsWhipping && !surface.IsPressActive && !surface.IsRepeating && !surface.HasPointerCapture,
                        "Releasing the manually held whip left hidden repetition/capture active.");
                    Require(surface.ActiveEffectCount == 0 && CountRenderedWhipStrokes(surface) == 1, "Released single whip did not settle with expired impact feedback.");
                    surface.SelectedTool = InteractionTool.Hammer;
                    Require(surface.VisibleWhipCount == 0 && CountRenderedWhipStrokes(surface) == 0, "Switching tools kept the whip drawing alive.");
                }
                }
                finally { surface.HitRecorded -= record; }
            });

            Check("manual_whip_drag_cancels_across_host_ui_and_plugin_lifecycle", () =>
            {
                foreach (var action in new[] { "clear", "cancel", "menu", "tool", "character", "disable", "lost capture", "deactivate", "unload", "exit" })
                {
                    plugin.StopMode(); plugin.StartMode(); Pump();
                    var surface = plugin.ActiveSurface!;
                    surface.SelectedTool = InteractionTool.Whip;
                    surface.BeginPress(surface.WhipHandlePosition);
                    surface.UpdatePressPosition(new Point(250, 275)); surface.AdvanceForTesting(.12);
                    Require(surface.IsWhipping && surface.IsPressActive, "Fixture failed to begin the manual whip drag: " + action);
                    var scene = Descendants<FrameworkElement>(surface).Single(element => element.GetType().Name == "MascotScene");
                    switch (action)
                    {
                        case "clear": surface.ClearEffects(); break;
                        case "cancel": surface.CancelPress(); break;
                        case "menu": surface.OpenToolMenu(); break;
                        case "tool": surface.SelectedTool = InteractionTool.Hammer; break;
                        case "character": surface.SelectedCharacter = surface.SelectedCharacter == CharacterKind.Doubao ? CharacterKind.Codex : CharacterKind.Doubao; break;
                        case "disable": surface.IsEnabled = false; break;
                        case "lost capture": scene.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent }); break;
                        case "deactivate": ((HarnessWindow)window).RaiseDeactivatedForTest(); break;
                        case "unload": surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); break;
                        case "exit": plugin.StopMode(); break;
                    }
                    int stoppedHits = surface.HitCount;
                    surface.UpdatePressPosition(new Point(345, 207)); surface.AdvanceForTesting(10);
                    Require(!surface.IsWhipping && !surface.IsPressActive && !surface.IsRepeating && !surface.HasPointerCapture && surface.HitCount == stoppedHits,
                        action + " left a manually grabbed whip or queued hits active.");
                    if (action is "exit" or "unload") Require(!surface.IsAnimationRunning, action + " retained the animation timer.");
                    if (action == "exit") Require(!plugin.IsActive && main.UIGrid.Children.Count == normalChildren, "Plugin exit retained its overlay.");
                }
            });

            Check("deepseek_idle_and_reaction_frames_animate_and_recover", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                surface.Reset(); surface.SelectedCharacter = CharacterKind.DeepSeek;
                Require(surface.HasDeepSeekSkin && surface.HasLoadedSkin && !surface.IsReacting, "DeepSeek animation is unavailable.");
                int idle = surface.CurrentCharacterFrame;
                surface.AdvanceForTesting(.3);
                Require(surface.CurrentCharacterFrame != idle && !surface.IsReacting, "DeepSeek idle frame did not advance.");
                surface.SelectedTool = InteractionTool.Hammer;
                surface.BeginPress(new Point(250, 275)); surface.EndPress();
                int reaction = surface.CurrentCharacterFrame;
                Require(surface.IsReacting && reaction >= 4, "DeepSeek did not enter its reaction row.");
                surface.AdvanceForTesting(.23);
                Require(surface.IsReacting && surface.CurrentCharacterFrame != reaction, "DeepSeek reaction frame did not advance.");
                surface.AdvanceForTesting(1);
                Require(!surface.IsReacting && surface.CurrentCharacterFrame < 4, "DeepSeek did not return to its idle row.");
                surface.ClearEffects();
                Require(surface.RedMarkCount == 0 && surface.BlushStrength < .001, "DeepSeek clear left cartoon marks.");
            });

            Check("pinch_limits_displacement_and_completes_one_spring_return", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                surface.Reset(); SelectToolByClick(surface, InteractionTool.Pinch);
                surface.BeginPress(new Point(250, 275)); surface.UpdatePressPosition(new Point(1250, 975));
                Require(surface.IsPinching && surface.PinchDisplacement.Length is > 50 and <= 56.00001, "Pinch exceeded the 56-DIP displacement limit.");
                surface.EndPress(); surface.EndPress();
                Require(!surface.IsPinching && surface.IsPinchReturning && !surface.HasPointerCapture && surface.HitCount == 1,
                    "Release did not begin exactly one spring return.");
                surface.AdvanceForTesting(.1);
                Require(surface.IsPinchReturning && surface.PinchDisplacement.Length > .01, "Pinch release skipped visible spring movement.");
                surface.AdvanceForTesting(1);
                Require(!surface.IsPinchReturning && surface.PinchDisplacement.Length < .001 && surface.ActiveEffectCount == 0,
                    "Spring return did not settle and expire.");
            });

            Check("pinch_cancels_on_menu_tool_character_clear_disable_and_explicit_cancel", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                var actions = new (string name, Action action)[]
                {
                    ("menu", () => OpenToolMenuByClick(surface)),
                    ("tool", () => surface.SelectedTool = InteractionTool.Egg),
                    ("face", () => surface.SelectedCharacter = CharacterKind.Doubao),
                    ("clear", surface.ClearEffects), ("cancel", surface.CancelPress),
                    ("disable", () => surface.IsEnabled = false)
                };
                foreach (var (name, cancel) in actions)
                {
                    surface.IsEnabled = true; surface.Reset(); surface.SelectedTool = InteractionTool.Pinch;
                    surface.BeginPress(new Point(250, 275)); surface.UpdatePressPosition(new Point(305, 285));
                    Require(surface.IsPinching, "Could not begin pinch before " + name);
                    cancel();
                    Require(!surface.IsPinching && !surface.IsPinchReturning && !surface.HasPointerCapture && surface.PinchDisplacement.Length < .001,
                        name + " retained pinch/capture/displacement.");
                    if (name == "disable") Require(!surface.IsToolMenuOpen, "Disabled surface retained an open tool menu.");
                    surface.IsEnabled = true; surface.CloseToolMenu();
                }
            });

            Check("lost_capture_deactivation_and_escape_cancel_pinch_and_close_menu", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                void BeginPinch()
                {
                    surface.Reset(); surface.SelectedTool = InteractionTool.Pinch;
                    surface.BeginPress(new Point(250, 275)); surface.UpdatePressPosition(new Point(305, 285));
                    Require(surface.IsPinching, "Could not begin pinch for a host event.");
                }
                BeginPinch();
                var scene = Descendants<FrameworkElement>(surface).Single(element => element.GetType().Name == "MascotScene");
                scene.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
                Require(!surface.IsPinching && !surface.IsPinchReturning && surface.PinchDisplacement.Length < .001 && !surface.HasPointerCapture,
                    "Lost capture failed to cancel pinch.");
                BeginPinch(); ((HarnessWindow)window).RaiseDeactivatedForTest();
                Require(!surface.IsPinching && surface.PinchDisplacement.Length < .001 && !surface.HasPointerCapture, "Window deactivation retained pinch.");
                OpenToolMenuByClick(surface); ((HarnessWindow)window).RaiseDeactivatedForTest();
                Require(!surface.IsToolMenuOpen, "Window deactivation retained the tool menu.");
                BeginPinch();
                var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("Window has no presentation source.");
                surface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Pump();
                Require(!plugin.IsActive && !surface.IsPinching && !surface.IsPinchReturning && !surface.HasPointerCapture && !surface.IsToolMenuOpen,
                    "Escape did not close the pinch session and tool menu.");
            });

            Check("plugin_stop_unload_and_endgame_remove_menu_and_pinch_state", () =>
            {
                foreach (string action in new[] { "stop", "unload", "endgame" })
                {
                    using var temporary = new QiaoQiaoPlugin(proxy, plugin.AssetsDirectory);
                    foreach (bool openMenu in new[] { false, true })
                    {
                        if (action == "endgame" && openMenu) continue;
                        temporary.StartMode(); Pump();
                        var surface = temporary.ActiveSurface!;
                        surface.SelectedTool = InteractionTool.Pinch;
                        surface.BeginPress(new Point(250, 275)); surface.UpdatePressPosition(new Point(305, 285));
                        if (openMenu) OpenToolMenuByClick(surface);
                        if (action == "stop") temporary.StopMode();
                        else if (action == "unload") main.UIGrid.Children.Remove((UIElement)surface.Parent);
                        else temporary.EndGame();
                        Pump();
                        Require(!temporary.IsActive && !surface.IsPinching && !surface.IsPinchReturning && !surface.IsToolMenuOpen && !surface.HasPointerCapture &&
                            surface.PinchDisplacement.Length < .001 && !surface.IsAnimationRunning,
                            $"{action} retained pinch/menu/capture/timer state (menu={openMenu}).");
                    }
                }
            });

            Check("three_offline_faces_switch_and_plugin_remembers_selection", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                Require(surface.HasCodexSkin && surface.HasDoubaoSkin && surface.HasDeepSeekSkin, "All three bundled character animations must load offline.");
                surface.SelectedCharacter = CharacterKind.DeepSeek;
                Require(surface.SelectedCharacter == CharacterKind.DeepSeek && surface.HasLoadedSkin, "DeepSeek did not load.");
                surface.SelectedCharacter = CharacterKind.Doubao;
                Require(surface.SelectedCharacter == CharacterKind.Doubao && surface.HasLoadedSkin, "Doubao did not load.");
                plugin.StopMode(); Pump(); plugin.StartMode(); Pump();
                Require(plugin.ActiveSurface!.SelectedCharacter == CharacterKind.Doubao && plugin.ActiveSurface.HasLoadedSkin, "Plugin forgot the selected face between sessions.");
                plugin.ActiveSurface.SelectedCharacter = CharacterKind.Codex;
                Require(plugin.ActiveSurface.SelectedCharacter == CharacterKind.Codex && plugin.ActiveSurface.HasLoadedSkin, "Codex did not restore.");
            });

            Check("doubao_idle_blink_and_both_hit_reactions_advance_in_real_host", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                surface.Reset(); surface.SelectedCharacter = CharacterKind.Doubao;
                Require(surface.DoubaoFrame == 0 && !surface.IsReacting, "Doubao idle animation did not begin at frame 0.");
                foreach (var step in new[] { (seconds: 2.85, frame: 1), (seconds: .14, frame: 2), (seconds: .13, frame: 3), (seconds: .13, frame: 0) })
                {
                    surface.AdvanceForTesting(step.seconds);
                    Require(surface.DoubaoFrame == step.frame && !surface.IsReacting, $"Idle blink expected {step.frame}; got {surface.DoubaoFrame}.");
                }
                foreach (var tool in new[] { InteractionTool.Hammer, InteractionTool.Poop })
                {
                    surface.Reset(); surface.SelectedCharacter = CharacterKind.Doubao; surface.SelectedTool = tool;
                    surface.BeginPress(new Point(250, 275));
                    if (tool == InteractionTool.Poop) { surface.AdvanceForTesting(.1); surface.EndPress(); }
                    else surface.EndPress();
                    for (int expected = 4; expected <= 7; expected++)
                    {
                        Require(surface.IsReacting && surface.DoubaoFrame == expected && surface.CurrentCharacterFrame == expected,
                            $"{tool} expected reaction frame {expected}; got {surface.DoubaoFrame}.");
                        surface.AdvanceForTesting(.2);
                    }
                    Require(!surface.IsReacting && surface.DoubaoFrame is >= 0 and <= 3, $"{tool} face did not recover after 0.72 seconds.");
                }
            });

            Check("doubao_continuous_spray_keeps_expressions_playing_and_release_recovers", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                surface.Reset(); surface.SelectedCharacter = CharacterKind.Doubao; surface.SelectedTool = InteractionTool.Poop;
                surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.61);
                var frames = new HashSet<int>();
                for (int index = 0; index < 20; index++)
                {
                    Require(surface.IsSpraying && surface.IsReacting && surface.DoubaoFrame is >= 4 and <= 7, "Sustained spray dropped back to an idle face.");
                    frames.Add(surface.DoubaoFrame); surface.AdvanceForTesting(.1);
                }
                Require(frames.SetEquals([4, 5, 6, 7]), "Repeated shots restarted the face before all reaction frames could play.");
                var count = surface.SuperPoopCount;
                surface.EndPress(); surface.AdvanceForTesting(.8);
                Require(!surface.IsReacting && surface.DoubaoFrame is >= 0 and <= 3 && !surface.IsPressActive && !surface.HasPointerCapture && surface.SuperPoopCount == count,
                    "Release did not stop spray and return the face to idle.");
            });

            Check("repeated_doubao_hits_do_not_break_clear_cancel_or_plugin_exit", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                surface.Reset(); surface.SelectedCharacter = CharacterKind.Doubao;
                for (int index = 0; index < 20; index++)
                {
                    surface.SelectedTool = index % 2 == 0 ? InteractionTool.Hammer : InteractionTool.Poop;
                    surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.05); surface.EndPress();
                    Require(surface.IsReacting && !surface.IsPressActive && !surface.HasPointerCapture, "Repeated click failed to release cleanly.");
                }
                surface.SelectedTool = InteractionTool.Poop;
                surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.8);
                surface.ClearEffects();
                var count = surface.SuperPoopCount;
                surface.AdvanceForTesting(2);
                Require(!surface.IsReacting && !surface.IsSpraying && !surface.IsPressActive && !surface.HasPointerCapture && surface.SuperPoopCount == count,
                    "Clear after repeated reactions left expression/spray active.");
                surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.8);
                plugin.StopMode(); Pump();
                Require(!surface.IsReacting && !surface.IsPressActive && !surface.IsSpraying && !surface.HasPointerCapture && !surface.IsAnimationRunning,
                    "Plugin exit did not clean up an animated reaction session.");
            });

            Check("short_press_threshold_and_long_spray_use_the_shared_state_machine", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                surface.Reset(); surface.SelectedTool = InteractionTool.Poop;
                surface.BeginPress(new Point(250, 275));
                surface.AdvanceForTesting(VentSurface.ChargeDurationSeconds - .01);
                Require(surface.IsCharging && !surface.IsSpraying && surface.SuperPoopCount == 0 && surface.HitCount == 0, "Emission occurred before the 0.6-second threshold.");
                surface.EndPress(); surface.EndPress();
                Require(surface.HitCount == 1 && surface.SuperPoopCount == 0 && !surface.IsPressActive, "A short press must produce exactly one ordinary poop on release.");
                surface.Reset(); surface.SelectedTool = InteractionTool.Poop;
                surface.BeginPress(new Point(250, 275));
                surface.AdvanceForTesting(VentSurface.ChargeDurationSeconds + .01);
                Require(surface.IsSpraying && surface.SuperPoopCount >= 1 && surface.ActiveSuperEffectCount >= 1, "Held poop did not become super spray.");
                var first = surface.SuperPoopCount;
                for (int i = 0; i < 30; i++) surface.AdvanceForTesting(VentSurface.SprayIntervalSeconds);
                Require(surface.SuperPoopCount > first + 15 && surface.ActiveEffectCount <= 24, "Continuous emission stalled or effects were unbounded.");
                var emissions = surface.SuperPoopCount;
                var hits = surface.HitCount;
                surface.EndPress();
                surface.AdvanceForTesting(10);
                Require(surface.SuperPoopCount == emissions && surface.HitCount == hits && surface.ActiveEffectCount == 0, "Released spray emitted queued work or kept expired effects.");
                Require(!surface.IsPressActive && !surface.IsSpraying && !surface.HasPointerCapture, "Release left the press active/captured.");
            });

            Check("lost_capture_and_window_deactivation_cancel_spray", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                void BeginSpray()
                {
                    surface.Reset(); surface.SelectedTool = InteractionTool.Poop;
                    surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.8);
                    Require(surface.IsSpraying, "Spray did not start for cancellation test.");
                }
                void RequireStopped(string reason)
                {
                    var count = surface.SuperPoopCount;
                    surface.AdvanceForTesting(2);
                    Require(!surface.IsPressActive && !surface.IsSpraying && !surface.HasPointerCapture && surface.SuperPoopCount == count, reason + " did not stop and release spray.");
                }
                BeginSpray();
                var scene = Descendants<FrameworkElement>(surface).Single(element => element.GetType().Name == "MascotScene");
                scene.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
                RequireStopped("WPF LostMouseCapture event");
                BeginSpray();
                ((HarnessWindow)window).RaiseDeactivatedForTest();
                RequireStopped("Window.Deactivated event");
            });

            Check("toolbar_actions_face_change_clear_and_explicit_cancel_stop_spray", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                var buttons = Descendants<Button>(surface).ToArray();
                var cancellations = new (string name, Action action)[]
                {
                    ("tool button", () => SelectToolByClick(surface, InteractionTool.Hammer)),
                    ("clear button", () => buttons.Single(b => Equals(b.Content, "一键清理")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent))),
                    ("face switch", () => surface.SelectedCharacter = CharacterKind.Doubao),
                    ("explicit cancel", surface.CancelPress)
                };
                foreach (var (name, cancel) in cancellations)
                {
                    surface.Reset(); surface.SelectedTool = InteractionTool.Poop;
                    surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.8);
                    Require(surface.IsSpraying, "Could not begin spray before " + name);
                    cancel();
                    var count = surface.SuperPoopCount;
                    surface.AdvanceForTesting(2);
                    Require(!surface.IsPressActive && !surface.IsSpraying && !surface.HasPointerCapture && surface.SuperPoopCount == count, name + " left spray active or queued.");
                }
            });

            Check("plugin_stop_unload_and_exit_cancel_active_spray", () =>
            {
                foreach (string reason in new[] { "plugin stop", "unload", "Escape" })
                {
                    plugin.StartMode(); Pump();
                    var surface = plugin.ActiveSurface!;
                    surface.SelectedTool = InteractionTool.Poop;
                    surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.8);
                    Require(surface.IsSpraying, "Could not start spray before " + reason);
                    if (reason == "plugin stop") plugin.StopMode();
                    else if (reason == "unload") main.UIGrid.Children.Remove((UIElement)surface.Parent);
                    else
                    {
                        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("Window has no presentation source.");
                        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                        surface.RaiseEvent(escape);
                        Require(escape.Handled, "Escape close route did not handle the key.");
                    }
                    Pump();
                    var count = surface.SuperPoopCount;
                    surface.AdvanceForTesting(5);
                    Require(!plugin.IsActive && !surface.IsPressActive && !surface.IsSpraying && !surface.HasPointerCapture && !surface.IsAnimationRunning && surface.SuperPoopCount == count,
                        reason + " did not release the active press, timer and session.");
                }
            });

            Check("escape_stops_mode", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                surface.SelectedTool = InteractionTool.Poop;
                surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.8);
                var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("Window has no presentation source.");
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                surface.RaiseEvent(escape); Pump();
                Require(escape.Handled && !plugin.IsActive && !surface.IsAnimationRunning && !surface.IsPressActive && !surface.IsSpraying && !surface.HasPointerCapture, "Escape failed to end the mode and release spray.");
            });

            Check("external_layer_removal_rolls_back_session", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                var layer = (UIElement)surface.Parent;
                main.UIGrid.Children.Remove(layer); Pump();
                Require(!plugin.IsActive && !surface.IsAnimationRunning && ReferenceEquals(main.PetGrid.OpacityMask, Brushes.Black), "Unloaded did not roll back owned state.");
            });

            Check("ended_plugin_is_collectible_with_host_still_alive", () =>
            {
                var weak = CreateEndedPlugin(proxy, plugin.AssetsDirectory);
                Pump(150);
                for (int i = 0; i < 3 && weak.IsAlive; i++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Pump(100);
                }
                Require(!weak.IsAlive, "Host menu, window or input subscriptions retained an ended plugin.");
                Require(main.UIGrid.Children.Count == normalChildren && MenuCount(main) == 1, "Ended transient plugin changed the live plugin's menu/layers.");
            });

            Check("end_game_is_idempotent_and_removes_menu", () =>
            {
                plugin.StartMode(); Pump();
                var surface = plugin.ActiveSurface!;
                plugin.EndGame(); plugin.EndGame(); Pump();
                Require(MenuCount(main) == 0 && !plugin.IsActive && !surface.IsAnimationRunning, "EndGame did not clean the menu/session.");
                Require(main.UIGrid.Children.Count == normalChildren && ReferenceEquals(main.PetGrid.OpacityMask, Brushes.Black), "EndGame left owned visuals/masks.");
            });
            results.Add(new("no_save_numeric_or_unauthorized_host_interface_access", accessLog.UnexpectedCalls.Count == 0,
                accessLog.UnexpectedCalls.Count == 0 ? "Only IMainWindow.Main and Dispatcher were accessed." : string.Join(", ", accessLog.UnexpectedCalls), 0));
        }
        catch (Exception exception) { results.Add(new("harness_setup_or_unhandled_failure", false, exception.ToString(), 0)); }
        finally
        {
            plugin?.EndGame();
            window?.Close();
            main?.Dispose();
            app.Shutdown();
        }

        var payload = new
        {
            success = results.Count > 0 && results.All(r => r.Passed),
            hostCommit = "5a64ad2bf00a3f321227a26aa585bac6399b1a0e",
            target = "VPet 1.10.74 source-built Core/Interface + QiaoQiaoAI plugin",
            testedAt = DateTimeOffset.UtcNow,
            cases = results,
            observations,
            limitations = new[]
            {
                "Real Core.Main and ToolBar are used; IMainWindow/controller are strict lightweight proxies, without a game save or graph.",
                "Input routing tests synthesize WPF routed events. They do not replace hardware input/DPI testing in the Steam executable.",
                "Press/charge/release checks use the same BeginPress/EndPress/CancelPress and timer state machine as physical pointer handlers; WPF LostMouseCapture and Window.Deactivated events are raised without moving the mouse or acquiring OS capture. Native pointer capture/release still requires manual acceptance.",
                "This harness does not test Steam Workshop distribution, code-MOD trust dialogs or host auto-movement during normal gameplay."
            }
        };
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        if (report is not null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!); File.WriteAllText(report, json); }
        Console.WriteLine(json);
        return payload.success ? 0 : 1;
    }

    private static string? Option(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void ValidateWhipGeometry(VentSurface surface, string context)
    {
        var points = surface.WhipPoints;
        Require(surface.VisibleWhipCount == 1 && points.Count == 18, context + ": expected one 18-point chain.");
        for (int index = 0; index < points.Count; index++)
        {
            var point = points[index];
            Require(double.IsFinite(point.X) && double.IsFinite(point.Y) && point.X is >= 24.999 and <= 477.001 && point.Y is >= 170.999 and <= 361.001,
                context + ": point escaped its finite chain bounds: " + point);
            if (index > 0) Require((point - points[index - 1]).Length <= 13.5, context + ": a rope segment stretched beyond its constraint.");
        }
        Require((points[0] - surface.WhipHandlePosition).Length < .001, context + ": visible rope detached from its handle.");
    }
    private static int CountRenderedWhipStrokes(VentSurface surface)
    {
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(500, 500, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var scene = Descendants<FrameworkElement>(surface).Single(element => element.GetType().Name == "MascotScene");
        var paint = scene.GetType().GetField("WhipLeather", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as SolidColorBrush
            ?? throw new InvalidOperationException("Cannot identify the real whip stroke paint.");
        var drawing = VisualTreeHelper.GetDrawing(scene) ?? throw new InvalidOperationException("The host scene produced no drawing.");
        int Count(Drawing item) => item switch
        {
            DrawingGroup group => group.Children.Sum(Count),
            GeometryDrawing geometry when geometry.Pen?.Brush is SolidColorBrush brush && brush.Color == paint.Color => 1,
            _ => 0
        };
        return Count(drawing);
    }
    private static bool IsTransparent(Brush? brush) => brush is SolidColorBrush solid && (solid.Color.A == 0 || solid.Opacity == 0);
    private static bool IsColor(Brush? brush, Color color) => brush is SolidColorBrush solid && solid.Color == color && solid.Opacity == 1;
    private static string DescribeBrush(Brush? brush) => brush is SolidColorBrush solid ? $"{solid.Color} / opacity {solid.Opacity}" : brush?.GetType().Name ?? "null";
    private static int MenuCount(HostMain main) => main.ToolBar.MenuDIY.Items.OfType<MenuItem>().Count(m => Equals(m.Header, "人工智障修理厂"));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void OpenToolMenuByClick(VentSurface surface)
    {
        surface.UpdateLayout();
        if (surface.IsToolMenuOpen) return;
        var toggles = Descendants<Button>(surface).Where(button => Equals(button.Tag, "ToolMenuToggle")).ToArray();
        Require(toggles.Length == 1, $"Expected one ToolMenuToggle button; found {toggles.Length}.");
        toggles[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        surface.UpdateLayout();
        Require(surface.IsToolMenuOpen, "Tool toggle click did not expand the menu.");
    }

    private static Button GetToolButton(VentSurface surface, InteractionTool tool)
    {
        surface.UpdateLayout();
        var matches = Descendants<Button>(surface).Where(button => Equals(button.Tag, tool)).ToArray();
        Require(matches.Length == 1, $"Expected one {tool} button; found {matches.Length}; menu open={surface.IsToolMenuOpen}.");
        return matches[0];
    }

    private static void SelectToolByClick(VentSurface surface, InteractionTool tool)
    {
        OpenToolMenuByClick(surface);
        GetToolButton(surface, tool).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(surface.SelectedTool == tool && !surface.IsToolMenuOpen, "Tool selection did not update and close: " + tool);
    }
    private static void Pump(int milliseconds = 55)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateEndedPlugin(IMainWindow host, string assetsDirectory)
    {
        var temporary = new QiaoQiaoPlugin(host, assetsDirectory);
        temporary.GameLoaded(); temporary.StartMode(); Pump();
        temporary.EndGame(); Pump();
        return new WeakReference(temporary);
    }
    private sealed record CheckResult(string Name, bool Passed, string Detail, long Milliseconds);
}

internal sealed class HarnessWindow : Window
{
    public void RaiseDeactivatedForTest() => OnDeactivated(EventArgs.Empty);
}

public class StrictProxy : DispatchProxy
{
    public Dictionary<string, object?> Values { get; set; } = new();
    public List<string> UnexpectedCalls { get; } = new();
    public static T Create<T>(Dictionary<string, object?> values, out StrictProxy log) where T : class
    {
        var proxy = Create<T, StrictProxy>();
        log = (StrictProxy)(object)proxy;
        log.Values = values;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        string name = targetMethod?.Name ?? "<null>";
        if (Values.TryGetValue(name, out var value)) return value;
        UnexpectedCalls.Add(name);
        throw new InvalidOperationException("Harness forbids host access: " + name);
    }
}

public sealed class MaskSource : DependencyObject
{
    public static readonly DependencyProperty MaskProperty = DependencyProperty.Register(nameof(Mask), typeof(Brush), typeof(MaskSource));
    public Brush Mask { get => (Brush)GetValue(MaskProperty); set => SetValue(MaskProperty, value); }
}
