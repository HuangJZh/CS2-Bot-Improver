using System.Numerics;

namespace BotAimImprover;

internal static class AimVisibilityPolicy
{
    internal static bool SegmentIntersectsSmoke(Vector3 eye, Vector3 target, Vector3 center, float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0) return false;
        Vector3 segment = target - eye;
        float lengthSquared = segment.LengthSquared();
        float fraction = lengthSquared > 0 ? Math.Clamp(Vector3.Dot(center - eye, segment) / lengthSquared, 0, 1) : 0;
        return Vector3.DistanceSquared(eye + segment * fraction, center) <= radius * radius;
    }

    internal static float SmokeRadius(float ageSeconds)
        => ageSeconds is < 0 or >= 20 ? 0 : 155f * Math.Clamp(ageSeconds / 0.7f, 0, 1);
}
