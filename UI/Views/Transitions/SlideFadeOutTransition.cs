using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HyPrism.UI.Views.Transitions
{
    public class SlideFadeOutTransition : IPageTransition
    {
        public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(550);
        public double Offset { get; set; } = 40.0;
        public bool IsVertical { get; set; } = true;

        public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
        {
            var easing = new SineEaseInOut();
            var exitOffset = forward ? -Offset : Offset;
            var enterOffset = forward ? Offset : -Offset;

            if (from != null)
            {
                var animation = new Animation
                {
                    FillMode = FillMode.Forward,
                    Duration = Duration,
                    Easing = easing,
                    Children =
                    {
                        new KeyFrame
                        {
                            Cue = new Cue(0),
                            Setters =
                            {
                                new Setter(Visual.OpacityProperty, 1.0),
                                new Setter(TranslateTransform.XProperty, 0.0),
                                new Setter(TranslateTransform.YProperty, 0.0)
                            }
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(1),
                            Setters =
                            {
                                new Setter(Visual.OpacityProperty, 0.0),
                                new Setter(TranslateTransform.XProperty, IsVertical ? 0.0 : exitOffset),
                                new Setter(TranslateTransform.YProperty, IsVertical ? exitOffset : 0.0)
                            }
                        }
                    }
                };
                await animation.RunAsync(from, cancellationToken);
            }

            if (to != null)
            {
                to.IsVisible = true;

                var animation = new Animation
                {
                    FillMode = FillMode.Forward,
                    Duration = Duration,
                    Easing = easing,
                    Children =
                    {
                        new KeyFrame
                        {
                            Cue = new Cue(0),
                            Setters =
                            {
                                new Setter(Visual.OpacityProperty, 0.0),
                                new Setter(TranslateTransform.XProperty, IsVertical ? 0.0 : enterOffset),
                                new Setter(TranslateTransform.YProperty, IsVertical ? enterOffset : 0.0)
                            }
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(1),
                            Setters =
                            {
                                new Setter(Visual.OpacityProperty, 1.0),
                                new Setter(TranslateTransform.XProperty, 0.0),
                                new Setter(TranslateTransform.YProperty, 0.0)
                            }
                        }
                    }
                };
                await animation.RunAsync(to, cancellationToken);
            }
        }
    }
}
