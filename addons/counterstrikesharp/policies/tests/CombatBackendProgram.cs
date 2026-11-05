using BotCombatPolicy;
var temporary=Directory.CreateTempSubdirectory("cs2-native-backend-");int count=0;
void Check(bool condition,string name){if(!condition)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
try
{
 var plugins=Path.Combine(temporary.FullName,"plugins");
 Directory.CreateDirectory(Path.Combine(plugins,"BotAimImprover"));
 string config=Path.Combine(plugins,"BotAimImprover","human-aim.json");
 foreach(string module in new[]{"BotState","BotSupport"})
 {
  string path=Path.Combine(plugins,module);Directory.CreateDirectory(path);
  if(File.Exists(config))File.Delete(config);
  Check(!CombatBackend.Native(path),module+" defaults to restored human combat when no configuration exists");
  File.WriteAllText(config,"{\"NativeCombat\":false}");
  Check(!CombatBackend.Native(path),module+" reads the same explicit human-backend switch as the aim module");
  File.WriteAllText(config,"{\"NativeCombat\":true}");
  Check(CombatBackend.Native(path),module+" follows the explicit native-backend switch");
  File.WriteAllText(config,"{}");
  Check(!CombatBackend.Native(path),module+" existing configurations without a new key select restored human combat");
  File.WriteAllText(config,"[]");
  Check(!CombatBackend.Native(path),module+" malformed settings cannot reactivate the failed native trial");
 }
}
finally
{
 var parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
 if(temporary.Parent?.FullName.TrimEnd(Path.DirectorySeparatorChar)==parent&&temporary.Name.StartsWith("cs2-native-backend-"))temporary.Delete(true);
}
Console.WriteLine($"{count} backend checks passed");
