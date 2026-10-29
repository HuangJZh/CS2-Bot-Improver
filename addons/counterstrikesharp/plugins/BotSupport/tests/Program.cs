using BotSupport;
var count=0;
void Check(bool ok,string label) { if(!ok) throw new Exception(label); count++; Console.WriteLine("PASS "+label); }
foreach(var pair in new[] { ("Low",75),("Medium",85),("High",95) })
    Check(Enumerable.Range(0,100).Count(r => SupportPolicy.StopSucceeds(pair.Item1,r))==pair.Item2,pair.Item1+" exact chance out of 100");
Check(SupportPolicy.IsWeaponRequest("#Chatwheel_requestweapon"),"installed wheel token");
Check(SupportPolicy.IsWeaponRequest("CW.NeedWeapon"),"CW token");
Check(!SupportPolicy.IsWeaponRequest("#Chatwheel_followme"),"other radio commands untouched");
var rich = new[]{new Donor(1,10000,200),new Donor(2,12000,300)};
Check(SupportPolicy.Select(rich,2,"auto") is { } a && a.Donor.Slot==2 && a.Gun.Item=="weapon_awp","one richest donor, prefer AWP");
Check(SupportPolicy.Select([new(1,5000,100)],2,"auto")?.Gun.Item=="weapon_ak47","fallback to AK when AWP unaffordable");
Check(SupportPolicy.Select([new(1,5000,100)],3,"auto")?.Gun.Item=="weapon_m4a1_silencer","CT fallback to M4A1-S");
Check(SupportPolicy.Select([new(1,3500,100)],2,"auto") != null,"exact reserve threshold accepted");
Check(SupportPolicy.Select([new(1,3499,100)],2,"auto") == null,"reserve protected");
Check(SupportPolicy.Select([new(1,16000,1501)],2,"auto") == null,"distant donor rejected");
Check(SupportPolicy.Select(rich,3,"ak") == null,"wrong team weapon rejected");
Check(SupportPolicy.Select(rich,3,"m4a4")?.Gun is { Item:"weapon_m4a1", Price:2900 },"M4A4 item mapping and price");
Check(SupportPolicy.Select(rich,3,"m4a1")?.Gun.Item=="weapon_m4a1_silencer","M4A1-S item mapping");
Check(SupportPolicy.Select(rich,2,"awm")?.Gun.Item=="weapon_awp","AWM alias");
Check(SupportPolicy.Select([],2,"auto") == null,"no donor no weapon");
Check(SupportPolicy.Select([new(1,10000,400),new(2,10000,200)],2,"auto")?.Donor.Slot==2,"equal funds choose nearer donor");
Check(SupportPolicy.Select([new(1,10000,200),new(2,10000,200)],2,"auto")?.Donor.Slot==1,"equal funds and distance deterministic single donor");
Console.WriteLine($"{count} checks passed");
