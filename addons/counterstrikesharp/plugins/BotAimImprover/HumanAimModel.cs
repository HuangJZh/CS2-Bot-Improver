using System.Numerics;
namespace BotAimImprover;

// Gameplay tuning, not measured player ranks. Angles are degrees, times seconds.
public sealed record HumanAimProfile(float Reaction, float SensorDelay, float SampleInterval,
    float FlickTime, float MotorError, float AmplitudeError, float TrackingTime, float MaxSpeed, float Prediction)
{
    public static HumanAimProfile For(string level) => level switch
    {
        "Low" => new(.200f,.090f,.050f,.155f,.32f,.020f,.105f,480,.60f),
        "High" => new(.130f,.045f,.025f,.095f,.07f,.006f,.045f,850,.92f),
        _ => new(.170f,.065f,.035f,.125f,.16f,.011f,.070f,650,.80f)
    };
}
public readonly record struct AimObservation(float Time, Vector3 Position);
public readonly record struct AimOutput(Vector2 Angles, bool AllowFire, string Phase, float ObservationAge);

public static class AimMath
{
    public static float Wrap(float x) { float n=(x+180)%360; return (n<0 ? n+360 : n)-180; }
    public static Vector2 Delta(Vector2 to,Vector2 from) => new(Wrap(to.X-from.X),Wrap(to.Y-from.Y));
    public static Vector2 Normalize(Vector2 a) => new(Math.Clamp(a.X,-89,89),Wrap(a.Y));
    public static Vector2 Angles(Vector3 eye,Vector3 point)
    {
        var d=point-eye;return new(-MathF.Atan2(d.Z,MathF.Sqrt(d.X*d.X+d.Y*d.Y))*180/MathF.PI,MathF.Atan2(d.Y,d.X)*180/MathF.PI);
    }
    public static Vector3 Ray(Vector3 eye,Vector2 angles,float distance=1000)
    {
        float p=angles.X*MathF.PI/180,y=angles.Y*MathF.PI/180;
        return eye+new Vector3(MathF.Cos(p)*MathF.Cos(y),MathF.Cos(p)*MathF.Sin(y),-MathF.Sin(p))*distance;
    }
    public static float Ease(float t) { t=Math.Clamp(t,0,1);return t*t*t*(10+t*(-15+6*t)); }
    public static Vector2 Advance(Vector2 current,Vector2 target,float dt,float tau,float speed)
    {
        var d=Delta(target,current)*(1-MathF.Exp(-Math.Clamp(dt,0,.05f)/tau));
        float max=speed*Math.Clamp(dt,0,.05f);if(d.Length()>max)d=Vector2.Normalize(d)*max;
        return Normalize(current+d);
    }
}

