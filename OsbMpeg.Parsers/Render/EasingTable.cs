using OsbMpeg.Ir;

namespace OsbMpeg.Render;

/// <summary>Standard Penner easing equations, indexed to match osu-framework's Easing enum
/// order (0..34; see Ir/SbEasing.cs). t is normalized progress in [0,1].</summary>
public static class EasingTable
{
    private const double Pi = Math.PI;

    public static double Apply(SbEasing easing, double t) => easing switch
    {
        SbEasing.None => t,
        SbEasing.Out => OutQuad(t),
        SbEasing.In => InQuad(t),
        SbEasing.InQuad => InQuad(t),
        SbEasing.OutQuad => OutQuad(t),
        SbEasing.InOutQuad => InOutQuad(t),
        SbEasing.InCubic => Math.Pow(t, 3),
        SbEasing.OutCubic => 1 - Math.Pow(1 - t, 3),
        SbEasing.InOutCubic => t < 0.5 ? 4 * Math.Pow(t, 3) : 1 - Math.Pow(-2 * t + 2, 3) / 2,
        SbEasing.InQuart => Math.Pow(t, 4),
        SbEasing.OutQuart => 1 - Math.Pow(1 - t, 4),
        SbEasing.InOutQuart => t < 0.5 ? 8 * Math.Pow(t, 4) : 1 - Math.Pow(-2 * t + 2, 4) / 2,
        SbEasing.InQuint => Math.Pow(t, 5),
        SbEasing.OutQuint => 1 - Math.Pow(1 - t, 5),
        SbEasing.InOutQuint => t < 0.5 ? 16 * Math.Pow(t, 5) : 1 - Math.Pow(-2 * t + 2, 5) / 2,
        SbEasing.InSine => 1 - Math.Cos(t * Pi / 2),
        SbEasing.OutSine => Math.Sin(t * Pi / 2),
        SbEasing.InOutSine => -(Math.Cos(Pi * t) - 1) / 2,
        SbEasing.InExpo => t == 0 ? 0 : Math.Pow(2, 10 * t - 10),
        SbEasing.OutExpo => t == 1 ? 1 : 1 - Math.Pow(2, -10 * t),
        SbEasing.InOutExpo => InOutExpo(t),
        SbEasing.InCirc => 1 - Math.Sqrt(1 - Math.Pow(t, 2)),
        SbEasing.OutCirc => Math.Sqrt(1 - Math.Pow(t - 1, 2)),
        SbEasing.InOutCirc => InOutCirc(t),
        SbEasing.InElastic => InElastic(t),
        SbEasing.OutElastic => OutElastic(t),
        SbEasing.OutElasticHalf => OutElasticAmplitude(t, 0.5),
        SbEasing.OutElasticQuarter => OutElasticAmplitude(t, 0.25),
        SbEasing.InOutElastic => InOutElastic(t),
        SbEasing.InBack => InBack(t),
        SbEasing.OutBack => OutBack(t),
        SbEasing.InOutBack => InOutBack(t),
        SbEasing.InBounce => 1 - OutBounce(1 - t),
        SbEasing.OutBounce => OutBounce(t),
        SbEasing.InOutBounce => InOutBounce(t),
        _ => t,
    };

    private static double InQuad(double t) => t * t;
    private static double OutQuad(double t) => 1 - (1 - t) * (1 - t);
    private static double InOutQuad(double t) => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

    private static double InOutExpo(double t) => t switch
    {
        0 => 0,
        1 => 1,
        < 0.5 => Math.Pow(2, 20 * t - 10) / 2,
        _ => (2 - Math.Pow(2, -20 * t + 10)) / 2,
    };

    private static double InOutCirc(double t) => t < 0.5
        ? (1 - Math.Sqrt(1 - Math.Pow(2 * t, 2))) / 2
        : (Math.Sqrt(1 - Math.Pow(-2 * t + 2, 2)) + 1) / 2;

    private static double InElastic(double t)
    {
        if (t is 0 or 1) return t;
        const double c4 = 2 * Pi / 3;
        return -Math.Pow(2, 10 * t - 10) * Math.Sin((t * 10 - 10.75) * c4);
    }

    private static double OutElastic(double t) => OutElasticAmplitude(t, 1);

    private static double OutElasticAmplitude(double t, double amplitude)
    {
        if (t is 0 or 1) return t;
        var c4 = 2 * Pi / 3;
        return Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * c4) * amplitude + 1;
    }

    private static double InOutElastic(double t)
    {
        if (t is 0 or 1) return t;
        const double c5 = 2 * Pi / 4.5;
        return t < 0.5
            ? -(Math.Pow(2, 20 * t - 10) * Math.Sin((20 * t - 11.125) * c5)) / 2
            : Math.Pow(2, -20 * t + 10) * Math.Sin((20 * t - 11.125) * c5) / 2 + 1;
    }

    private static double InBack(double t)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return c3 * t * t * t - c1 * t * t;
    }

    private static double OutBack(double t)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    private static double InOutBack(double t)
    {
        const double c1 = 1.70158, c2 = c1 * 1.525;
        return t < 0.5
            ? Math.Pow(2 * t, 2) * ((c2 + 1) * 2 * t - c2) / 2
            : (Math.Pow(2 * t - 2, 2) * ((c2 + 1) * (t * 2 - 2) + c2) + 2) / 2;
    }

    private static double OutBounce(double t)
    {
        const double n1 = 7.5625, d1 = 2.75;
        return t switch
        {
            < 1 / d1 => n1 * t * t,
            < 2 / d1 => n1 * (t -= 1.5 / d1) * t + 0.75,
            < 2.5 / d1 => n1 * (t -= 2.25 / d1) * t + 0.9375,
            _ => n1 * (t -= 2.625 / d1) * t + 0.984375,
        };
    }

    private static double InOutBounce(double t) => t < 0.5
        ? (1 - OutBounce(1 - 2 * t)) / 2
        : (1 + OutBounce(2 * t - 1)) / 2;
}
