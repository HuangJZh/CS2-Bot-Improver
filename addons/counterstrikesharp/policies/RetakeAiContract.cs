namespace BotCombatPolicy;

public static class RetakeAiContract
{
    public const string Tree="scripts/ai/custom_improver/retake.kv3";
    public static bool Eligible(bool enabled,int type,int mode,bool planted,bool roundLive)
        =>enabled&&type==0&&mode is 0 or 1&&planted&&roundLive;
}
