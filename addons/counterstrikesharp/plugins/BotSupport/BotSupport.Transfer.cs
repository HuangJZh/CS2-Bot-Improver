using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace BotSupport;
public sealed partial class BotSupport
{
    private bool TransferBestPrimary(CCSPlayerController caller,CCSPlayerController[] bots)
    {
        var candidates=new List<HeldPrimary>();
        foreach(var bot in bots)
        {
            if(bot.PlayerPawn.Value?.WeaponServices is not {} services)continue;
            foreach(var h in services.MyWeapons)
            {
                if(h.Value is not {IsValid:true} weapon)continue;
                var data=weapon.As<CCSWeaponBase>().VData;
                if(data?.GearSlot==gear_slot_t.GEAR_SLOT_RIFLE)candidates.Add(new(bot.Slot,h.Raw,data.Price,Distance(bot,caller)));
            }
        }
        var best=SupportPolicy.SelectHeld(candidates);
        if(best==null)return false;
        var donor=bots.First(b=>b.Slot==best.Value.Slot);
        var pawn=donor.PlayerPawn.Value;var recipient=caller.PlayerPawn.Value;
        var gun=pawn?.WeaponServices?.MyWeapons.FirstOrDefault(h=>h.Raw==best.Value.Weapon)?.Value;
        if(pawn?.ItemServices==null||recipient?.AbsOrigin is not {} pos||gun is not {IsValid:true})return false;
        uint raw=gun.EntityHandle.Raw;string name=gun.DesignerName;
        // Drop the original entity: ammo, wear, paint and stickers remain intact.
        // No new weapon is created and no money is deducted for a transfer.
        try
        {
            pawn.ItemServices.As<CCSPlayer_ItemServices>().DropActivePlayerWeapon(gun);
            if(!gun.IsValid||pawn.WeaponServices!.MyWeapons.Any(h=>h.Raw==raw))return false;
            gun.Teleport(new Vector(pos.X,pos.Y,pos.Z+20),null,new Vector());
        }
        catch(Exception e){Logger.LogWarning(e,"Held-primary transfer failed; original weapon was not cloned or charged");return false;}
        _requests[caller.Slot]=Server.CurrentTime;
        Logger.LogInformation("Transferred held primary slot={Donor} to slot={Recipient} item={Item} value={Price}",donor.Slot,caller.Slot,name,best.Value.Price);
        Reply(caller,$"{donor.PlayerName} 资金不足，已把现有的 {name.Replace("weapon_","")} 丢给你。");
        return true;
    }
    private void RegisterTransferFixture()
    {
        var args=Environment.GetCommandLineArgs();int ip=Array.IndexOf(args,"-ip"),port=Array.IndexOf(args,"-port");
        if(!args.Contains("-bot-fix-fixture")||!args.Contains("-dedicated")||ip<0||port<0
            ||args.ElementAtOrDefault(ip+1)!="127.0.0.1"||args.ElementAtOrDefault(port+1)!="27025")return;
        AddCommand("bot_support_fixture_transfer","Owned loopback fixture only",(caller,command)=>
        {
            if(caller!=null||command.ArgCount<2||!int.TryParse(command.GetArg(1),out int slot))return;
            var players=Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").Where(p=>p.IsValid&&p.IsBot&&p.PawnIsAlive).ToArray();
            var recipient=players.FirstOrDefault(p=>p.Slot==slot);if(recipient==null||HasPrimary(recipient))return;
            bool success=TransferBestPrimary(recipient,players.Where(p=>p.Slot!=slot&&p.TeamNum==recipient.TeamNum).ToArray());
            Server.PrintToConsole($"[SupportFixture] transfer={success}");
        });
    }
}
