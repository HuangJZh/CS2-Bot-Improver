using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Vec=System.Numerics.Vector3;
using BotMovement;

namespace BotState;
public partial class BotState
{
    private sealed class JumpApproach
    {
        public uint Pawn;
        public Vec Goal;
        public float GroundAt,FlightAt=-1,LastTick;
    }
    private readonly Dictionary<int,JumpApproach> _jumpApproaches=new();
    private readonly NativeMoveGoalReader _jumpMoveGoal=new();
    private void AssistNativeJump(CCSPlayerController p,CCSPlayerPawn pawn,CCSBot bot,int idx,bool nearLadder,bool combat)
    {
        float now=Server.CurrentTime;bool grounded=(pawn.Flags&1)!=0;
        // Do not steer falling, combat, scoped, ladder, replay or possessed bots.
        string weapon=pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName??"";
        if(combat||bot.IsEnemyVisible||pawn.IsDefusing||pawn.IsScoped||nearLadder||pawn.AbsOrigin is not {} position
            ||weapon is "weapon_negev" or "weapon_m249" or "weapon_c4" or "weapon_flashbang" or "weapon_molotov"
            ||weapon.Contains("grenade",StringComparison.Ordinal)
            ||(_botController!=null&&BotControllerBridge.IsViewControlled(_botController,p.Slot)))
        {_jumpApproaches.Remove(idx);return;}
        var feet=new Vec(position.X,position.Y,position.Z);
        if(!_jumpMoveGoal.TryRead(bot,out var goal)){_jumpApproaches.Remove(idx);return;}
        if(grounded)
        {
            if(!JumpTraversalPolicy.NearRise(feet,goal,bot.PathIndex>0)){_jumpApproaches.Remove(idx);return;}
            if(!_jumpApproaches.TryGetValue(idx,out var armed)||armed.Pawn!=pawn.EntityHandle.Raw||Vec.DistanceSquared(armed.Goal,goal)>32*32)
                _jumpApproaches[idx]=armed=new();
            armed.Pawn=pawn.EntityHandle.Raw;armed.Goal=goal;
            armed.GroundAt=armed.LastTick=now;armed.FlightAt=-1;
            return;
        }
        if(!_jumpApproaches.TryGetValue(idx,out var state)||state.Pawn!=pawn.EntityHandle.Raw)return;
        if(state.FlightAt<0)
        {
            float jumpAge=now-bot.JumpTimestamp;
            if(now-state.GroundAt>.20f||jumpAge<0||jumpAge>.25f||pawn.AbsVelocity.Z<80)
            {_jumpApproaches.Remove(idx);return;}
            state.FlightAt=now;
        }
        if(now-state.FlightAt>.8f||Vec.DistanceSquared(goal,state.Goal)>64*64
            ||pawn.AbsVelocity.Z< -80&&feet.Z<state.Goal.Z-16)
        {_jumpApproaches.Remove(idx);return;}
        float dt=Math.Clamp(now-state.LastTick,0,.05f);state.LastTick=now;
        var velocity=JumpTraversalPolicy.AirStep(new(feet.X,feet.Y),new(pawn.AbsVelocity.X,pawn.AbsVelocity.Y),new(state.Goal.X,state.Goal.Y),dt);
        pawn.AbsVelocity.X=velocity.X;pawn.AbsVelocity.Y=velocity.Y;
    }
}
