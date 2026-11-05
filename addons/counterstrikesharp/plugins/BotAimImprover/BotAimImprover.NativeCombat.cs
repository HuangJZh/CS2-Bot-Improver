using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Vec=System.Numerics.Vector3;
namespace BotAimImprover;

public partial class BotAimImprover
{
    private sealed class NativeContact
    {
        public uint Pawn,Target,Weapon;
        public float First,Last,ReadyAt,SampleAt;
        public string Point="none";
    }
    private readonly Dictionary<int,NativeContact> _nativeContacts=new();
    private void ApplyTeammatePassthrough()=>ConVar.Find("mp_solid_teammates")?.SetValue(0);
    private NativeContact? SampleNativeContact(CCSPlayerController p,CCSPlayerPawn pawn,CCSBot bot,Vector eye,out Vec point)
    {
        point=default;float now=Server.CurrentTime;
        if(!bot.IsEnemyVisible||pawn.BlindUntilTime>now||bot.Enemy.Value is not {IsValid:true,LifeState:0} enemy||enemy.TeamNum==p.TeamNum)return null;
        uint weapon=pawn.WeaponServices?.ActiveWeapon.Raw??uint.MaxValue;
        _nativeContacts.TryGetValue(p.Slot,out var contact);
        bool same=contact!=null&&contact.Pawn==p.PlayerPawn.Raw&&contact.Target==bot.Enemy.Raw&&contact.Weapon==weapon&&now-contact.Last<.3f;
        bool visible=TrySelectHumanPoint(p,bot,pawn,enemy,eye,out point,out _,out var result,out _,out _,out _,out var smoke);
        if(!visible&&!smoke)return null;
        if(!same)contact=new(){Pawn=p.PlayerPawn.Raw,Target=bot.Enemy.Raw,Weapon=weapon,First=now};
        var profile=CombatProfile(p,now);
        contact!.Last=now;contact.ReadyAt=contact.First+profile.Reaction;contact.SampleAt=now;contact.Point=smoke&&!visible?"smoke":result;
        _nativeContacts[p.Slot]=contact;return contact;
    }
    private void ObserveNativeCombat(nint pointer)
    {
        var p=ResolveBotController(pointer);
        if(p is not {IsValid:true,IsBot:true,PawnIsAlive:true}||p.HasBeenControlledByPlayerThisRound
            ||p.PlayerPawn.Value is not {IsValid:true} pawn||pawn.Bot is not {} bot||!IsGun(pawn)||OtherViewOwner(p.Slot)||!TryGetBotEyePosition(p,out var eye))return;
        if(!_nativeContacts.TryGetValue(p.Slot,out var contact)||Server.CurrentTime-contact.SampleAt>=.02f||contact.Target!=bot.Enemy.Raw)
            SampleNativeContact(p,pawn,bot,eye,out _);
    }
    private void ApplyNativeAimPoint(CCSBot bot,CCSPlayerController p,CCSPlayerPawn enemy,Vector eye)
    {
        if(p is not {IsValid:true,IsBot:true,PawnIsAlive:true}||p.HasBeenControlledByPlayerThisRound||OtherViewOwner(p.Slot)
            ||p.PlayerPawn.Value is not {IsValid:true} pawn||!IsGun(pawn))return;
        var contact=SampleNativeContact(p,pawn,bot,eye,out var point);
        if(contact==null)return;
        if(contact.Point=="smoke"){ApplySmokeError(bot,enemy,eye);return;}
        // Preserve native motion prediction; translate its target by the selected
        // visible part offset, instead of replacing velocity with zero or driving view.
        var current=bot.TargetSpot;var predicted=bot.TargetSpotPredicted;
        var offset=point-new Vec(current.X,current.Y,current.Z);
        predicted.X+=offset.X;predicted.Y+=offset.Y;predicted.Z+=offset.Z;
        current.X=point.X;current.Y=point.Y;current.Z=point.Z;
        WriteTelemetry(new{kind="native_aim_point",time=Server.CurrentTime,slot=p.Slot,pawn=p.PlayerPawn.Raw,
            enemy=bot.Enemy.Raw,mode=_aimMode.ToString(),point=contact.Point,x=point.X,y=point.Y,z=point.Z});
    }
    private FireGateDecision EvaluateNativeCombatGate(CCSPlayerController p,CCSPlayerPawn pawn,CCSBot bot)
    {
        if(bot.Enemy.Value is not {IsValid:true,LifeState:0} enemy||enemy.TeamNum==p.TeamNum||!TryGetBotEyePosition(p,out var eye))
            return new(false,false,"native-no-target");
        if(enemy.AbsOrigin is {} origin)
        {
            var bearing=AimMath.Angles(new(eye.X,eye.Y,eye.Z),new(origin.X,origin.Y,origin.Z+enemy.ViewOffset.Z*.7f));
            if(FacingFirePolicy.OutsideCone(pawn.EyeAngles.Y,bearing.Y))return new(false,true,"target-outside-fire-cone");
        }
        if(!bot.IsEnemyVisible||pawn.BlindUntilTime>Server.CurrentTime)return new(false,false,"native-search");
        if(_nativeContacts.TryGetValue(p.Slot,out var contact)&&contact.Pawn==p.PlayerPawn.Raw&&contact.Target==bot.Enemy.Raw
            &&contact.Weapon==(pawn.WeaponServices?.ActiveWeapon.Raw??uint.MaxValue)&&Server.CurrentTime-contact.Last<.2f
            &&Server.CurrentTime<contact.ReadyAt)return new(false,true,"native-reaction");
        return new(true,false,"native-ready");
    }
}
