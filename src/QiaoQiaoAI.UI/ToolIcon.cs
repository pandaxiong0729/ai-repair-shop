using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace QiaoQiaoAI.UI;

/// <summary>Scalable monochrome tool illustrations, shared by the picker and current-tool indicator.</summary>
public sealed class ToolIcon : FrameworkElement
{
    public static readonly DependencyProperty ToolProperty = DependencyProperty.Register(
        nameof(Tool), typeof(InteractionTool), typeof(ToolIcon),
        new FrameworkPropertyMetadata(InteractionTool.Hammer, FrameworkPropertyMetadataOptions.AffectsRender),
        value => Enum.IsDefined((InteractionTool)value));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(ToolIcon), new FrameworkPropertyMetadata(Brushes.Black,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public InteractionTool Tool
    {
        get => (InteractionTool)GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public ToolIcon(InteractionTool tool)
    {
        Tool = tool;
        Focusable = false;
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(40, availableSize.Width), Math.Min(40, availableSize.Height));

    private static Geometry Shape(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    private static readonly Geometry PaperOutline = Shape(
        "M 14,12 L 27,7 36,10 46,9 53,19 57,29 53,41 48,51 37,56 25,52 14,54 7,42 10,31 7,22 Z");
    private static readonly Geometry PaperFolds = Shape(
        "M 14,12 L 19,26 10,31 M 19,26 L 31,21 27,7 M 31,21 L 42,28 53,19 " +
        "M 42,28 L 37,39 53,41 M 19,26 L 25,39 14,54 M 25,39 L 37,39 37,56 " +
        "M 25,39 L 20,46 M 42,28 L 47,31");

    // The fingers meet the upper/lower edges of a cheek pulled toward the hand.
    private static readonly Geometry PinchedFace = Shape(
        "M 28,27 C 25,15 32,8 41,8 C 53,8 59,17 59,30 " +
        "C 59,45 52,54 42,54 C 33,54 27,48 27,42 " +
        "Q 22,39 22,35 Q 22,31 28,27 Z");
    private static readonly Geometry PinchingHand = Shape(
        "M 5,27 Q 5,22 9,22 L 16,22 Q 20,23 24,27 " +
        "C 27,29 25,33 22,32 L 15,29 L 11,39 L 22,36 " +
        "C 26,35 28,38 25,41 L 18,45 C 17,50 13,54 6,53 Z");
    private static readonly Geometry PinchExpression = Shape(
        "M 34,22 L 39,26 34,29 M 51,22 L 46,26 51,29 " +
        "M 34,17 L 39,19 M 47,19 L 52,17 M 8,45 L 14,46");
    private static readonly Geometry PainTear = Shape(
        "M 57,35 C 61,40 61,44 57,44 C 54,44 54,40 57,35 Z");

    private static readonly Geometry SquashedFace = Shape(
        "M 6,43 C 8,35 22,35 32,35 C 43,35 56,35 58,43 " +
        "C 61,53 47,56 32,56 C 17,56 3,53 6,43 Z");
    private static readonly Geometry SquashedExpression = Shape(
        "M 18,41 L 23,44 18,47 M 46,41 L 41,44 46,47 M 27,51 Q 32,47 37,51");
    private static readonly Geometry Compression = Shape(
        "M 9,30 L 55,30 M 32,7 L 32,22 M 25,16 L 32,23 39,16");

    private static readonly Geometry WhipHandle = Shape("M 6,51 L 13,58 27,44 20,37 Z");
    private static readonly Geometry WhipGrip = Shape("M 11,47 L 18,54 M 15,43 L 22,50");
    private static readonly Geometry WhipCord = Shape(
        "M 24,40 C 31,29 43,44 54,30 C 64,16 47,5 36,13 C 28,19 24,17 19,7");

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double scale = Math.Min(ActualWidth, ActualHeight) / 64;
        drawing.PushTransform(new TranslateTransform((ActualWidth - 64 * scale) / 2,
            (ActualHeight - 64 * scale) / 2));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        var outline = Stroke(3.2);
        var detail = Stroke(2.5);
        switch (Tool)
        {
            case InteractionTool.PaperReject:
                drawing.DrawGeometry(null, outline, PaperOutline);
                drawing.DrawGeometry(null, detail, PaperFolds);
                break;
            case InteractionTool.Stamp:
                DrawGlyph(drawing, "滚", "Microsoft YaHei UI", FontWeights.Bold, 52);
                break;
            case InteractionTool.Pinch:
                drawing.DrawGeometry(null, outline, PinchedFace);
                drawing.DrawGeometry(null, outline, PinchingHand);
                drawing.DrawGeometry(null, detail, PinchExpression);
                drawing.DrawEllipse(Foreground, null, new Point(43, 41), 3.6, 5.4);
                drawing.DrawGeometry(Foreground, null, PainTear);
                break;
            case InteractionTool.Jelly:
                drawing.DrawGeometry(null, outline, Compression);
                drawing.DrawGeometry(null, outline, SquashedFace);
                drawing.DrawGeometry(null, detail, SquashedExpression);
                break;
            case InteractionTool.Whip:
                drawing.DrawGeometry(null, outline, WhipCord);
                drawing.DrawGeometry(null, outline, WhipHandle);
                drawing.DrawGeometry(null, detail, WhipGrip);
                break;
            default:
                DrawGlyph(drawing, InteractionTools.Get(Tool).Emoji, "Segoe UI Emoji", FontWeights.Normal, 46);
                break;
        }
        drawing.Pop();
        drawing.Pop();
    }

    private Pen Stroke(double width) => new(Foreground, width)
    {
        StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
    };

    private void DrawGlyph(DrawingContext drawing, string text, string font, FontWeight weight, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily(font), FontStyles.Normal, weight, FontStretches.Normal), size,
            Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        // Center the visible glyph instead of its font line box, which has large emoji/CJK ascender space.
        var geometry = formatted.BuildGeometry(new Point());
        var bounds = geometry.Bounds;
        if (bounds.IsEmpty) return;
        drawing.PushTransform(new TranslateTransform(32 - bounds.X - bounds.Width / 2,
            32 - bounds.Y - bounds.Height / 2));
        drawing.DrawGeometry(Foreground, null, geometry);
        drawing.Pop();
    }
}
