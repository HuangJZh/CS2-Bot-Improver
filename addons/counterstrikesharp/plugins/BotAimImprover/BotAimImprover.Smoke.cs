using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vec=System.Numerics.Vector3;
namespace BotAimImprover;
public partial class BotAimImprover
{
    private sealed record SmokeGuess(uint Enemy,Vec Position,float Until);
    private readonly Dictionary<nint,SmokeGuess> _smokeGuesses=new();
    private readonly List<(Vec Center,float Radius)> _smokes=new();
    private float _smokeRefresh;
    private void ClearSmokeError() { _smokeGuesses.Clear(); _smokes.Clear(); _smokeRefresh=0; }
    private bool ApplySmokeError(CCSBot bot,CCSPlayerPawn enemy,Vector eye)
    {
        if (enemy.AbsOrigin is not { } origin) return false;
        float now=Server.CurrentTime;
        if (now>=_smokeRefresh)
        {
            _smokeRefresh=now+.1f; _smokes.Clear();
            foreach (var smoke in Utilities.FindAllEntitiesByDesignerName<CSmokeGrenadeProjectile>("smokegrenade_projectile"))
            {
                if (!smoke.IsValid || !smoke.DidSmokeEffect || smoke.SmokeEffectTickBegin<=0) continue;
                float radius=SmokeAimPolicy.Radius((Server.TickCount-smoke.SmokeEffectTickBegin)*Server.TickInterval);
                var c=smoke.SmokeDetonationPos;
                if (radius>0) _smokes.Add((new(c.X,c.Y,c.Z),radius));
            }
        }
        var start=new Vec(eye.X,eye.Y,eye.Z);
        var body=new Vec(origin.X,origin.Y,origin.Z+(enemy.ViewOffset?.Z ?? 64)*.67f);
        if (!_smokes.Any(s=>SmokeAimPolicy.Blocks(start,body,s.Center,s.Radius)))
        { _smokeGuesses.Remove(bot.Handle); return false; }
        if (!_smokeGuesses.TryGetValue(bot.Handle,out var guess) || guess.Enemy!=enemy.EntityHandle.Raw || now>=guess.Until)
        {
            float angle=Random.Shared.NextSingle()*MathF.Tau;
            float error=SmokeAimPolicy.Error(Vec.Distance(start,body));
            // Freeze an inaccurate body-level guess: no accurate head selection or per-tick tracking.
            var position=body+new Vec(MathF.Cos(angle)*error,MathF.Sin(angle)*error,Random.Shared.NextSingle()*24-12);
            guess=new(enemy.EntityHandle.Raw,position,now+.4f);
            _smokeGuesses[bot.Handle]=guess;
        }
        var spot=bot.TargetSpot; spot.X=guess.Position.X;spot.Y=guess.Position.Y;spot.Z=guess.Position.Z;
        var predicted=bot.TargetSpotPredicted;predicted.X=spot.X;predicted.Y=spot.Y;predicted.Z=spot.Z;
        var velocity=bot.TargetSpotVelocity;velocity.X=0;velocity.Y=0;velocity.Z=0;
        return true;
    }
}
