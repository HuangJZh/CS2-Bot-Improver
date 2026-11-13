using BotCombatPolicy;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Vec=System.Numerics.Vector3;
using Angles=System.Numerics.Vector2;

namespace BotAimImprover;
public partial class BotAimImprover
{
    private const string RetakeServerHash="84d596644ad574ac70f427777c89f9006a8023f493c46f4c215f40bceef13cbb";
    private const string RetakeReloadSignature="48 89 5C 24 10 48 89 6C 24 18 56 57 41 56 48 81 EC 30 01 00 00 48 8B D9 33 F6 E8 ? ? ? ? 48 8B 0D ? ? ? ? E8 ? ? ? ? 84 C0 75 0F";
    private MemoryFunctionVoid<nint>? _reloadRetakeBrain;
    private MemoryFunctionVoid<nint,bool>? _retakeEquipBest;
    private ConVar? _retakeTreeVar;
    private QAngle? _retakeViewAngles;
    private string? _retakePreviousTree;
    private readonly Dictionary<int,uint> _retakePawns=new();
    private readonly Dictionary<int,(uint Pawn,uint Target,float Next,bool Visible)> _retakeSight=new();
    private static readonly int[] RetakeAttentionParts=[0,17,3,8,9,4];
    private static readonly int[] RetakeCoverParts=[6,7,10,11,12,13,14,15,16,18,19,1,2];
    private readonly Dictionary<int,float> _retakeCoverAt=new();
    private readonly Dictionary<int,int> _retakeCoverCursor=new();
    private bool RetakeOwnsGoals=>_retakePreviousTree!=null;
    private bool IsRetakeBot(CCSPlayerController p)=>_retakePawns.TryGetValue(p.Slot,out var pawn)&&pawn==p.PlayerPawn.Raw;

