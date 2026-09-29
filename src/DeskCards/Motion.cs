using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace DeskCards;

/// <summary>
/// Fluent 표준 모션. 시간은 Windows 설정 앱과 같은 단계(83 · 167 · 250 · 333ms)를 쓰고,
/// 곡선은 "툭 나와서 스르륵 멈추는" 강한 감속(cubic-bezier 0,0,0,1)이 기본이다. 들어갈 때는 가속(1,0,1,1).
/// Windows의 "애니메이션 효과"를 끄면 모두 곧바로 끝난다.
/// </summary>
internal static class Motion
{
    /// <summary>컨트롤 상태(마우스 올림·누름) 색 바뀜.</summary>
    public const double Faster = 83;
    /// <summary>작은 것이 나타나고 사라짐, 투명도.</summary>
    public const double Fast = 167;
    /// <summary>창·메뉴가 열림, 작은 이동.</summary>
    public const double Normal = 250;
    /// <summary>페이지 전환처럼 큰 이동.</summary>
    public const double Slow = 333;

    /// <summary>들어오는 것: 빠르게 출발해 천천히 멈춘다.</summary>
    public static readonly CubicBezierEase Decelerate = Freeze(new CubicBezierEase(0, 0, 0, 1));
    /// <summary>나가는 것: 천천히 출발해 빠르게 사라진다.</summary>
    public static readonly CubicBezierEase Accelerate = Freeze(new CubicBezierEase(1, 0, 1, 1));
    /// <summary>화면 안에서 자리를 옮기는 것.</summary>
    public static readonly CubicBezierEase Standard = Freeze(new CubicBezierEase(0.8, 0, 0.2, 1));

    /// <summary>Windows 설정 › 접근성 › 시각 효과 › 애니메이션 효과.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static DoubleAnimation In(double? from, double to, double milliseconds) =>
        Make(from, to, milliseconds, Decelerate);

    public static DoubleAnimation Out(double? from, double to, double milliseconds) =>
        Make(from, to, milliseconds, Accelerate);

    public static DoubleAnimation Move(double? from, double to, double milliseconds) =>
        Make(from, to, milliseconds, Standard);

    private static DoubleAnimation Make(double? from, double to, double milliseconds, IEasingFunction easing)
    {
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(Enabled ? milliseconds : 0), EasingFunction = easing };
        if (from != null) a.From = from;
        return a;
    }

    /// <summary>프레임마다 직접 그리는 애니메이션의 진행률(0~1)에 감속 곡선을 입힌다.</summary>
    public static double Decel(double progress) => Decelerate.Ease(Math.Clamp(progress, 0, 1));

    /// <summary>프레임마다 직접 그리는 애니메이션에 쓸 시간. 애니메이션 효과가 꺼져 있으면 0.</summary>
    public static double Ms(double milliseconds) => Enabled ? milliseconds : 0;

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

/// <summary>CSS·WinUI의 cubic-bezier(x1, y1, x2, y2) 곡선. 시작점 (0,0), 끝점 (1,1).</summary>
internal sealed class CubicBezierEase : EasingFunctionBase
{
    private readonly double _x1, _y1, _x2, _y2;

    public CubicBezierEase() : this(0, 0, 0, 1) { }

    public CubicBezierEase(double x1, double y1, double x2, double y2)
    {
        _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
        EasingMode = EasingMode.EaseIn; // 곡선 모양을 그대로 쓴다(뒤집지 않는다).
    }

    private static double Bezier(double t, double p1, double p2)
    {
        double u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }

    protected override double EaseInCore(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        // x(t) = x 를 이분법으로 푼다(단조 증가라 늘 수렴한다).
        double lo = 0, hi = 1, t = x;
        for (int i = 0; i < 40; i++)
        {
            t = (lo + hi) / 2;
            if (Bezier(t, _x1, _x2) < x) lo = t; else hi = t;
        }
        return Bezier(t, _y1, _y2);
    }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase(_x1, _y1, _x2, _y2);
}
