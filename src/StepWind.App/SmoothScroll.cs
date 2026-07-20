using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StepWind.App;

/// <summary>
/// Animated, pixel-smooth mouse-wheel scrolling — the thing that makes a list feel premium
/// instead of teleporting a few rows per notch. WPF's ScrollViewer.VerticalOffset is
/// read-only, so an attached proxy property is animated and its change-callback drives
/// ScrollToVerticalOffset. Wheel ticks accumulate into a moving target (tracked per viewer)
/// so a fast spin glides instead of stuttering; the target resyncs to the real offset once a
/// glide finishes, so dragging the scrollbar in between never causes a jump.
///
/// Attach with SmoothScroll.Enabled="True" on a ListBox or ScrollViewer. Our lists are bounded
/// (timeline capped at 200, browser/search capped server-side), so switching the inner viewer
/// to pixel mode to get smooth offsets costs no meaningful virtualization.
/// </summary>
public static class SmoothScroll
{
    private sealed class State
    {
        public double Target;
        public bool Animating;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

    private static readonly DependencyProperty OffsetProxyProperty = DependencyProperty.RegisterAttached(
        "OffsetProxy", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0.0, OnProxyChanged));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is not true)
        {
            return;
        }

        fe.Loaded += (_, _) =>
        {
            ScrollViewer? sv = fe as ScrollViewer ?? FindScrollViewer(fe);
            if (sv is null)
            {
                return;
            }

            sv.CanContentScroll = false; // pixel offsets → the animation is smooth
            sv.PreviewMouseWheel -= OnWheel;
            sv.PreviewMouseWheel += OnWheel;
        };
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || sv.ScrollableHeight <= 0)
        {
            return;
        }

        State st = States.GetOrCreateValue(sv);
        double baseOffset = st.Animating ? st.Target : sv.VerticalOffset;
        double target = Math.Clamp(baseOffset - e.Delta, 0, sv.ScrollableHeight);
        st.Target = target;
        st.Animating = true;

        var anim = new DoubleAnimation
        {
            From = sv.VerticalOffset,
            To = target,
            Duration = TimeSpan.FromMilliseconds(280),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) => st.Animating = false;
        sv.BeginAnimation(OffsetProxyProperty, anim, HandoffBehavior.SnapshotAndReplace);
        e.Handled = true;
    }

    private static void OnProxyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv && e.NewValue is double v)
        {
            sv.ScrollToVerticalOffset(v);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
            {
                return sv;
            }

            ScrollViewer? found = FindScrollViewer(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
