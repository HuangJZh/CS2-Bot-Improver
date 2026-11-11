using InventorySimulator;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

Check(LocalOnlyPolicy.AllowsLaunch("cs2", ["cs2.exe", "-insecure"]), "insecure CS2 allowed");
Check(!LocalOnlyPolicy.AllowsLaunch("cs2", ["cs2.exe"]), "normal launch rejected");
Check(!LocalOnlyPolicy.AllowsLaunch("cs2", ["-insecure", "-dedicated"]), "dedicated server rejected");
Check(!LocalOnlyPolicy.AllowsLaunch("other", ["-insecure"]), "other process rejected");
Check(!LocalOnlyPolicy.AllowsLaunch("cs2", ["+echo -insecure"]), "partial argument rejected");
Check(LocalOnlyPolicy.IsLoopback("loopback"), "engine loopback accepted");
Check(LocalOnlyPolicy.IsLoopback("loopback:0"), "engine loopback port accepted");
Check(LocalOnlyPolicy.IsLoopback("127.0.0.1:27005"), "IPv4 loopback accepted");
Check(LocalOnlyPolicy.IsLoopback("[::1]:27005"), "IPv6 loopback accepted");
Check(!LocalOnlyPolicy.IsLoopback("203.0.113.10:27015"), "internet address rejected");
Check(!LocalOnlyPolicy.IsLoopback("192.168.1.20:27015"), "LAN remote address rejected");
Check(!LocalOnlyPolicy.IsLoopback("127.0.0.1.example.com"), "loopback prefix spoof rejected");
Check(!LocalOnlyPolicy.IsLoopback(null), "missing address rejected");
Check(LocalOnlyPolicy.AllowsPlayer(true, true, 123, 123, false, "loopback"), "configured local human accepted");
Check(!LocalOnlyPolicy.AllowsPlayer(false, true, 123, 123, false, "loopback"), "secure launch cannot apply");
Check(!LocalOnlyPolicy.AllowsPlayer(true, false, 123, 123, false, "loopback"), "ended local map cannot apply");
Check(!LocalOnlyPolicy.AllowsPlayer(true, true, 123, 456, false, "loopback"), "different account rejected");
Check(!LocalOnlyPolicy.AllowsPlayer(true, true, 123, 123, true, "loopback"), "bots left to BotRandomizer");
Check(!LocalOnlyPolicy.AllowsPlayer(true, true, 123, 123, false, "203.0.113.10:27015"), "configured account on remote connection rejected");
Check(!LocalOnlyPolicy.AllowsPlayer(true, true, 0, 0, false, "loopback"), "empty account rejected");
Console.WriteLine($"{passed} policy checks passed.");
