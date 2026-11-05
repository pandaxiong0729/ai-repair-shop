namespace QiaoQiaoAI.UI;

public enum InteractionTool { Hammer, Poop, PaperReject, Stamp, Pinch, SpringGlove, Jelly, Egg, Whip }

public sealed record InteractionToolDescriptor(InteractionTool Tool, string Emoji, string Name, string Hint);

/// <summary>One recorded hit, or a bounded catch-up batch. Count is always positive.</summary>
public sealed class HitRecordedEventArgs(InteractionTool tool, CharacterKind character, int count = 1, int comboCount = 0) : EventArgs
{
    public InteractionTool Tool { get; } = tool;
    public CharacterKind Character { get; } = character;
    public int Count { get; } = Math.Max(1, count);
    public int ComboCount { get; } = comboCount;
}

/// <summary>The single catalog used by the picker, selected-tool help and accessibility labels.</summary>
public static class InteractionTools
{
    public static IReadOnlyList<InteractionToolDescriptor> All { get; } = Array.AsReadOnly<InteractionToolDescriptor>(
    [
        new(InteractionTool.Hammer, "🔨", "橡皮锤", "点按挥锤，按住连续敲"),
        new(InteractionTool.Poop, "💩", "便便喷射", "轻点投一颗，按住蓄力连喷"),
        new(InteractionTool.PaperReject, "📄", "扔纸团", "点按投出揉皱纸团，按住连续扔"),
        new(InteractionTool.Stamp, "滚", "滚字章", "点按印一个滚字，按住连续盖章"),
        new(InteractionTool.Pinch, "🤏", "捏脸弹回", "按住拖动连续拉，松手弹回"),
        new(InteractionTool.SpringGlove, "🥊", "弹簧拳套", "点按出拳，按住连弹软拳套"),
        new(InteractionTool.Jelly, "🫠", "果冻压扁", "点按压扁，按住连续果冻晃"),
        new(InteractionTool.Egg, "🥚", "砸鸡蛋", "点按抛蛋，按住连续砸"),
        new(InteractionTool.Whip, "🪢", "软鞭抽打", "抓住鞭柄来回甩；长按角色连甩")
    ]);

    public static InteractionToolDescriptor Get(InteractionTool tool) =>
        All.FirstOrDefault(item => item.Tool == tool) ?? throw new ArgumentOutOfRangeException(nameof(tool));
}
