using System;
using System.Windows.Media.Animation;

namespace DeskCards;

/// <summary>처음엔 빠르고 끝으로 갈수록 느려지는 애니메이션. 시간과 시작·끝 값은 호출한 쪽에서 정한다.</summary>
internal static class Motion
{
    public static DoubleAnimation CubicOut(double from, double to, double milliseconds) =>
        EaseOut(from, to, milliseconds, new CubicEase());

    public static DoubleAnimation QuarticOut(double from, double to, double milliseconds) =>
        EaseOut(from, to, milliseconds, new QuarticEase());

    private static DoubleAnimation EaseOut(double from, double to, double milliseconds, EasingFunctionBase easing)
    {
        easing.EasingMode = EasingMode.EaseOut;
        return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing };
    }

    /// <summary>프레임마다 직접 그리는 애니메이션의 진행률. 입력 범위는 호출한 쪽에서 정한다.</summary>
    public static double EaseOut(double progress, int power) => 1 - Math.Pow(1 - progress, power);
}
