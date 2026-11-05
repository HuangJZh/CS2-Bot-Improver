using System.Text.Json;
namespace BotCombatPolicy;
public static class CombatBackend
{
    public static bool Native(string moduleDirectory)
    {
        try
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(moduleDirectory,"../BotAimImprover/human-aim.json"))));
            return !doc.RootElement.TryGetProperty("NativeCombat",out var value)||value.ValueKind!=JsonValueKind.False;
        }
        catch(IOException){return true;}
        catch(JsonException){return true;}
        catch(InvalidOperationException){return true;}
    }
}