// Receives observations only: no entity pointers or access to live enemy coordinates.
public sealed class HumanAimModel
{
    public HumanAimProfile Profile {get;}
    private readonly Random _random;
    private readonly Queue<AimObservation> _pending=new();
    private AimObservation? _latest,_previous;
    private float _nextSample=float.NegativeInfinity,_lastVisible=float.NegativeInfinity;
    private float _lastStep,_start,_ready,_duration,_nextBias,_nextCorrection;
    private bool _planned,_correcting;
    private Vector2 _view,_startView,_endView,_bias;
    private Vector3 _velocity;
    public float LastVisible => _lastVisible;
    public bool HasObservation => _latest.HasValue || _pending.Count>0;
    public HumanAimModel(HumanAimProfile profile,Vector2 initial,float now,int seed)
    { Profile=profile;_view=initial;_lastStep=now;_random=new(seed); }
    private float Symmetric() => (float)(_random.NextDouble()+_random.NextDouble()-1);
    private Vector2 Noise(float magnitude) => new(Symmetric()*magnitude,Symmetric()*magnitude);
    public bool NeedsObservation(float now) => now>=_nextSample;
    public void Observe(float now,Vector3 point)
    {
        if(now<_nextSample || !float.IsFinite(point.X+point.Y+point.Z))return;
        _nextSample=now+Profile.SampleInterval;_lastVisible=now;
        _pending.Enqueue(new(now,point));
        while(_pending.Count>32)_pending.Dequeue();
    }
    public AimOutput Step(float now,Vector3 eye,bool visible)
    {
        float dt=Math.Clamp(now-_lastStep,0,.05f);_lastStep=now;
        while(_pending.TryPeek(out var obs) && obs.Time<=now-Profile.SensorDelay)
        {
            _pending.Dequeue();_previous=_latest;_latest=obs;
            if(_previous is {} prev && obs.Time>prev.Time)
            {
                var estimate=(obs.Position-prev.Position)/(obs.Time-prev.Time);
                // Do not acquire teleports as extreme target velocity.
                if(estimate.Length()>350)estimate=Vector3.Normalize(estimate)*350;
                _velocity=Vector3.Lerp(_velocity,estimate,.65f);
            }
        }
        if(_latest is not {} latest)return new(_view,false,"perception",float.PositiveInfinity);
        float age=now-latest.Time;
        if(!visible && now-_lastVisible>.20f)return new(_view,false,"memory-expired",age);
        var predicted=latest.Position+_velocity*Math.Clamp(age,0,.14f)*Profile.Prediction;
        var desired=AimMath.Angles(eye,predicted);
        if(!_planned)
        {
            _planned=true;_start=latest.Time+Profile.Reaction*(1+Symmetric()*.12f);
            _startView=_view;
            float amplitude=AimMath.Delta(desired,_view).Length();
            float angularWidth=MathF.Max(.12f,MathF.Atan2(8,Vector3.Distance(eye,latest.Position))*180/MathF.PI);
            _duration=amplitude<angularWidth*.5f ? .015f : Profile.FlickTime+.018f*MathF.Log2(1+amplitude/angularWidth);
            // Endpoint error persists for this movement instead of per-frame random jitter.
            _bias=Noise(Profile.MotorError+amplitude*Profile.AmplitudeError);
            _endView=AimMath.Normalize(desired+_bias);
            _ready=_start+_duration*.88f;_nextBias=_start+_duration+.25f;
        }
        if(now<_start)return new(_view,false,"reaction",age);
        if(now<_start+_duration)
        {
            // Early smooth regulation uses delayed observations; it never snaps to the live target.
            if(now-_start>.08f)
                _endView=AimMath.Advance(_endView,AimMath.Normalize(desired+_bias),dt,.18f,60);
            var planned=_startView+AimMath.Delta(_endView,_startView)*AimMath.Ease((now-_start)/_duration);
            _view=AimMath.Advance(_view,planned,dt,.012f,Profile.MaxSpeed);
            return new(_view,visible && age<.20f && now>=_ready,"flick",age);
        }
        if(now>=_nextBias)
        {
            _bias=Vector2.Lerp(_bias,Noise(Profile.MotorError),.35f);
            _nextBias=now+.22f+(float)_random.NextDouble()*.18f;
        }
        var target=AimMath.Normalize(desired+_bias);
        float error=AimMath.Delta(target,_view).Length();
        // Large missed flicks receive a delayed second movement, not an automatic pause on every shot.
        if(!_correcting && error>2.5f) { _correcting=true;_nextCorrection=now+Profile.SensorDelay; }
        bool waiting=_correcting && now<_nextCorrection;
        if(!waiting)_view=AimMath.Advance(_view,target,dt,Profile.TrackingTime,Profile.MaxSpeed);
        if(error<1)_correcting=false;
        bool allow=visible && age<.20f && now>=_ready && (!waiting || error<8);
        // Hidden information expires; hold view after a short memory window.
        if(!visible && now-_lastVisible>.20f)allow=false;
        return new(_view,allow,waiting ? "correction" : "tracking",age);
    }
}
