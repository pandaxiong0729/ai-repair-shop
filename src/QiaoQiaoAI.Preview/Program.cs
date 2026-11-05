using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QiaoQiaoAI.UI;

namespace QiaoQiaoAI.Preview;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        var assets = Value(args, "--assets") ?? Path.Combine(AppContext.BaseDirectory, "assets");
        try
        {
            if (Value(args, "--connection-check") is { } connectionReport)
                return ResidentConnectionCheck.RunAsync(Path.GetFullPath(connectionReport), Value(args, "--codex-path")).GetAwaiter().GetResult();
            var residentDirectory = Path.GetFullPath(Value(args, "--resident-dir") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiaoQiaoAI", "resident"));
            if (args.Contains("--resident-stop"))
            {
                Directory.CreateDirectory(residentDirectory);
                File.WriteAllText(Path.Combine(residentDirectory, "stop.request"), DateTimeOffset.UtcNow.ToString("O"));
                return 0;
            }
            using var residentMutex = args.Contains("--resident") ? new Mutex(false,
                "Local\\AiRepairShop-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(residentDirectory.ToUpperInvariant())))[..24]) : null;
            if (residentMutex is not null)
            {
                bool owns;
                try { owns = residentMutex.WaitOne(0); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns) return 0;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            if (Value(args, "--self-test") is { } report)
            {
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                return SelfTest(assets, report);
            }
            if (Value(args, "--render-sequence") is { } frames)
                return RenderSequence(assets, frames);
            if (Value(args, "--render-whip-sequence") is { } whipFrames)
                return RenderWhipSequence(assets, whipFrames);
            BitmapSource? codexAtlas = File.Exists(Path.Combine(assets, "public-codex.json"))
                ? PublicCodexAtlas.Load()
                : null;
            if ((Value(args, "--snapshot-floating") ?? Value(args, "--snapshot")) is { } snapshot)
            {
                using var still = new VentSurface(assets, codexAtlas) { FloatingMode = args.Contains("--snapshot-floating") || args.Contains("--floating") };
                if (Value(args, "--character") is { } requestedCharacter)
                {
                    if (!Enum.TryParse<CharacterKind>(requestedCharacter, true, out var character) || !Enum.IsDefined(character))
                        throw new ArgumentException("未知角色：" + requestedCharacter);
                    still.SelectedCharacter = character;
                    if (still.SelectedCharacter != character || !still.HasLoadedSkin) throw new InvalidDataException("所选角色素材缺失，无法生成截图。");
                }
                SaveFrame(still, snapshot, 1000);
                return 0;
            }
            if (args.Contains("--resident"))
            {
                var residentSurface = new VentSurface(assets, codexAtlas);
                var controller = new ResidentFeedbackController(residentSurface, residentDirectory,
                    CodexResidentConnection.FindExecutable(Value(args, "--codex-path")));
                var residentWindow = new ResidentPetWindow(residentSurface, controller);
                controller.StopRequested += (_, _) => residentWindow.Close();
                app.Run(residentWindow);
                return 0;
            }
            string? feedbackDirectory = FeedbackDirectory(args);
            var surface = new VentSurface(assets, codexAtlas);
            FeedbackRound? feedback = null;
            DesktopPetWindow window;
            try
            {
                if (feedbackDirectory is not null) feedback = new FeedbackRound(feedbackDirectory, surface);
                window = new DesktopPetWindow(surface, feedback, classic: args.Contains("--classic"));
            }
            catch { feedback?.Dispose(); surface.Dispose(); throw; }
            app.Run(window);
            return 0;
        }
        catch (Exception ex)
        {
            var report = Value(args, "--self-test");
            if (report is not null)
                File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }, JsonOptions));
            else if (args.Any(a => a is "--snapshot" or "--snapshot-floating" or "--render-sequence" or "--render-whip-sequence" or "--connection-check"))
                Console.Error.WriteLine(ex.Message);
            else
                MessageBox.Show("启动失败：" + ex.Message, "人工智障修理厂");
            return 1;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string? Value(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string? FeedbackDirectory(string[] args)
    {
        string? option = args.Contains("--feedback-dir") ? "--feedback-dir" : args.Contains("--feedback") ? "--feedback" : null;
        if (option is null) return null;
        string? directory = Value(args, option);
        if (string.IsNullOrWhiteSpace(directory) || directory.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("反馈模式需要当前聊天技能创建的会话目录。请用 --feedback-dir <会话目录>；直接启动则为玩具模式。");
        return Path.GetFullPath(directory);
    }

    private static int SelfTest(string assets, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var results = new List<object>();
        var observations = new List<string>();
        int failures = 0;
        void Check(string name, Action action)
        {
            try { action(); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.Message }); }
        }
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        Check("resident routing uses per-message timestamps and first-hit cutoff", () => ResidentRoutingChecks.RunAsync().GetAwaiter().GetResult());
        ResidentControllerChecks.Run(Check, assets);
        using var surface = new VentSurface(assets) { FloatingMode = false };
        surface.SoundEnabled = false;
        // Real WPF button visuals are materialized by layout, even in this windowless test.
        // Do this before looking up controls, not only when the later render checks run.
        PrepareLayout(surface);
        Check("official spritesheet loaded", () => Require(surface.HasLoadedSkin, surface.AssetStatus));
        Check("three bundled character animation sets load and switch offline", () =>
        {
            Require(surface.HasCodexSkin && surface.HasDoubaoSkin, "Both Codex and Doubao assets are required.");
            Require(Enum.GetValues<CharacterKind>().Length == 3, "Expected Codex, Doubao and DeepSeek.");
            foreach (var character in Enum.GetValues<CharacterKind>())
            {
                surface.SelectedCharacter = character;
                Require(surface.SelectedCharacter == character && surface.HasLoadedSkin, $"Failed to switch to {character}.");
            }
        });
        Check("rapid alternating hits remain bounded", () =>
        {
            for (int i = 0; i < 500; i++)
                surface.Hit(i % 2 == 0 ? InteractionTool.Hammer : InteractionTool.Poop, new Point(250, 250));
            Require(surface.HitCount == 500, $"Expected 500 hits, got {surface.HitCount}");
            Require(surface.ActiveEffectCount <= 32, "Unbounded effect instances");
        });
        Check("effects expire and apology appears", () =>
        {
            surface.AdvanceForTesting(12);
            Require(surface.ActiveEffectCount == 0, "Expired effects remain");
            Require(!string.IsNullOrWhiteSpace(surface.CurrentMessage), "Missing offline message");
        });
        Check("twenty reset cycles do not multiply hits", () =>
        {
            for (int i = 0; i < 20; i++)
            {
                surface.Reset();
                surface.Hit(InteractionTool.Hammer, new Point(250, 250));
                Require(surface.HitCount == 1, "Reset did not isolate a new interaction");
                surface.ClearEffects();
                Require(surface.ActiveEffectCount == 0, "Clear left an effect");
            }
        });
        Check("reset button clears interactions without exiting or changing selections", () =>
        {
            int exits = 0;
            int recordedHits = 0;
            EventHandler exited = (_, _) => exits++;
            EventHandler<HitRecordedEventArgs> recorded = (_, args) => recordedHits += args.Count;
            surface.ExitRequested += exited;
            surface.HitRecorded += recorded;
            try
            {
                foreach (var tool in Enum.GetValues<InteractionTool>())
                {
                    surface.Reset(); surface.SelectedCharacter = CharacterKind.DeepSeek; surface.SelectedTool = tool;
                    surface.Hit(tool, new Point(250, 275));
                    surface.BeginPress(new Point(250, 275));
                    surface.AdvanceForTesting(.8);
                    if (tool == InteractionTool.Pinch) surface.UpdatePressPosition(new Point(290, 285));
                    Require(surface.HitCount > 0, $"Could not establish {tool} interaction before reset.");
                    int beforeReset = recordedHits;
                    surface.SoundEnabled = true;
                    RequireButton(surface, "重置", "reset action").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Require(surface.SelectedCharacter == CharacterKind.DeepSeek && surface.SelectedTool == tool && surface.SoundEnabled,
                        $"Reset changed the selected character, tool or sound setting for {tool}.");
                    surface.SoundEnabled = false;
                    surface.AdvanceForTesting(1);
                    Require(exits == 0 && recordedHits == beforeReset, "Reset closed the surface or generated another recorded hit.");
                    Require(surface.HitCount == 0 && surface.ComboCount == 0 && surface.SuperPoopCount == 0 &&
                        surface.ActiveEffectCount == 0 && surface.RedMarkCount == 0 && surface.BlushStrength == 0 &&
                        !surface.IsPressActive && !surface.HasPointerCapture && !surface.IsPinching && !surface.IsPinchReturning && !surface.IsWhipping,
                        $"Reset left {tool} effects, gestures or counters active.");
                    surface.Hit(tool, new Point(250, 275));
                    Require(surface.HitCount == 1 && exits == 0, "Reset prevented subsequent interaction.");
                }
            }
            finally
            {
                surface.SoundEnabled = false;
                surface.ExitRequested -= exited;
                surface.HitRecorded -= recorded;
                surface.Reset();
            }
        });
        Check("short poop press emits exactly one ordinary hit on release", () =>
        {
            surface.Reset(); surface.SelectedTool = InteractionTool.Poop;
            surface.BeginPress(new Point(250, 275));
            surface.AdvanceForTesting(.2);
            Require(surface.IsPressActive && surface.IsCharging && !surface.IsSpraying && surface.HitCount == 0, "Short press fired before release.");
            surface.EndPress(); surface.EndPress();
            Require(surface.HitCount == 1 && surface.SuperPoopCount == 0 && surface.ActiveSuperEffectCount == 0, "Short press did not emit exactly one ordinary poop.");
            Require(!surface.IsPressActive && !surface.HasPointerCapture, "Released press is still active/captured.");
        });
        Check("charging remains quiet before threshold", () =>
        {
            surface.Reset(); surface.SelectedTool = InteractionTool.Poop;
            surface.BeginPress(new Point(250, 275));
            surface.AdvanceForTesting(VentSurface.ChargeDurationSeconds - .01);
            Require(surface.IsCharging && surface.ChargeProgress > .9 && !surface.IsSpraying, "Charging state or progress is incorrect.");
            Require(surface.SuperPoopCount == 0 && surface.HitCount == 0 && surface.ActiveEffectCount == 0, "Poop emitted before charge threshold.");
            surface.CancelPress();
        });
        Check("held poop becomes bounded continuous super spray and release has no backlog", () =>
        {
            surface.Reset(); surface.SelectedTool = InteractionTool.Poop;
            surface.BeginPress(new Point(250, 275));
            surface.AdvanceForTesting(VentSurface.ChargeDurationSeconds + .01);
            Require(surface.IsSpraying && surface.SuperPoopCount >= 1 && surface.ActiveSuperEffectCount >= 1, "Threshold did not start super poop.");
            int first = surface.SuperPoopCount;
            for (int i = 0; i < 50; i++) surface.AdvanceForTesting(VentSurface.SprayIntervalSeconds);
            Require(surface.SuperPoopCount > first + 20, "Holding did not continue emitting.");
            Require(surface.ActiveEffectCount <= 24 && surface.ActiveSuperEffectCount <= 24, "Continuous spray grew unbounded.");
            surface.AdvanceForTesting(600);
            Require(surface.ActiveEffectCount <= 24, "Large elapsed interval queued unbounded effects.");
            int beforeRelease = surface.SuperPoopCount;
            int hitsBeforeRelease = surface.HitCount;
            surface.EndPress();
            for (int i = 0; i < 12; i++) surface.AdvanceForTesting(.5);
            Require(surface.SuperPoopCount == beforeRelease && surface.HitCount == hitsBeforeRelease, "Release emitted an extra shot or retained a queued spray.");
            Require(!surface.IsPressActive && !surface.IsSpraying && !surface.HasPointerCapture && surface.ActiveEffectCount == 0, "Released spray did not settle.");
        });
        Check("tool clear character reset cancel and exit stop spray", () =>
        {
            var cancellations = new (string name, Action action)[]
            {
                ("tool", () => surface.SelectedTool = InteractionTool.Hammer),
                ("clear", surface.ClearEffects),
                ("character", () => surface.SelectedCharacter = CharacterKind.Doubao),
                ("reset", surface.Reset),
                ("cancel", surface.CancelPress),
                ("exit", surface.RequestExit)
            };
            foreach (var (name, cancel) in cancellations)
            {
                surface.Reset(); surface.SelectedTool = InteractionTool.Poop;
                surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.8);
                Require(surface.IsSpraying, $"Could not begin spray before {name}.");
                cancel();
                int count = surface.SuperPoopCount;
                surface.AdvanceForTesting(2);
                Require(!surface.IsPressActive && !surface.IsSpraying && !surface.HasPointerCapture, $"{name} did not cancel/release.");
                Require(surface.SuperPoopCount == count, $"{name} retained queued emissions.");
            }
        });
        Check("doubao idle animation opens blinks and returns to open eyes", () =>
        {
            surface.Reset(); surface.SelectedCharacter = CharacterKind.Doubao;
            Require(surface.HasDoubaoSkin && surface.DoubaoFrame == 0 && !surface.IsReacting, "Doubao must start on the animated idle frame 0.");
            SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, "doubao-idle-frame-0.png"), 1000);
            foreach (var step in new[] { (seconds: 2.85, frame: 1), (seconds: .14, frame: 2), (seconds: .13, frame: 3), (seconds: .13, frame: 0) })
            {
                surface.AdvanceForTesting(step.seconds);
                Require(surface.DoubaoFrame == step.frame && surface.CurrentCharacterFrame == step.frame && !surface.IsReacting,
                    $"Idle blink expected frame {step.frame}, got {surface.DoubaoFrame}.");
                SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, $"doubao-idle-frame-{step.frame}.png"), 1000);
            }
        });
        Check("doubao hammer and poop reactions advance through four expressive frames and recover", () =>
        {
            foreach (var tool in new[] { InteractionTool.Hammer, InteractionTool.Poop })
            {
                surface.Reset(); surface.SelectedCharacter = CharacterKind.Doubao;
                surface.SelectedTool = tool;
                surface.BeginPress(new Point(250, 275));
                if (tool == InteractionTool.Poop) { surface.AdvanceForTesting(.1); surface.EndPress(); }
                else surface.EndPress();
                for (int expected = 4; expected <= 7; expected++)
                {
                    Require(surface.IsReacting && surface.DoubaoFrame == expected && surface.CurrentCharacterFrame == expected,
                        $"{tool} reaction expected frame {expected}, got {surface.DoubaoFrame}; reacting={surface.IsReacting}.");
                    SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, $"doubao-{tool.ToString().ToLowerInvariant()}-reaction-frame-{expected}.png"), 1000);
                    surface.AdvanceForTesting(.2);
                }
                Require(!surface.IsReacting && surface.DoubaoFrame is >= 0 and <= 3, $"{tool} reaction did not return to idle after 0.72 seconds.");
                SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, $"doubao-{tool.ToString().ToLowerInvariant()}-recovered.png"), 1000);
            }
        });
        Check("doubao sustained spray keeps facial animation cycling and release restores idle", () =>
        {
            surface.Reset(); surface.SelectedCharacter = CharacterKind.Doubao; surface.SelectedTool = InteractionTool.Poop;
            surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.61);
            var frames = new HashSet<int>();
            for (int index = 0; index < 20; index++)
            {
                Require(surface.IsSpraying && surface.IsReacting && surface.DoubaoFrame is >= 4 and <= 7,
                    "Continuous spray lost its reaction expression before release.");
                frames.Add(surface.DoubaoFrame);
                surface.AdvanceForTesting(.1);
            }
            Require(frames.SetEquals([4, 5, 6, 7]), "Continuous hits kept resetting the reaction instead of allowing all four frames to play.");
            SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, "doubao-sustained-reaction.png"), 1000);
            var emitted = surface.SuperPoopCount;
            surface.EndPress(); surface.AdvanceForTesting(.8);
            Require(!surface.IsReacting && surface.DoubaoFrame is >= 0 and <= 3 && surface.SuperPoopCount == emitted,
                "Release retained queued emissions or a stuck facial reaction.");
            Require(!surface.IsPressActive && !surface.HasPointerCapture, "Reaction recovery retained press/capture state.");
        });
        Check("repeated doubao hits and restarted holds preserve cancellation", () =>
        {
            surface.Reset(); surface.SelectedCharacter = CharacterKind.Doubao;
            for (int index = 0; index < 20; index++)
            {
                surface.SelectedTool = index % 2 == 0 ? InteractionTool.Hammer : InteractionTool.Poop;
                surface.BeginPress(new Point(250, 275));
                surface.AdvanceForTesting(.05); surface.EndPress();
                Require(surface.IsReacting && !surface.IsPressActive && !surface.HasPointerCapture, "Repeated short click corrupted expression or release state.");
            }
            surface.SelectedTool = InteractionTool.Poop;
            for (int index = 0; index < 5; index++) { surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.7); }
            surface.ClearEffects();
            var emitted = surface.SuperPoopCount;
            surface.AdvanceForTesting(3);
            Require(!surface.IsReacting && !surface.IsSpraying && !surface.IsPressActive && !surface.HasPointerCapture && surface.SuperPoopCount == emitted,
                "Clearing after repeated hits/holds did not cancel animation and spray.");
        });
        Check("expanded tool list exposes and selects all nine real buttons", () =>
        {
            surface.Reset();
            var tools = Enum.GetValues<InteractionTool>();
            Require(tools.Length == 9, $"Expected exactly nine tools, got {tools.Length}.");
            foreach (var tool in tools)
            {
                OpenToolMenuByClick(surface);
                var entries = Descendants<Button>(surface).Where(button => button.Tag is InteractionTool).ToArray();
                Require(entries.Length == 9 && entries.Select(button => (InteractionTool)button.Tag).Distinct().Count() == 9,
                    "Expanded menu must expose all nine distinct clickable tools.");
                if (tool == tools[0]) SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, "all-nine-tools-menu.png"), 1000);
                GetToolButton(surface, tool).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(surface.SelectedTool == tool && !surface.IsToolMenuOpen, $"Selecting {tool} did not select and close the list.");
            }
        });
        Check("fixed paper and stamp visuals have no obsolete reply editor; legacy text remains bounded", () =>
        {
            surface.Reset(); OpenToolMenuByClick(surface);
            Require(!Descendants<TextBox>(surface).Any(), "The tool menu still offers unused reply-card input.");
            surface.ReplyText = new string('文', 180);
            Require(surface.ReplyText.Length == 120, "Programmatic reply text must obey the same length limit.");
            surface.ReplyText = "这段回答没有回答我的问题。";
            surface.CloseToolMenu();
        });
        Check("every tool renders reacts and expires on all three characters", () =>
        {
            foreach (var character in Enum.GetValues<CharacterKind>())
            foreach (var tool in Enum.GetValues<InteractionTool>())
            {
                surface.Reset(); surface.SelectedCharacter = character;
                surface.ReplyText = "这段回答没有回答我的问题。";
                SelectToolByClick(surface, tool);
                surface.BeginPress(new Point(250, 275));
                if (tool == InteractionTool.Poop) { surface.AdvanceForTesting(.12); surface.EndPress(); }
                if (tool == InteractionTool.Pinch)
                {
                    surface.UpdatePressPosition(new Point(310, 275));
                    Require(surface.IsPinching && surface.IsReacting, $"{character} pinch did not deform/react while held.");
                    SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, $"{character.ToString().ToLowerInvariant()}-pinch-drag.png"), 1000);
                    surface.EndPress();
                }
                else if (tool != InteractionTool.Poop) surface.EndPress();
                Require(surface.ActiveEffectCount > 0 && surface.IsReacting, $"{character}/{tool} did not create a visible effect and reaction.");
                foreach (double step in new[] { .1, .25, .4 })
                {
                    surface.AdvanceForTesting(step);
                    _ = RenderBitmap(surface, 1000); // Observe drawing failures in each animation phase.
                }
                surface.AdvanceForTesting(10);
                Require(surface.ActiveEffectCount == 0 && !surface.IsReacting && !surface.IsPinching && !surface.IsPinchReturning,
                    $"{character}/{tool} retained an expired animation.");

                // Capture the actual contact stage for each tool; .1 seconds is only the
                // approach phase for paper, stamps and thrown objects.
                surface.Reset(); surface.SelectedCharacter = character; surface.SelectedTool = tool;
                surface.ReplyText = "这段回答没有回答我的问题。";
                surface.BeginPress(new Point(250, 275));
                if (tool == InteractionTool.Poop) { surface.AdvanceForTesting(.12); surface.EndPress(); }
                if (tool == InteractionTool.Pinch) { surface.UpdatePressPosition(new Point(310, 275)); surface.EndPress(); }
                else if (tool != InteractionTool.Poop) surface.EndPress();
                double impactTime = tool switch
                {
                    InteractionTool.PaperReject => .99,
                    InteractionTool.Stamp => .75,
                    InteractionTool.SpringGlove => .23,
                    InteractionTool.Jelly => .3,
                    InteractionTool.Egg => .42,
                    InteractionTool.Pinch => .1,
                    InteractionTool.Whip => .2,
                    InteractionTool.Poop => .35,
                    _ => .1
                };
                surface.AdvanceForTesting(impactTime);
                if (tool == InteractionTool.PaperReject)
                    Require(surface.IsReacting, $"{character} did not react when the paper ball actually hit at .97 seconds.");
                SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, $"{character.ToString().ToLowerInvariant()}-{tool.ToString().ToLowerInvariant()}-impact.png"), 1000);
            }
        });
        Check("all tool effects stay bounded under repeated interactions", () =>
        {
            foreach (var tool in Enum.GetValues<InteractionTool>())
            {
                surface.Reset(); surface.SelectedTool = tool;
                for (int index = 0; index < 80; index++)
                {
                    // After the first whip gesture the grip occupies this same point. A
                    // second pointer press correctly grabs it without another impact.
                    // This case stresses 80 impact effects; external-grip pointer semantics
                    // and real rendered single-rope geometry have their own regression below.
                    if (tool == InteractionTool.Whip) surface.Hit(tool, new Point(250, 275));
                    else
                    {
                        surface.BeginPress(new Point(250, 275));
                        if (tool == InteractionTool.Pinch) surface.UpdatePressPosition(new Point(305, 290));
                        surface.EndPress();
                    }
                }
                Require(surface.HitCount == 80 && surface.ActiveEffectCount is > 0 and <= 24,
                    $"{tool} expected 80 hits and 1..24 effects, got {surface.HitCount} hits / {surface.ActiveEffectCount} effects.");
                _ = RenderBitmap(surface, 1000);
                surface.AdvanceForTesting(10);
                Require(surface.ActiveEffectCount == 0 && !surface.IsPinchReturning, $"{tool} did not expire after repeated use.");
            }
        });
        Check("pinch is bounded springs back and cancels on every ui transition", () =>
        {
            surface.Reset(); surface.SelectedTool = InteractionTool.Pinch;
            surface.BeginPress(new Point(250, 275)); surface.UpdatePressPosition(new Point(950, 875));
            Require(surface.IsPinching && surface.PinchDisplacement.Length is > 50 and <= 56.00001, "Pinch displacement did not clamp to 56 DIPs.");
            surface.EndPress(); surface.EndPress();
            Require(!surface.IsPinching && surface.IsPinchReturning && surface.HitCount == 1 && !surface.HasPointerCapture, "Pinch release did not begin one spring return.");
            surface.AdvanceForTesting(.1);
            Require(surface.IsPinchReturning && surface.PinchDisplacement.Length > .01, "Pinch return skipped its visible animation.");
            SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, "codex-pinch-release-100ms.png"), 1000);
            surface.AdvanceForTesting(1);
            Require(!surface.IsPinchReturning && surface.PinchDisplacement.Length < .001, "Pinch return did not settle to zero.");
            var actions = new (string name, Action action)[]
            {
                ("tool", () => surface.SelectedTool = InteractionTool.Egg),
                ("menu", surface.OpenToolMenu), ("clear", surface.ClearEffects),
                ("character", () => surface.SelectedCharacter = CharacterKind.Doubao),
                ("cancel", surface.CancelPress), ("reset", surface.Reset),
                ("disable", () => surface.IsEnabled = false),
                ("exit", surface.RequestExit)
            };
            foreach (var (name, cancel) in actions)
            {
                surface.IsEnabled = true; surface.Reset(); surface.SelectedTool = InteractionTool.Pinch;
                surface.BeginPress(new Point(250, 275)); surface.UpdatePressPosition(new Point(305, 290));
                Require(surface.IsPinching, "Could not begin pinch before " + name);
                cancel();
                Require(!surface.IsPinching && !surface.IsPinchReturning && !surface.HasPointerCapture && surface.PinchDisplacement.Length < .001,
                    name + " retained pinch state, displacement or capture.");
                if (name is "disable" or "exit") Require(!surface.IsToolMenuOpen, name + " left the tool menu open.");
                surface.IsEnabled = true; surface.CloseToolMenu();
            }
        });
        Check("actual pinch deformation stays monotone during eight-direction drags and reverse spring motion", () =>
        {
            const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
            var scene = typeof(VentSurface).GetField("_scene", instancePrivate)?.GetValue(surface)
                ?? throw new InvalidOperationException("Cannot inspect the actual scene for pinch monotonicity.");
            var sceneType = scene.GetType();
            var gradientMethod = sceneType.GetMethod("WeightAndGradient", instancePrivate)
                ?? throw new InvalidOperationException("Actual WeightAndGradient method was not found.");
            double minimumDeterminant = double.PositiveInfinity;
            int samples = 0;
            foreach (var character in Enum.GetValues<CharacterKind>())
            foreach (double anchorX in new[] { 200d, 300d })
            for (int direction = 0; direction < 8; direction++)
            {
                surface.Reset(); surface.SelectedCharacter = character; surface.SelectedTool = InteractionTool.Pinch;
                var anchor = new Point(anchorX, 275);
                surface.BeginPress(anchor);
                double angle = direction * Math.PI / 4;
                surface.UpdatePressPosition(anchor + new Vector(Math.Cos(angle) * 800, Math.Sin(angle) * 800));
                Require(surface.IsPinching && surface.PinchDisplacement.Length > .01, $"Could not start {character} pinch at {anchor}, direction {direction}.");
                var rectValue = sceneType.GetProperty("CharacterRect", instancePrivate)?.GetValue(scene);
                Require(rectValue is Rect, "Could not read the actual selected character's drawing rectangle.");
                var rect = (Rect)rectValue!;
                var heldOffset = surface.PinchDisplacement;
                bool sampledPositive = false, sampledNegative = false;

                void InspectActualMapping(string phase)
                {
                    var offset = surface.PinchDisplacement;
                    double projection = Vector.Multiply(offset, heldOffset);
                    sampledPositive |= projection > 1e-8;
                    sampledNegative |= projection < -1e-8;
                    int xSteps = (int)Math.Ceiling(rect.Width / 8);
                    int ySteps = (int)Math.Ceiling(rect.Height / 8);
                    for (int yi = 0; yi <= ySteps; yi++)
                    for (int xi = 0; xi <= xSteps; xi++)
                    {
                        var point = new Point(Math.Min(rect.Right, rect.Left + xi * 8), Math.Min(rect.Bottom, rect.Top + yi * 8));
                        object?[] arguments = [point, rect, 0d, default(Vector)];
                        gradientMethod.Invoke(scene, arguments);
                        var gradient = (Vector)arguments[3]!;
                        double determinant = 1 + Vector.Multiply(offset, gradient);
                        samples++;
                        minimumDeterminant = Math.Min(minimumDeterminant, determinant);
                        Require(double.IsFinite(determinant) && determinant >= .09999,
                            $"Pinch fold: {character}, anchor {anchorX}, direction {direction}, {phase}, point {point}, determinant {determinant:R}.");
                    }
                }

                InspectActualMapping("held");
                surface.EndPress();
                double previousTime = 0;
                foreach (double time in new[] { .04, .12, .2, .32, .48, .7, .94 })
                {
                    surface.AdvanceForTesting(time - previousTime); previousTime = time;
                    InspectActualMapping($"spring {time:F2}s");
                }
                Require(sampledPositive && sampledNegative, "Regression did not exercise both outward and reversed spring displacement.");
                surface.AdvanceForTesting(.1);
                Require(!surface.IsPinchReturning && surface.PinchDisplacement.Length < .001, "Monotonicity check left a running spring.");
            }
            observations.Add($"Actual WeightAndGradient reflection: {samples} sampled Jacobian determinants; minimum={minimumDeterminant:R}; all three characters, both cheeks, eight directions, held and positive/negative spring phases.");
        });
        Check("viewbox scaled dip input and every new drawing path remain valid", () =>
        {
            foreach (int pixels in new[] { 500, 750, 1000 })
            {
                var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = surface };
                try
                {
                    viewbox.Measure(new Size(pixels, pixels)); viewbox.Arrange(new Rect(0, 0, pixels, pixels)); viewbox.UpdateLayout();
                    var screenPoint = surface.TranslatePoint(new Point(250, 275), viewbox);
                    Require(Math.Abs(screenPoint.X - 250d * pixels / 500) < .01, "Viewbox did not produce the expected display scale.");
                    var dipPoint = viewbox.TranslatePoint(screenPoint, surface);
                    Require((dipPoint - new Point(250, 275)).Length < .01, "Scaled coordinates did not map back to the character's DIPs.");
                    foreach (var tool in Enum.GetValues<InteractionTool>())
                    {
                        surface.Reset(); surface.SelectedTool = tool;
                        surface.BeginPress(dipPoint);
                        if (tool == InteractionTool.Pinch) surface.UpdatePressPosition(dipPoint + new Vector(55, 15));
                        surface.EndPress(); surface.AdvanceForTesting(.1);
                        Require(surface.HitCount == 1 && surface.ActiveEffectCount > 0, $"{tool} missed at {pixels / 5}% scale.");
                        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(viewbox);
                    }
                }
                finally { viewbox.Child = null; }
            }
            PrepareLayout(surface);
        });
        Check("100 150 200 percent rendering", () =>
        {
            surface.Reset();
            foreach (var character in Enum.GetValues<CharacterKind>())
            {
                surface.SelectedCharacter = character;
                foreach (var size in new[] { 500, 750, 1000 })
                    SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, $"{character.ToString().ToLowerInvariant()}-scale-{size}.png"), size);
            }
            surface.SelectedTool = InteractionTool.Poop;
            surface.BeginPress(new Point(250, 275)); surface.AdvanceForTesting(.35);
            SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, "doubao-charging.png"), 1000);
            surface.AdvanceForTesting(1.2);
            SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, "doubao-super-spray.png"), 1000);
            surface.CancelPress();
        });
        Check("all nine tools repeat while held and stop without queued hits", () =>
        {
            foreach (var tool in Enum.GetValues<InteractionTool>())
            {
                surface.Reset(); surface.SelectedTool = tool;
                surface.BeginPress(new Point(250, 275));
                if (tool == InteractionTool.Pinch) surface.UpdatePressPosition(new Point(305, 285));
                double delay = tool == InteractionTool.Poop ? VentSurface.ChargeDurationSeconds : VentSurface.RepeatDelaySeconds;
                surface.AdvanceForTesting(delay - .01);
                int initial = tool is InteractionTool.Poop or InteractionTool.Pinch ? 0 : 1;
                Require(surface.HitCount == initial && !surface.IsRepeating, $"{tool} repeated before its hold threshold.");
                surface.AdvanceForTesting(.02);
                Require(surface.IsRepeating && surface.HitCount > initial, $"{tool} did not enter repeating mode.");
                for (int index = 0; index < 20; index++) surface.AdvanceForTesting(VentSurface.RepeatIntervalSeconds);
                Require(surface.HitCount > initial + 15 && surface.ComboCount > 1 && surface.ActiveEffectCount <= 24, $"{tool} repeat stalled or effects grew unbounded.");
                surface.EndPress();
                int finished = surface.HitCount;
                surface.AdvanceForTesting(10);
                Require(surface.HitCount == finished && !surface.IsPressActive && !surface.IsRepeating && !surface.IsPinching && !surface.IsWhipping && !surface.HasPointerCapture,
                    $"{tool} continued a gesture after release.");
                Require(surface.ActiveEffectCount == 0 && surface.ComboCount == 0, $"{tool} did not clear expired effects/combos.");
            }
        });
        Check("whip movement produces reactions on all three characters and cancels", () =>
        {
            foreach (var character in Enum.GetValues<CharacterKind>())
            {
                surface.Reset(); surface.SelectedCharacter = character; surface.SelectedTool = InteractionTool.Whip;
                surface.BeginPress(new Point(250, 275));
                surface.AdvanceForTesting(.11); surface.UpdatePressPosition(new Point(310, 220));
                int afterFirstDrag = surface.HitCount;
                surface.AdvanceForTesting(.06); surface.UpdatePressPosition(new Point(190, 315));
                Require(surface.HitCount == afterFirstDrag, "Whip movement ignored its 0.1-second hit rate limit.");
                surface.AdvanceForTesting(.05);
                surface.UpdatePressPosition(new Point(190, 315));
                Require(surface.IsWhipping && surface.HitCount >= 2 && surface.IsReacting, $"{character} did not react to moving the soft whip.");
                SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, $"{character.ToString().ToLowerInvariant()}-whip-drag.png"), 1000);
                surface.CancelPress(); surface.AdvanceForTesting(10);
                Require(!surface.IsWhipping && !surface.IsPressActive && !surface.HasPointerCapture && surface.ActiveEffectCount == 0,
                    $"{character} whip did not cancel and expire.");
            }
        });
        Check("one rendered whip can be grabbed outside every character and remains stable through fast sweeps", () =>
        {
            int recorded = 0;
            EventHandler<HitRecordedEventArgs> record = (_, hit) => recorded += hit.Count;
            surface.HitRecorded += record;
            try
            {
                foreach (var character in Enum.GetValues<CharacterKind>())
                {
                    surface.Reset(); surface.SelectedCharacter = character; surface.SelectedTool = InteractionTool.Whip;
                    recorded = 0;
                    surface.FloatingMode = true;
                    surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                    Require(surface.VisibleWhipCount == 1 && CountRenderedWhipStrokes(surface) == 1,
                        $"{character} must display exactly one selectable whip before any hit.");
                    var scene = Descendants<FrameworkElement>(surface).Single(element => element.GetType().Name == "MascotScene");
                    var handle = surface.WhipHandlePosition;
                    var localHandle = handle - new Vector(21, 167);
                    var characterHit = scene.GetType().GetMethod("IsCharacterHit") ?? throw new InvalidOperationException("Missing actual character hit-test method.");
                    Require(!(bool)characterHit.Invoke(scene, [localHandle])!, "The test handle must be outside the character.");
                    Require(ReferenceEquals(VisualTreeHelper.HitTest(scene, localHandle)?.VisualHit, scene),
                        $"{character} transparent scene has no hit-testable handle drawing outside the character.");
                    surface.BeginPress(handle);
                    Require(surface.IsWhipping && surface.IsPressActive && surface.HitCount == 0,
                        "Grabbing the external handle must start the actual drag without inventing a character hit.");
                    surface.AdvanceForTesting(.8);
                    Require(surface.HitCount == 0 && recorded == 0 && !surface.IsRepeating, "A stationary held handle started automatic character hits/feedback.");
                    var initial = surface.WhipPoints.ToArray();
                    Point previous = handle;
                    foreach (var direction in new[] { new Vector(1, 0), new Vector(-1, 0), new Vector(0, -1), new Vector(0, 1),
                        new Vector(1, -1), new Vector(-1, 1), new Vector(1, 1), new Vector(-1, -1) })
                    {
                        var target = new Point(250 + direction.X * 95, 275 + direction.Y * 68);
                        for (int step = 1; step <= 12; step++)
                        {
                            var pointer = previous + (target - previous) * (step / 12d);
                            surface.UpdatePressPosition(pointer); surface.AdvanceForTesting(1d / 120);
                            ValidateWhipGeometry(surface, $"{character} sweep {direction} step {step}");
                            Require((surface.WhipHandlePosition - pointer).Length < .001, "The single handle no longer follows the grabbed pointer.");
                            Require(recorded == surface.HitCount, "A physics-driven whip hit was omitted or duplicated in feedback.");
                        }
                        Require(CountRenderedWhipStrokes(surface) == 1, "A fast sweep drew more than one rope.");
                        previous = target;
                    }
                    Require(surface.HitCount > 0 && surface.IsReacting && surface.RedMarkCount > 0 && recorded == surface.HitCount,
                        $"{character} dragged whip did not produce matched hits, feedback, reaction and cartoon marks.");
                    Require(surface.WhipPoints.Zip(initial, (current, old) => (current - old).Length).Max() > 30,
                        "The visible chain did not move with the sweep.");
                    SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, $"{character.ToString().ToLowerInvariant()}-single-whip-drag.png"), 1000);
                    for (int hit = 0; hit < 80; hit++) surface.Hit(InteractionTool.Whip, new Point(250, 275));
                    Require(surface.ActiveEffectCount <= 24 && surface.VisibleWhipCount == 1 && CountRenderedWhipStrokes(surface) == 1,
                        "Repeated whip hits stacked extra rendered ropes/handles instead of bounded impact feedback.");
                    surface.UpdatePressPosition(new Point(1e9, -1e9)); surface.AdvanceForTesting(1d / 120);
                    ValidateWhipGeometry(surface, "extreme finite captured pointer");
                    surface.UpdatePressPosition(new Point(double.NaN, 0));
                    ValidateWhipGeometry(surface, "ignored non-finite pointer");
                    surface.UpdatePressPosition(new Point(250, 275)); surface.AdvanceForTesting(.05);
                    surface.EndPress(); int releasedHits = surface.HitCount; int releasedRecords = recorded;
                    Point releasedTip = surface.WhipPoints[^1];
                    surface.AdvanceForTesting(.15);
                    Require((surface.WhipPoints[^1] - releasedTip).Length > 1, "The released chain did not continue its inertial motion.");
                    SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, $"{character.ToString().ToLowerInvariant()}-single-whip-release.png"), 1000);
                    surface.AdvanceForTesting(10);
                    ValidateWhipGeometry(surface, "released and settled");
                    Require(surface.HitCount == releasedHits && recorded == releasedRecords && recorded == releasedHits &&
                        !surface.IsWhipping && !surface.IsPressActive && !surface.IsRepeating &&
                        !surface.HasPointerCapture && surface.ActiveEffectCount == 0 && CountRenderedWhipStrokes(surface) == 1,
                        "Release left hit repetition or extra whips behind.");
                    surface.SelectedTool = InteractionTool.Hammer;
                    Require(surface.VisibleWhipCount == 0 && CountRenderedWhipStrokes(surface) == 0, "Changing tools retained the whip drawing.");
                }
            }
            finally { surface.HitRecorded -= record; surface.CancelPress(); surface.FloatingMode = false; }
        });
        Check("cartoon red marks accumulate within limits fade and clear on each character", () =>
        {
            foreach (var character in Enum.GetValues<CharacterKind>())
            {
                surface.Reset(); surface.SelectedCharacter = character;
                for (int index = 0; index < 40; index++) surface.Hit(InteractionTool.Hammer, new Point(250, 275));
                Require(surface.RedMarkCount is > 0 and <= 8 && surface.BlushStrength is > 0 and <= .850001,
                    $"{character} red marks or blush exceeded their bounded cartoon effect.");
                SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, $"{character.ToString().ToLowerInvariant()}-cartoon-red-marks.png"), 1000);
                surface.AdvanceForTesting(6);
                Require(surface.RedMarkCount == 0 && surface.BlushStrength < .001, $"{character} marks did not fade.");
                surface.Hit(InteractionTool.Whip, new Point(250, 275)); surface.ClearEffects();
                Require(surface.RedMarkCount == 0 && surface.BlushStrength < .001, "Clear did not remove red marks immediately.");
            }
        });
        Check("floating layers stay transparent and hover menu hit testing works", () =>
        {
            try
            {
                foreach (var character in Enum.GetValues<CharacterKind>())
                {
                    surface.Reset(); surface.SelectedCharacter = character; surface.FloatingMode = true;
                    surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                    Require(!surface.AreControlsVisible && !surface.IsToolMenuOpen, "Floating controls should hide when not hovered.");
                    var bitmap = RenderBitmap(surface, 500);
                    byte[] pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(24, 230, 1, 1), pixel, 4, 0);
                    if (pixel[3] != 0)
                    {
                        var evidence = new PngBitmapEncoder(); evidence.Frames.Add(BitmapFrame.Create(bitmap));
                        using var failureImage = File.Create(Path.Combine(Path.GetDirectoryName(path)!, $"{character.ToString().ToLowerInvariant()}-floating-alpha-failure.png"));
                        evidence.Save(failureImage);
                    }
                    Require(pixel[3] == 0, $"{character} floating mode retained an opaque card/stage outside the pet: alpha={pixel[3]}, " +
                        $"floating={surface.FloatingMode}, controls={surface.AreControlsVisible}, parent={surface.Parent?.GetType().Name ?? "none"}.");
                    SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, $"{character.ToString().ToLowerInvariant()}-floating.png"), 1000);
                    surface.BeginPress(new Point(24, 230)); surface.EndPress();
                    Require(surface.HitCount == 0, "An empty transparent region counted as a character hit.");
                    surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                    Require(surface.AreControlsVisible, "Hover did not reveal desktop controls.");
                    foreach (var tool in Enum.GetValues<InteractionTool>())
                    {
                        SelectToolByClick(surface, tool);
                        surface.BeginPress(new Point(250, 275));
                        if (tool == InteractionTool.Pinch) surface.UpdatePressPosition(new Point(305, 285));
                        surface.EndPress(); surface.AdvanceForTesting(.1);
                        Require(surface.ActiveEffectCount > 0, $"{character}/{tool} could not hit in transparent mode.");
                        _ = RenderBitmap(surface, 750); surface.ClearEffects();
                    }
                    OpenToolMenuByClick(surface);
                    surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                    Require(surface.AreControlsVisible && surface.IsToolMenuOpen, "Moving into the expanded menu hid its controls.");
                    SaveFrame(surface, Path.Combine(Path.GetDirectoryName(path)!, $"{character.ToString().ToLowerInvariant()}-floating-menu.png"), 1000);
                    surface.CloseToolMenu();
                }
            }
            finally { surface.CancelPress(); surface.CloseToolMenu(); surface.FloatingMode = false; }
        });
        Check("desktop window is transparent topmost movable and clearly closable without toy feedback", () =>
        {
            using var floatingSurface = new VentSurface(assets);
            var desktop = new DesktopPetWindow(floatingSurface);
            bool desktopClosed = false;
            try
            {
                Require(desktop.AllowsTransparency && desktop.WindowStyle == WindowStyle.None && desktop.Topmost && !desktop.ShowInTaskbar && desktop.ResizeMode == ResizeMode.NoResize,
                    "Default desktop window does not have the required transparent always-on-top settings.");
                Require(floatingSurface.FloatingMode && !desktop.IsFeedbackMode && desktop.SendFeedbackButton is null && !desktop.CompleteRound(),
                    "Toy mode created or attempted to send a feedback round.");
                Require(Equals(desktop.MoveHandle.Tag, "DesktopMove") && Equals(desktop.CloseHandle.Tag, "DesktopClose"), "Dedicated move/close handles are missing.");
                desktop.ToolsHandle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(floatingSurface.IsToolMenuOpen, "Desktop tool handle did not open the full menu.");
                desktop.CloseHandle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                desktopClosed = true;
                Require(!floatingSurface.IsAnimationRunning && !floatingSurface.IsToolMenuOpen, "Closing the desktop window left its controls/timer alive.");
            }
            finally { if (!desktopClosed) desktop.Close(); }
            using var classicSurface = new VentSurface(assets);
            var classic = new DesktopPetWindow(classicSurface, classic: true);
            try { Require(!classic.AllowsTransparency && classic.ShowInTaskbar && !classicSurface.FloatingMode, "--classic no longer creates a normal panel window."); }
            finally { classic.Close(); }
            Require(FeedbackDirectory([]) is null, "Default arguments enabled feedback.");
            bool rejected = false;
            try { _ = FeedbackDirectory(["--feedback"]); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "Unbound feedback mode must not silently create a session.");
        });
        Check("feedback window seals one local round on send and an empty round on close", () =>
        {
            foreach (string action in new[] { "send", "empty-close", "discard" })
            {
                string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "feedback-window-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(new
                {
                    schema = 1, sessionId = Guid.NewGuid().ToString("D"), target = "current-agent",
                    createdUtc = DateTimeOffset.UtcNow, expiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
                }));
                using var feedbackSurface = new VentSurface(assets);
                using var round = new FeedbackRound(directory, feedbackSurface);
                var desktop = new DesktopPetWindow(feedbackSurface, round);
                bool desktopClosed = false;
                try
                {
                    Require(desktop.IsFeedbackMode && desktop.SendFeedbackButton is not null, "Bound mode omitted its feedback identity/button.");
                    if (action is "send" or "discard")
                    {
                        feedbackSurface.BeginPress(new Point(250, 275)); feedbackSurface.EndPress();
                        desktop.ReasonEditor!.Text = "没有回答我的问题";
                    }
                    if (action == "send")
                    {
                        desktop.SendFeedbackButton!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Require(round.IsSealed && File.Exists(Path.Combine(directory, "batch.json")), "Send did not atomically seal a local feedback round.");
                        using var payload = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "batch.json")));
                        Require(payload.RootElement.GetProperty("total").GetInt32() == 1 && payload.RootElement.GetProperty("reason").GetString() == "没有回答我的问题",
                            "Window did not record the actual hit/reason.");
                        Require(desktop.FeedbackStatus.Contains("等待当前 AI 读取", StringComparison.Ordinal) && !desktop.CompleteRound(),
                            "Send claimed delivery or duplicated the completed round.");
                    }
                    if (action == "discard")
                        desktop.CloseHandle.ContextMenu!.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    else desktop.Close();
                    desktopClosed = true;
                    Require(round.IsSealed, "Closing a bound window did not finish the round.");
                    if (action != "send")
                    {
                        Require(File.Exists(Path.Combine(directory, "ended.json")) && !File.Exists(Path.Combine(directory, "batch.json")), "Empty/cancelled close failed to finish without a feedback message.");
                        if (action == "discard")
                        {
                            using var ended = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "ended.json")));
                            Require(ended.RootElement.GetProperty("status").GetString() == "cancelled", "Discard did not notify the waiting skill that the round was cancelled.");
                        }
                    }
                }
                finally
                {
                    if (!desktopClosed) desktop.Close();
                    round.Dispose();
                    foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
                    Directory.Delete(directory); // A unique test directory created immediately above.
                }
            }
        });
        Check("explicit feedback discard closes and releases resources even if its end marker cannot be written", () =>
        {
            string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "feedback-discard-failure-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(new
            {
                schema = 1, sessionId = Guid.NewGuid().ToString("D"), target = "current-agent",
                createdUtc = DateTimeOffset.UtcNow, expiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            }));
            using var feedbackSurface = new VentSurface(assets);
            using var round = new FeedbackRound(directory, feedbackSurface);
            var desktop = new DesktopPetWindow(feedbackSurface, round);
            bool closed = false;
            desktop.Closed += (_, _) => closed = true;
            string blockedDestination = Path.Combine(directory, "ended.json");
            try
            {
                // Exercise the actual timer/lifecycle handler without showing a desktop window.
                feedbackSurface.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Require(feedbackSurface.IsAnimationRunning, "The discard fixture did not start the real animation timer.");
                feedbackSurface.SelectedTool = InteractionTool.Pinch;
                feedbackSurface.BeginPress(new Point(250, 275));
                feedbackSurface.UpdatePressPosition(new Point(305, 285));
                feedbackSurface.AdvanceForTesting(.5);
                Require(feedbackSurface.IsPinching && round.TotalCount > 0, "The discard fixture did not retain an active feedback gesture.");
                Directory.CreateDirectory(blockedDestination); // An existing directory makes atomic file publication fail.
                desktop.CloseHandle.ContextMenu!.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(closed, "Explicit discard left the window open when ended.json could not be written.");
                Require(!round.IsSealed && !File.Exists(Path.Combine(directory, "batch.json")) && Directory.Exists(blockedDestination),
                    "Failed discard publication claimed to seal or wrote feedback despite explicit discard.");
                Require(!feedbackSurface.IsAnimationRunning && !feedbackSurface.IsPressActive && !feedbackSurface.IsPinching &&
                    !feedbackSurface.IsPinchReturning && !feedbackSurface.IsWhipping && !feedbackSurface.HasPointerCapture &&
                    !feedbackSurface.IsToolMenuOpen && feedbackSurface.ActiveEffectCount == 0,
                    "Closing after a failed discard left timer, effects or gesture resources active.");
                using var releasedLease = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Require(releasedLease.CanWrite, "Closing after a failed discard retained the session writer lease.");
            }
            finally
            {
                if (Directory.Exists(blockedDestination)) Directory.Delete(blockedDestination);
                if (!closed) desktop.Close();
                round.Dispose();
                foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
                Directory.Delete(directory); // Only this freshly-created fixture directory is removed.
            }
        });
        Check("disposal is repeatable and stops timers", () =>
        {
            surface.Dispose();
            surface.Dispose();
            Require(!surface.IsAnimationRunning, "Animation timer remains active");
            Require(surface.ActiveEffectCount == 0, "Disposed effects remain");
        });
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            passed = failures == 0, failures, results, observations,
            inputBoundary = "BeginPress/EndPress/CancelPress and AdvanceForTesting use the same state machine as WPF pointer handlers and the animation timer. No desktop input was generated; native mouse capture and hardware DPI require manual acceptance."
        }, JsonOptions));
        return failures == 0 ? 0 : 1;
    }

    private static int RenderSequence(string assets, string directory)
    {
        Directory.CreateDirectory(directory);
        using var surface = new VentSurface(assets) { FloatingMode = false };
        surface.SoundEnabled = false;
        const int fps = 30;
        const int length = 45;
        var tools = new[] { InteractionTool.Hammer, InteractionTool.Poop, InteractionTool.PaperReject, InteractionTool.Stamp,
            InteractionTool.Pinch, InteractionTool.SpringGlove, InteractionTool.Jelly, InteractionTool.Egg, InteractionTool.Whip };
        PrepareLayout(surface);
        for (int frame = 0; frame < fps * length; frame++)
        {
            int segment = frame / 150;
            int local = frame % 150;
            var tool = tools[segment];
            if (local == 0)
            {
                surface.Reset();
                surface.SelectedCharacter = segment < 3 ? CharacterKind.Codex : segment < 6 ? CharacterKind.Doubao : CharacterKind.DeepSeek;
                surface.ReplyText = "这段回答没有回答我的问题。";
                OpenToolMenuByClick(surface);
            }
            if (local == 20) GetToolButton(surface, tool).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (local == 35) surface.BeginPress(new Point(250, 275));
            if (tool == InteractionTool.Poop)
            {
                if (local == 40) surface.EndPress();
                if (local == 55) surface.BeginPress(new Point(250, 275));
                if (local == 120) surface.EndPress();
            }
            else if (tool == InteractionTool.Pinch)
            {
                if (local is >= 35 and < 90)
                {
                    double amount = Math.Min(1, (local - 35) / 30d);
                    surface.UpdatePressPosition(new Point(250 + amount * 60, 275 + Math.Sin(amount * Math.PI) * 14));
                }
                if (local == 90) surface.EndPress();
            }
            else
            {
                if (tool == InteractionTool.Whip && local is > 35 and < 95)
                    surface.UpdatePressPosition(new Point(250 + Math.Sin((local - 35) * .18) * 65, 275 + Math.Cos((local - 35) * .18) * 40));
                if (local == 95) surface.EndPress();
            }
            if (local == 140)
            {
                surface.EndPress();
                surface.ClearEffects();
            }
            surface.AdvanceForTesting(1d / fps);
            SaveFrame(surface, Path.Combine(directory, $"frame-{frame:D4}.png"), 1000);
        }
        return 0;
    }

    private static int RenderWhipSequence(string assets, string directory)
    {
        Directory.CreateDirectory(directory);
        using var surface = new VentSurface(assets) { FloatingMode = false, SoundEnabled = false };
        const int fps = 30;
        const int framesPerCharacter = 4 * fps;
        var characters = Enum.GetValues<CharacterKind>();
        Point grabbedHandle = default;
        PrepareLayout(surface);
        for (int frame = 0; frame < characters.Length * framesPerCharacter; frame++)
        {
            int local = frame % framesPerCharacter;
            if (local == 0)
            {
                surface.Reset();
                surface.SelectedCharacter = characters[frame / framesPerCharacter];
                surface.SelectedTool = InteractionTool.Whip;
            }
            if (local == 15)
            {
                grabbedHandle = surface.WhipHandlePosition;
                surface.BeginPress(grabbedHandle);
            }
            if (local is >= 15 and < 100)
            {
                double phase = (local - 15) / (double)fps;
                var sweep = new Point(250 + Math.Sin(phase * 8.4) * 91, 273 + Math.Sin(phase * 6.1) * 64);
                double approach = Math.Min(1, (local - 15) / 8d);
                surface.UpdatePressPosition(grabbedHandle + (sweep - grabbedHandle) * approach);
            }
            if (local == 100) surface.EndPress();
            surface.AdvanceForTesting(1d / fps);
            SaveFrame(surface, Path.Combine(directory, $"frame-{frame:D4}.png"), 1000);
        }
        return 0;
    }

    private static void SaveFrame(VentSurface surface, string path, int pixels)
    {
        var bitmap = RenderBitmap(surface, pixels);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static RenderTargetBitmap RenderBitmap(VentSurface surface, int pixels)
    {
        PrepareLayout(surface);
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96d * pixels / 500, 96d * pixels / 500, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        return bitmap;
    }

    private static void ValidateWhipGeometry(VentSurface surface, string context)
    {
        var points = surface.WhipPoints;
        if (surface.VisibleWhipCount != 1 || points.Count != 18)
            throw new InvalidOperationException($"{context}: expected one 18-point chain, got {surface.VisibleWhipCount} whips / {points.Count} points.");
        for (int index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X < 24.999 || point.X > 477.001 || point.Y < 170.999 || point.Y > 361.001)
                throw new InvalidOperationException($"{context}: chain point {index} escaped its finite physical bounds: {point}.");
            if (index > 0 && (point - points[index - 1]).Length > 13.5)
                throw new InvalidOperationException($"{context}: segment {index} stretched to {(point - points[index - 1]).Length:R} DIPs.");
        }
        if ((points[0] - surface.WhipHandlePosition).Length > .001)
            throw new InvalidOperationException($"{context}: visible chain is detached from its handle.");
    }

    private static int CountRenderedWhipStrokes(VentSurface surface)
    {
        _ = RenderBitmap(surface, 500);
        var scene = Descendants<FrameworkElement>(surface).Single(element => element.GetType().Name == "MascotScene");
        var paint = scene.GetType().GetField("WhipLeather", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as SolidColorBrush
            ?? throw new InvalidOperationException("Cannot identify the actual whip renderer's main stroke paint.");
        var drawing = VisualTreeHelper.GetDrawing(scene) ?? throw new InvalidOperationException("The scene did not create a retained drawing.");
        int Count(Drawing item) => item switch
        {
            DrawingGroup group => group.Children.Sum(Count),
            GeometryDrawing geometry when geometry.Pen?.Brush is SolidColorBrush brush && brush.Color == paint.Color => 1,
            _ => 0
        };
        return Count(drawing);
    }

    private static void OpenToolMenuByClick(VentSurface surface)
    {
        PrepareLayout(surface);
        if (surface.IsToolMenuOpen) return;
        var toggles = Descendants<Button>(surface).Where(button => Equals(button.Tag, "ToolMenuToggle")).ToArray();
        if (toggles.Length != 1) throw new InvalidOperationException($"Expected one real ToolMenuToggle button; found {toggles.Length}.");
        toggles[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PrepareLayout(surface);
        if (!surface.IsToolMenuOpen) throw new InvalidOperationException("Clicking the tool toggle did not expand the menu.");
    }

    private static Button GetToolButton(VentSurface surface, InteractionTool tool)
    {
        PrepareLayout(surface);
        var matches = Descendants<Button>(surface).Where(button => Equals(button.Tag, tool)).ToArray();
        if (matches.Length == 1) return matches[0];
        string labels = string.Join(" | ", Descendants<Button>(surface).Select(button => $"{button.Tag}:{button.Content}"));
        throw new InvalidOperationException($"Tool menu expected one {tool} entry, found {matches.Length}; open={surface.IsToolMenuOpen}; buttons={labels}.");
    }

    private static void SelectToolByClick(VentSurface surface, InteractionTool tool)
    {
        OpenToolMenuByClick(surface);
        GetToolButton(surface, tool).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (surface.SelectedTool != tool || surface.IsToolMenuOpen) throw new InvalidOperationException($"Selecting {tool} did not update the tool and close the menu.");
    }

    private static void PrepareLayout(VentSurface surface)
    {
        // WPF suspends a subtree's layout when its visual parent removes it. Measuring
        // the detached root alone does not resume its descendants, so captures after a
        // Viewbox test can otherwise retain old drawings despite correct live properties.
        // A temporary, never-shown layout parent resumes that normal WPF lifecycle.
        Grid? captureRoot = null;
        if (surface.Parent is null && VisualTreeHelper.GetParent(surface) is null)
        {
            captureRoot = new Grid();
            captureRoot.Children.Add(surface);
        }
        try
        {
            FrameworkElement root = captureRoot ?? (FrameworkElement)surface;
            root.Measure(new Size(500, 500));
            root.Arrange(new Rect(0, 0, 500, 500));
            root.UpdateLayout();
        }
        finally { captureRoot?.Children.Remove(surface); }
    }

    private static Button RequireButton(VentSurface surface, string labelPrefix, string context)
    {
        var buttons = Descendants<Button>(surface).ToArray();
        var matches = buttons.Where(button => button.Content?.ToString()?.StartsWith(labelPrefix, StringComparison.Ordinal) == true).ToArray();
        if (matches.Length == 1) return matches[0];
        string labels = buttons.Length == 0 ? "<none>" : string.Join(" | ", buttons.Select(button => button.Content?.ToString() ?? "<null>"));
        throw new InvalidOperationException($"{context}: expected one button starting with '{labelPrefix}', found {matches.Length}. " +
            $"Visible-tree buttons: {labels}. MeasureValid={surface.IsMeasureValid}; ArrangeValid={surface.IsArrangeValid}; " +
            $"root children={VisualTreeHelper.GetChildrenCount(surface)}.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
