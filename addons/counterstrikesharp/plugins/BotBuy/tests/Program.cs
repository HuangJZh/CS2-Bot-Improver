using BotBuyPatch;
int n=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);n++;}
foreach(var gun in new[]{"weapon_negev","weapon_xm1014","weapon_nova","weapon_mac10","weapon_sg556","weapon_scar20","weapon_g3sg1","weapon_ak47","weapon_m4a1","weapon_m4a1_silencer","weapon_awp"}){
 Check(PurchasePolicy.HasPrimary(new[]{"weapon_knife","weapon_usp_silencer",gun}),gun+" detected while knife/pistol is active");
 Check(!PurchasePolicy.CanBuy(gun,new[]{gun}),gun+" same-primary rebuy blocked");
 Check(!PurchasePolicy.CanBuy("weapon_ak47",new[]{gun}),gun+" carried primary prevents another primary buy");
 Check(!PurchasePolicy.CanSwap(gun,"weapon_m4a1",new[]{gun}),gun+" carried primary cannot be refunded/replaced");
}
Check(!PurchasePolicy.HasPrimary(new[]{"weapon_knife","weapon_deagle","weapon_taser","weapon_smokegrenade"}),"equipment is not a primary");
Check(PurchasePolicy.CanBuy("weapon_ak47",new[]{"weapon_glock"}),"unarmed T may buy AK");
Check(PurchasePolicy.CanBuy("weapon_m4a1_silencer",Array.Empty<string>()),"unarmed CT may buy M4");
Check(!PurchasePolicy.CanBuy("weapon_aug",Array.Empty<string>()),"AUG removed");
Check(!PurchasePolicy.CanBuy("weapon_sg556",Array.Empty<string>()),"SG553 removed");
Check(!PurchasePolicy.CanSwap("weapon_negev","weapon_negev",Array.Empty<string>()),"same gun swap is never attempted");
Check(PurchasePolicy.CanSwap("weapon_xm1014","weapon_negev",Array.Empty<string>()),"new budget shotgun variant remains available");
Check(PurchasePolicy.CanBuy("item_assaultsuit",new[]{"weapon_negev"}),"carried-primary protection permits armor");
Console.WriteLine($"{n} purchase checks passed");
