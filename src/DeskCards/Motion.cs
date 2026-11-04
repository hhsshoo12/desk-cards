using System;
using Windows.UI.ViewManagement;

namespace DeskCards;

/// <summary>
/// Fluent 표준 모션의 시간·곡선 값. 시간은 Windows 설정 앱과 같은 단계(83 · 167 · 250 · 333ms)를 쓰고,
/// 곡선은 "툭 나와서 스르륵 멈추는" 강한 감속(cubic-bezier 0,0,0,1)이 기본이다.
/// Windows의 "애니메이션 효과"를 끄면 모두 곧바로 끝난다.
/// </summary>
internal static class Motion
{
    public const double Faster = 83;
    public const double Fast = 167;
    public const double Normal = 250;
    public const double Slow = 333;

    private static readonly UISettings Settings = new();

    /// <summary>Windows 설정 › 접근성 › 시각 효과 › 애니메이션 효과.</summary>
    public static bool Enabled => Settings.AnimationsEnabled;

    /// <summary>프레임마다 직접 그리는 애니메이션의 진행률(0~1)에 감속 곡선(0,0,0,1)을 입힌다.</summary>
    public static double Decel(double progress) => Bezier(Math.Clamp(progress, 0, 1), 0, 0, 0, 1);

    /// <summary>나가는 것: 천천히 출발해 빠르게 사라지는 가속 곡선(1,0,1,1).</summary>
    public static double Accel(double progress) => Bezier(Math.Clamp(progress, 0, 1), 1, 0, 1, 1);

    /// <summary>프레임마다 직접 그리는 애니메이션에 쓸 시간. 애니메이션 효과가 꺼져 있으면 0.</summary>
    public static double Ms(double milliseconds) => Enabled ? milliseconds : 0;

    private static double Curve(double t, double p1, double p2)
    {
        double u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }

    /// <summary>CSS·WinUI의 cubic-bezier(x1, y1, x2, y2). x(t) = x 를 이분법으로 푼다.</summary>
    private static double Bezier(double x, double x1, double y1, double x2, double y2)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double lo = 0, hi = 1, t = x;
        for (int i = 0; i < 40; i++)
        {
            t = (lo + hi) / 2;
            if (Curve(t, x1, x2) < x) lo = t; else hi = t;
        }
        return Curve(t, y1, y2);
    }
}