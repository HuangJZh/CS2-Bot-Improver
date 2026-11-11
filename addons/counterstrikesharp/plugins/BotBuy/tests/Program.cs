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
Check(BotPurchasePolicy.TeamSniperPolicy.Limit(2)==1&&BotPurchasePolicy.TeamSniperPolicy.Limit(3)==2,"T one sniper CT two");
foreach(var gun in new[]{"weapon_awp","weapon_ssg08","weapon_scar20","weapon_g3sg1"})
 Check(BotPurchasePolicy.TeamSniperPolicy.IsSniper(gun),gun+" shares sniper quota");
Check(!BotPurchasePolicy.TeamSniperPolicy.IsSniper("weapon_aug"),"scoped rifle is not sniper");
var rich=Enumerable.Range(0,10).Select(i=>new BotPurchasePolicy.SniperBuyer((uint)i,i,i<5?2:3,16000,true,false,false)).ToArray();
var assigned=BotPurchasePolicy.TeamSniperPolicy.Assign(rich);
Check(assigned.Count(id=>id<5)==1&&assigned.Count(id=>id>=5)==2,"simultaneous rich buyers reserve three slots across teams");
var carried=rich.ToArray();carried[0]=carried[0] with{HasPrimary=true,HasSniper=true,Eligible=false};
assigned=BotPurchasePolicy.TeamSniperPolicy.Assign(carried);
Check(!assigned.Any(id=>id<5),"human saved AWP consumes all T quota");
carried[5]=carried[5] with{HasPrimary=true,HasSniper=true};
Check(BotPurchasePolicy.TeamSniperPolicy.Assign(carried).Count(id=>id>=5)==1,"CT saved Scout leaves one slot");
carried[6]=carried[6] with{HasPrimary=true,HasSniper=true};carried[7]=carried[7] with{HasPrimary=true,HasSniper=true};
Check(BotPurchasePolicy.TeamSniperPolicy.Assign(carried).Count==0,"pre-existing over-quota snipers prevent all new snipers");
Check(BotPurchasePolicy.TeamSniperPolicy.Assign(rich.Select(p=>p with{Money=4749}).ToArray()).Count==0,"no forced unaffordable AWP");
Check(BotPurchasePolicy.TeamSniperPolicy.Assign(rich.Select(p=>p with{HasPrimary=true}).ToArray()).Count==0,"existing rifles are not discarded to buy AWP");
Check(BotPurchasePolicy.TeamSniperPolicy.RifleAlias(2,16000)=="ak47","rich non-sniper T buys AK");
Check(BotPurchasePolicy.TeamSniperPolicy.RifleAlias(3,16000)=="m4a1_silencer"&&BotPurchasePolicy.TeamSniperPolicy.RifleAlias(3,16000,false)=="m4a1","rich CT supports both M4 variants");
Check(BotPurchasePolicy.TeamSniperPolicy.RifleAlias(2,2000)=="galilar"&&BotPurchasePolicy.TeamSniperPolicy.RifleAlias(3,2000)=="famas","lower budgets prefer affordable rifles");
Check(BotPurchasePolicy.TeamSniperPolicy.RifleAlias(2,1700)=="mac10"&&BotPurchasePolicy.TeamSniperPolicy.RifleAlias(3,1700)=="mp9","non-sniper eco cannot buy Scout instead");
Console.WriteLine($"{n} purchase checks passed");
