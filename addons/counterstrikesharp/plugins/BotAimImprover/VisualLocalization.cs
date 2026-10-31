using System.Numerics;
namespace BotAimImprover;

public readonly record struct VisualEstimate(Vector3 Position,float UncertaintyDegrees,float EccentricityDegrees,float AngularRadiusDegrees);

// Gameplay hypothesis, not a fitted psychophysical/rank model. Receives only
// an independently visible sample; never queries entities, sound or hidden state.
public sealed class VisualLocalization
{
    private readonly Random _random;
    private readonly float _baseError;
    private bool _started;
    private float _last,_nextBias;
    private Vector2 _bias,_goalBias;
    public VisualLocalization(float baseError,int seed)
    {
        if(!float.IsFinite(baseError) || baseError<0)throw new ArgumentOutOfRangeException(nameof(baseError));
        _baseError=baseError;_random=new(seed);
    }
    private float Symmetric()=>(float)(_random.NextDouble()+_random.NextDouble()-1);
    private Vector2 Noise()=>new(Symmetric(),Symmetric());
    public VisualEstimate Estimate(float now,Vector3 eye,Vector3 visiblePoint,Vector2 actualView,float radius)
    {
        if(!float.IsFinite(now) || !float.IsFinite(eye.X+eye.Y+eye.Z+visiblePoint.X+visiblePoint.Y+visiblePoint.Z+actualView.X+actualView.Y)
            || !float.IsFinite(radius) || radius<=0)throw new ArgumentException("Invalid visual sample");
        if(_started && now<_last)throw new ArgumentException("Visual sample time moved backwards");
        float distance=Vector3.Distance(eye,visiblePoint);
        var direction=AimMath.Angles(eye,visiblePoint);
        float eccentricity=AimMath.Delta(direction,actualView).Length();
        // Radius is an approximate part-size proxy, not measured visible pixels.
        float angularRadius=MathF.Max(.03f,MathF.Atan2(radius,MathF.Max(1,distance))*180/MathF.PI);
        float sizePenalty=Math.Clamp(MathF.Sqrt(.5f/angularRadius),.65f,3f);
        float peripheralPenalty=1+1.5f*MathF.Pow(Math.Clamp(eccentricity/55,0,1),1.4f);
        float uncertainty=Math.Clamp(_baseError*sizePenalty*peripheralPenalty,0,1.5f);
        if(!_started){_started=true;_last=now;_bias=_goalBias=Noise();_nextBias=now+.18f;}
        if(now>=_nextBias){_goalBias=Noise();_nextBias=now+.14f+(float)_random.NextDouble()*.1f;}
        _bias=Vector2.Lerp(_bias,_goalBias,1-MathF.Exp(-Math.Clamp(now-_last,0,.1f)/.18f));
        _last=now;
        var estimate=AimMath.Ray(eye,AimMath.Normalize(direction+_bias*uncertainty),distance);
        return new(estimate,uncertainty,eccentricity,angularRadius);
    }
}
