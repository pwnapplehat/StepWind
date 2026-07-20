using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StepWind.App;

/// <summary>
/// Tasteful, performant motion. Everything here animates only opacity and transforms — which
/// WPF composites on the render thread — so it stays smooth and never taxes the CPU/GPU the
/// way animated blur or layout would. Durations are short (deliberate, not sluggish) with a
/// cubic ease-out so things decelerate into place like a well-made native app.
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Fade + rise an element into place — used for view switches and panels.</summary>
    public static void PlayEnter(FrameworkElement element, double fromY = 16, double milliseconds = 260)
    {
        var translate = new TranslateTransform(0, fromY);
        element.RenderTransform = translate;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var dur = new Duration(TimeSpan.FromMilliseconds(milliseconds));

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, dur) { EasingFunction = EaseOut });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromY, 0, dur) { EasingFunction = EaseOut });
    }

    /// <summary>Hooks an element so it plays the enter animation every time it becomes visible.</summary>
    public static void AnimateOnShow(FrameworkElement element)
    {
        element.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                PlayEnter(element);
            }
        };
    }
}