    private void InitializeRetakeAi()
    {
        if(OperatingSystem.IsWindows())
        {
            var server=Path.GetFullPath(Path.Combine(ModuleDirectory,"../../../../bin/win64/server.dll"));
            try
            {
                if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(server))).Equals(RetakeServerHash,StringComparison.OrdinalIgnoreCase))
                {
                    _reloadRetakeBrain=new(RetakeReloadSignature);
                    if(_reloadRetakeBrain.Handle==nint.Zero)throw new InvalidOperationException("Retake AI initializer signature missing");
                    var dataPath=Path.GetFullPath(Path.Combine(ModuleDirectory,"../../../../addons/BotController/gamedata.json"));
                    using var data=JsonDocument.Parse(File.ReadAllText(dataPath));
                    _retakeEquipBest=new(data.RootElement.GetProperty("CCSBot::EquipBestWeapon").GetProperty("signatures").GetProperty("windows").GetString()!);
                    if(_retakeEquipBest.Handle==nint.Zero){_retakeEquipBest=null;Logger.LogWarning("Retake empty-hands recovery unavailable: EquipBestWeapon signature missing.");}
                }
                else Logger.LogWarning("Retake bridge disabled: server build needs revalidation.");
            }
            catch(Exception e){_reloadRetakeBrain=null;Logger.LogWarning(e,"Retake bridge initialization unavailable");}
        }
        _retakeTreeVar=ConVar.Find("mp_bot_ai_bt");
        _retakeViewAngles=new QAngle();
        RegisterEventHandler<EventBombPlanted>((e,i)=>{Server.NextFrame(BeginRetakeAi);return HookResult.Continue;},HookMode.Post);
        // Never destroy a tree from an event that its own action_use may emit.
        RegisterEventHandler<EventRoundEnd>((e,i)=>{Server.NextFrame(()=>EndRetakeAi(true));return HookResult.Continue;});
        RegisterEventHandler<EventBombDefused>((e,i)=>{Server.NextFrame(()=>EndRetakeAi(true));return HookResult.Continue;});
        RegisterEventHandler<EventBombExploded>((e,i)=>{Server.NextFrame(()=>EndRetakeAi(true));return HookResult.Continue;});
        RegisterListener<Listeners.OnMapEnd>(()=>EndRetakeAi(false));
        RegisterEventHandler<EventPlayerSpawn>((e,i)=>
        {
            if(RetakeOwnsGoals&&e.Userid is {IsValid:true,IsBot:true} p)
            {uint pawn=p.PlayerPawn.Raw;Server.NextFrame(()=>{if(RetakeOwnsGoals&&p.IsValid&&p.PlayerPawn.Raw==pawn)AttachRetakeBot(p);});}
            return HookResult.Continue;
        });
        AddCommand("bot_retake_ai","Local console: on/off/status",(p,c)=>
        {
            if(!IsLocalAimConsole(p))return;
            string mode=c.ArgCount>1?c.GetArg(1):"status";
            if(mode=="off"){_humanSettings.RetakeAI=false;EndRetakeAi(true);}
            if(mode=="on"){_humanSettings.RetakeAI=true;Server.NextFrame(BeginRetakeAi);}
            Server.PrintToConsole($"[RetakeAI] enabled={_humanSettings.RetakeAI} available={_reloadRetakeBrain?.Handle!=nint.Zero&&_reloadRetakeBrain!=null} active={RetakeOwnsGoals} bots={_retakePawns.Count} tree={_retakeTreeVar?.StringValue}");
        });
    }
    private void BeginRetakeAi()
    {
        if(RetakeOwnsGoals||_navigationRoundEnded||!HumanActive||_humanSettings.NativeCombat||_reloadRetakeBrain==null||_setEyeAngles==null||_retakeTreeVar==null)return;
        ReadObjective(Server.CurrentTime);
        int type=ConVar.Find("game_type")?.GetPrimitiveValue<int>()??-1,mode=ConVar.Find("game_mode")?.GetPrimitiveValue<int>()??-1;
        if(!RetakeAiContract.Eligible(_humanSettings.RetakeAI,type,mode,_bombPlanted,_roundLive))return;
        string previous=_retakeTreeVar.StringValue;
        if(!string.IsNullOrEmpty(previous))return; // Respect native Retakes, Rush and other tree owners.
        _retakePreviousTree=previous;
        // A mode handoff is not a new enemy contact. Keep reaction progress,
        // observations, burst history and elite assignment already in flight.
        ResetAllSilentMovement();ResetCoverPeeks();ResetTeamSupport();ResetTeamNavigation();
        _retakeTreeVar.SetValue(RetakeAiContract.Tree);
        try
        {
            foreach(var p in EntityPlayers())AttachRetakeBot(p);
            if(_retakePawns.Count==0){EndRetakeAi(true);return;}
            Logger.LogInformation("RetakeAI attached actual Valve objective subtrees to {Bots} bots; human combat bridge retained.",_retakePawns.Count);
        }
        catch(Exception e){Logger.LogError(e,"RetakeAI activation failed; restoring legacy brains");EndRetakeAi(true);}
    }
    private void AttachRetakeBot(CCSPlayerController p)
    {
        if(p is not {IsValid:true,IsBot:true,PawnIsAlive:true}||p.HasBeenControlledByPlayerThisRound
            ||p.PlayerPawn.Value?.Bot is not {} bot||IsRetakeBot(p))return;
        // This no-argument native initializer was verified in the pinned server:
        // it resets AI state and creates the tree at +0x5d8; it does not respawn the pawn.
        _retakePawns[p.Slot]=p.PlayerPawn.Raw;
        if(_humanStates.TryGetValue(p.Slot,out var state)&&state.Pawn==p.PlayerPawn.Raw)
            _retakeSight[p.Slot]=(p.PlayerPawn.Raw,state.Enemy,0,false);
        _pendingLargeTurns.Remove(bot.Handle);_turnAssistActive.Remove(bot.Handle);_nativeTurnAt.Remove(bot.Handle);
        _reloadRetakeBrain!.Invoke(bot.Handle);
        if(Marshal.ReadIntPtr(bot.Handle+0x5d8)==nint.Zero)throw new InvalidOperationException("Native behavior tree did not load");
    }
    private void EndRetakeAi(bool resetBrains)
    {
        if(_retakePreviousTree==null)return;
        string previous=_retakePreviousTree;_retakePreviousTree=null;
        ClearRetakeFireRecovery();
        if(_retakeTreeVar?.StringValue==RetakeAiContract.Tree)_retakeTreeVar.SetValue(previous);
        var owned=_retakePawns.ToArray();_retakePawns.Clear();_retakeSight.Clear();_retakeCoverAt.Clear();_retakeCoverCursor.Clear();
        foreach(var (slot,pawn) in owned)
        {
            ReleaseHuman(slot);ReleaseFireGuard(slot);
            var p=EntityPlayerAtSlot(slot);
            if(resetBrains&&p is {IsValid:true,IsBot:true,PawnIsAlive:true}&&!p.HasBeenControlledByPlayerThisRound
                &&p.PlayerPawn.Raw==pawn&&p.PlayerPawn.Value?.Bot is {} b)_reloadRetakeBrain?.Invoke(b.Handle);
        }
    }
    private bool UpdateRetakeSight(CCSPlayerController p,CCSPlayerPawn pawn,CCSBot bot,Vector eye,float now)
    {
        _retakeSight.TryGetValue(p.Slot,out var sight);
        if(sight.Pawn!=p.PlayerPawn.Raw||now>=sight.Next)
        {
            uint target=uint.MaxValue;
            if(pawn.BlindUntilTime<=now)
            {
                var from=new Vec(eye.X,eye.Y,eye.Z);var view=new Angles(pawn.EyeAngles.X,pawn.EyeAngles.Y);
                // Keep a current visible target before scanning alternatives. No
                // hidden enemy position is ever supplied as a visual observation.
                var players=EntityPlayers();
                for(int pass=0;pass<2&&target==uint.MaxValue;pass++)foreach(var candidate in players)
                {
                    if(candidate is not {IsValid:true,PawnIsAlive:true}||candidate.TeamNum==p.TeamNum||candidate.TeamNum is not (2 or 3))continue;
                    if((candidate.PlayerPawn.Raw==sight.Target)!=(pass==0)||candidate.PlayerPawn.Value is not {IsValid:true} enemy
                        ||!TryReadTargetGeometry(enemy,out var geometry))continue;
                    foreach(int part in RetakeAttentionParts)
                    {
                        if(!TryComputePartPos(geometry,part,out var x,out var y,out var z))continue;
                        var point=new Vec(x,y,z);var d=AimMath.Delta(AimMath.Angles(from,point),view);
                        if(MathF.Abs(d.Y)>_humanSettings.HorizontalFieldOfView*.5f||MathF.Abs(d.X)>50
                            ||HumanSmokeBlocks(eye,point)||!PointVisibleFromEye(eye,x,y,z))continue;
                        target=candidate.PlayerPawn.Raw;break;
                    }
                    if(target!=uint.MaxValue)break;
                }
                // A leg/arm-only exposure must not become a permanent fire lock.
                // Budget one extended candidate per 100ms, rotating fairly, rather
                // than tracing every point on every opponent on every tick.
                if(target==uint.MaxValue&&now>=_retakeCoverAt.GetValueOrDefault(p.Slot)&&players.Length>0)
                {
                    _retakeCoverAt[p.Slot]=now+.10f;
                    int first=_retakeCoverCursor.GetValueOrDefault(p.Slot)%players.Length;
                    for(int j=0;j<players.Length;j++)
                    {
                        int index=(first+j)%players.Length;var candidate=players[index];
                        if(candidate is not {IsValid:true,PawnIsAlive:true}||candidate.TeamNum==p.TeamNum||candidate.TeamNum is not (2 or 3)
                            ||candidate.PlayerPawn.Value is not {IsValid:true} enemy||!TryReadTargetGeometry(enemy,out var geometry)
                            ||!TryComputePartPos(geometry,3,out var cx,out var cy,out var cz))continue;
                        var gap=AimMath.Delta(AimMath.Angles(from,new(cx,cy,cz)),view);
                        if(MathF.Abs(gap.Y)>_humanSettings.HorizontalFieldOfView*.5f+10||MathF.Abs(gap.X)>60)continue;
                        _retakeCoverCursor[p.Slot]=(index+1)%players.Length;
                        foreach(int part in RetakeCoverParts)
                        {
                            if(!TryComputePartPos(geometry,part,out var x,out var y,out var z))continue;
                            var point=new Vec(x,y,z);var delta=AimMath.Delta(AimMath.Angles(from,point),view);
                            if(MathF.Abs(delta.Y)>_humanSettings.HorizontalFieldOfView*.5f||MathF.Abs(delta.X)>50
                                ||HumanSmokeBlocks(eye,point)||!PointVisibleFromEye(eye,x,y,z))continue;
                            target=candidate.PlayerPawn.Raw;break;
                        }
                        break;
                    }
                }
            }
            bool visible=target!=uint.MaxValue;
            // Retain identity through a brief occlusion, but never mark it visible
            // or feed live hidden coordinates into the model. This matches the
            // legacy enemy handle's continuity and avoids new reaction plans.
            bool previousAlive=false;
            foreach(var c in EntityPlayers())
                if(c.IsValid&&c.PawnIsAlive&&c.TeamNum!=p.TeamNum&&c.TeamNum is 2 or 3&&c.PlayerPawn.Raw==sight.Target)
                {previousAlive=true;break;}
            target=RetakeAiContract.RememberTarget(target,sight.Target,previousAlive);
            sight=(p.PlayerPawn.Raw,target,now+CombatProfile(p,now).SampleInterval,visible);
            _retakeSight[p.Slot]=sight;
        }
        bot.Enemy.Raw=sight.Target;bot.IsEnemyVisible=sight.Visible&&pawn.BlindUntilTime<=now;
        if(bot.IsEnemyVisible)ConfirmVisualAttention(p,sight.Target,now);
        return bot.IsEnemyVisible;
    }
    private void ApplyRetakeAim(nint pointer)
    {
        if(!RetakeOwnsGoals||_setEyeAngles==null||_retakeViewAngles==null||ResolveBotController(pointer) is not {IsValid:true,PawnIsAlive:true} p||!IsRetakeBot(p)
            ||p.HasBeenControlledByPlayerThisRound||OtherViewOwner(p.Slot)||p.PlayerPawn.Value is not {IsValid:true} pawn||pawn.IsDefusing
            ||!_humanStates.TryGetValue(p.Slot,out var state)||state.Pawn!=p.PlayerPawn.Raw||!state.LastSight||Server.CurrentTime-state.LastUsed>.05f)return;
        // Retakes action_aim uses a blackboard, not the legacy TargetSpot. Apply
        // the same model's first-shot view explicitly; native recoil owns a live spray.
        if(Server.CurrentTime-state.LastShotAt<.2f)return;
        var a=state.Output.Angles;if(!float.IsFinite(a.X+a.Y))return;
        _retakeViewAngles.X=a.X;_retakeViewAngles.Y=a.Y;_retakeViewAngles.Z=0;
        var flags=p.Flags;
        try{p.Flags=flags|0x100u;_setEyeAngles.Invoke(pawn.Handle,_retakeViewAngles.Handle);}
        finally{if(p.IsValid)p.Flags=flags;}
        if(pawn.Bot is {} bot){bot.LookPitch=a.X;bot.LookYaw=a.Y;}
        RefreshFireGuard(p);
    }
}
