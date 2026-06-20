using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace eBackup.App;

/// <summary>
/// Fluent-появление «стаггером»: дочерние элементы панели мягко всплывают (сдвиг снизу + проявление)
/// по очереди — современный, «живой» вход контента. Зовётся после построения/обновления списка
/// (напр. в RefreshAsync). Сдвиг ограничен по индексу, чтобы длинные списки не анимировались долго.
/// </summary>
public static class Entrance
{
    public static void Play(Panel? panel, double shift = 14, int staggerMs = 32, int durationMs = 300)
    {
        if (panel is null)
            return;

        var i = 0;
        foreach (var child in panel.Children)
        {
            if (child is not UIElement el)
                continue;

            var translate = new TranslateTransform { Y = shift };
            el.RenderTransform = translate;
            el.Opacity = 0;

            var begin = TimeSpan.FromMilliseconds(Math.Min(i, 12) * staggerMs); // кап стаггера для длинных списков
            var dur = TimeSpan.FromMilliseconds(durationMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var sb = new Storyboard();

            var fade = new DoubleAnimation { To = 1, BeginTime = begin, Duration = dur, EasingFunction = ease };
            Storyboard.SetTarget(fade, el);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);

            var slide = new DoubleAnimation { To = 0, BeginTime = begin, Duration = dur, EasingFunction = ease };
            Storyboard.SetTarget(slide, translate);
            Storyboard.SetTargetProperty(slide, "Y");
            sb.Children.Add(slide);

            sb.Begin();
            i++;
        }
    }
}
