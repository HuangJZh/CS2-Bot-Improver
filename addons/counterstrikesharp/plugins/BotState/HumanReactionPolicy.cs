namespace BotState;

internal static class HumanReactionPolicy
{
    internal static (float Min, float Max) RearDelay(string? difficulty) => difficulty?.ToLowerInvariant() switch
    {
        "low" => (0.45f, 0.65f),
        "high" => (0.20f, 0.30f),
        _ => (0.30f, 0.45f)
    };

    internal static float YawDifference(float facingDegrees, float directionDegrees)
        => MathF.Abs((directionDegrees - facingDegrees + 540f) % 360f - 180f);

    internal static bool OutsideScreen(float facingDegrees, float directionDegrees)
        => YawDifference(facingDegrees, directionDegrees) > 53.13f;
}
