using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace eBackup.App;

/// <summary>
/// Лёгкое «вдавливание» при нажатии (scale 0.97 → 1, плавно, Fluent ease-out) для любого контрола —
/// единая тактильность кнопок и вкладок. Включается вложенным свойством прямо в стилях
/// (<c>local:PressScale.IsEnabled="True"</c>), без правки каждого элемента.
/// </summary>
public static class PressScale
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(PressScale),
            new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool v) => o.SetValue(IsEnabledProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el)
            return;

        if ((bool)e.NewValue)
        {
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            if (el.RenderTransform is not ScaleTransform)
                el.RenderTransform = new ScaleTransform();
            el.PointerPressed += OnDown;
            el.PointerReleased += OnUp;
            el.PointerExited += OnUp;
            el.PointerCanceled += OnUp;
            el.PointerCaptureLost += OnUp;
        }
        else
        {
            el.PointerPressed -= OnDown;
            el.PointerReleased -= OnUp;
            el.PointerExited -= OnUp;
            el.PointerCanceled -= OnUp;
            el.PointerCaptureLost -= OnUp;
        }
    }

    private static void OnDown(object sender, PointerRoutedEventArgs e) => AnimateTo((UIElement)sender, 0.97, 70);
    private static void OnUp(object sender, PointerRoutedEventArgs e) => AnimateTo((UIElement)sender, 1.0, 120);

    private static void AnimateTo(UIElement el, double to, int ms)
    {
        if (el.RenderTransform is not ScaleTransform st)
            return;

        var sb = new Storyboard();
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var da = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(da, st);
            Storyboard.SetTargetProperty(da, prop);
            sb.Children.Add(da);
        }
        sb.Begin();
    }
}
