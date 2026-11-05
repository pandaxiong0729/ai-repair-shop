using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace QiaoQiaoAI.Plugin;

/// <summary>
/// Hides one host visual without replacing its local value, binding, style or existing animation.
/// Removing our composed clock restores whatever base value is current at that time.
/// </summary>
internal sealed class OwnedOpacityMask : IDisposable
{
    private AnimationClock? _clock;

    public OwnedOpacityMask(UIElement element)
    {
        var animation = new ObjectAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.Zero),
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.KeyFrames.Add(new DiscreteObjectKeyFrame(Brushes.Transparent, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.Freeze();
        _clock = (AnimationClock)animation.CreateClock(true);
        element.ApplyAnimationClock(UIElement.OpacityMaskProperty, _clock, HandoffBehavior.Compose);
    }

    public void Dispose()
    {
        var clock = _clock;
        _clock = null;
        // Never ClearValue/SetValue here: another plugin may have changed the base mask or
        // replaced the animation while this session was open. Only our own clock is removed.
        clock?.Controller?.Remove();
    }
}
