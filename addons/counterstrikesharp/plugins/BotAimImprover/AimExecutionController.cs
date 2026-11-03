using System.Numerics;
namespace BotAimImprover;

public readonly record struct ExecutionRequest(Vector2 Angles,Vector2 Correction,float ErrorDegrees,bool FeedbackActive);
public readonly record struct ExecutionPermission(bool Allow,string Reason,float ErrorDegrees,float ToleranceDegrees);
public static class FacingFirePolicy
{
    public static bool OutsideCone(float actualYaw,float targetYaw)
        =>float.IsFinite(actualYaw+targetYaw)&&MathF.Abs(AimMath.Wrap(targetYaw-actualYaw))>8;
}

// Bounded negative feedback around the requested (already imperfect) motor aim.
// It never reads enemy coordinates or writes the player's view directly.
public sealed class AimExecutionController
{
    private float _last=float.NegativeInfinity;
    private Vector2 _correction;
    private ExecutionRequest _request;
    private float? _waitStarted;
    public ExecutionRequest Update(float now,Vector2 requested,Vector2 actual,bool feedbackAllowed,float gain=.8f)
    {
        if(!float.IsFinite(now+requested.X+requested.Y+actual.X+actual.Y+gain))throw new ArgumentException("Invalid execution feedback");
        if(now==_last)return _request;
        if(now<_last)throw new ArgumentException("Execution clock moved backwards");
        float dt=float.IsFinite(_last)?Math.Clamp(now-_last,0,.05f):1f/64f;
        _last=now;
        var error=AimMath.Delta(requested,actual);
        var goal=feedbackAllowed ? new Vector2(Math.Clamp(error.X*gain,-.65f,.65f),Math.Clamp(error.Y*gain,-1f,1f)) : Vector2.Zero;
        _correction=Vector2.Lerp(_correction,goal,1-MathF.Exp(-dt/.07f));
        // Slew the additional correction, not the whole turn. Native recoil owns
        // a live spray; feedback decays smoothly instead of counteracting it.
        _request=new(AimMath.Normalize(requested+_correction),_correction,error.Length(),feedbackAllowed);
        return _request;
    }
    public ExecutionPermission FirstShot(float now,bool firstShot,float error,float tolerance,bool advance=true,float settleLimit=.24f,float horizontalError=0)
    {
        if(!float.IsFinite(now+error+tolerance+settleLimit+horizontalError)||error<0||tolerance<=0||settleLimit<=0)return new(false,"invalid-execution",error,tolerance);
        // A timeout is only a small-error fallback. A burst latch or a previous
        // shot must never license shooting sideways while the real view lags.
        if(MathF.Abs(horizontalError)>6 || firstShot&&error>8)
        {if(advance)_waitStarted=null;return new(false,"turn-not-ready",error,tolerance);}
        if(!firstShot){if(advance)_waitStarted=null;return new(true,"burst-continuation",error,tolerance);}
        if(error<=tolerance){if(advance)_waitStarted=null;return new(true,"execution-aligned",error,tolerance);}
        if(advance)_waitStarted??=now;
        // A bounded settling attempt cannot leave a bot mute when native motion
        // or recoil prevents convergence. This is a degraded shot, never alignment.
        return now-(_waitStarted??now)>=settleLimit
            ? new(true,"execution-settle-timeout",error,tolerance)
            : new(false,"waiting-for-execution",error,tolerance);
    }
    public void ResetReadiness()=>_waitStarted=null;
    public void Shot()=>ResetReadiness();
}
