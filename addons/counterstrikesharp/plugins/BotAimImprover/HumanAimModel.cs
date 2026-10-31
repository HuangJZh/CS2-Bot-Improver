using System.Numerics;
namespace BotAimImprover;

// Gameplay tuning, not measured player ranks. Angles are degrees, times seconds.
public sealed record HumanAimProfile(float Reaction, float SensorDelay, float SampleInterval,
    float FlickTime, float MotorError, float AmplitudeError, float TrackingTime, float MaxSpeed, float Prediction)
{
    public float PerceptionError {get;init;}=.07f;
    // How much the fire decision trusts the actual crosshair position versus the
    // intended motor plan. Humans see where their crosshair actually is, but can
    // also fire during a planned movement; more skilled profiles rely more on the
    // real crosshair. Calibration hypothesis, not measured rank data.
    public float FeedbackWeight {get;init;}=.50f;
    // Recoil-control model: humans start pulling down after a few rounds and do not
    // compensate perfectly. These scale the observed view-vs-plan climb during a
    // burst into an aim correction; they are gameplay hypotheses to calibrate.
    public float RecoilControlGain {get;init;}=.50f;
    public int RecoilControlStartShot {get;init;}=3;
    // Burst discipline and spray shape, derived from the Aurora vs Falcons pro
    // demos: median rifle burst six rounds, human spray yaw sweeps left then right
    // rather than holding a laser-straight line. Values are calibration hypotheses.
    public int BurstLength {get;init;}=6;
    public float BurstPause {get;init;}=.28f;
    public float SpraySwayGain {get;init;}=1f;
    public static HumanAimProfile For(string level) => level switch
    {
        "Low" => new(.200f,.090f,.050f,.155f,.32f,.020f,.105f,480,.60f){PerceptionError=.12f,FeedbackWeight=.35f,RecoilControlGain=.30f,RecoilControlStartShot=3,BurstLength=4,BurstPause=.32f,SpraySwayGain=1.3f},
        "High" => new(.130f,.045f,.025f,.095f,.07f,.006f,.045f,850,.92f){PerceptionError=.035f,FeedbackWeight=.65f,RecoilControlGain=.70f,RecoilControlStartShot=2,BurstLength=8,BurstPause=.24f,SpraySwayGain=.7f},
        _ => new(.170f,.065f,.035f,.125f,.16f,.011f,.070f,650,.80f)
    };
    // Mean relative view drift (degrees) by shot index during a pro rifle spray,
    // measured from 639 bursts >=2 rounds across five maps from two pro series
    // (Aurora vs Falcons BO3, Vitality vs MOUZ BO3). Burst median 5 rounds.
    public static readonly float[] ProSprayPitch={0f,.21f,.98f,2.10f,3.53f,5.06f,6.15f,7.00f,7.57f,8.38f,8.57f,9.02f,9.04f,8.64f,7.86f,7.78f,7.79f,8.11f,8.36f,8.52f,9.75f};
    public static readonly float[] ProSprayYaw={0f,.14f,.13f,.03f,-.06f,-.51f,-1.04f,-1.62f,-1.58f,-.57f,1.80f,2.40f,3.40f,3.59f,3.55f,5.78f,6.19f,6.39f,6.41f,6.44f,6.18f};
}
public readonly record struct AimObservation(float Time, Vector3 Position,float Radius=8,float Uncertainty=0);
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
    private readonly VisualLocalization _visual;
    private readonly Queue<AimObservation> _pending=new();
    private AimObservation? _latest,_previous;
    private float _nextSample=float.NegativeInfinity,_lastVisible=float.NegativeInfinity;
    private float _lastStep,_start,_duration,_nextBias,_nextCorrection;
    private bool _planned,_correcting;
    private Vector2 _view,_startView,_endView,_bias,_intendedView,_intendedEndView;
    private Vector2 _lastDesired,_desiredRate;
    private bool _hasDesired;
    private Vector3 _velocity;
    public float LastVisible => _lastVisible;
    public bool HasObservation => _latest.HasValue || _pending.Count>0;
    // Contact geometry of the current plan: how far the crosshair had to travel to
    // the first believed target direction. Small gaps model a pre-aimed/holding
    // contact, large gaps an unexpected contact with a startle penalty.
    public float ContactGapDegrees {get;private set;}=float.NaN;
    public string ContactKind {get;private set; }="none";
    public float PlanStartedAt => _start;
    public float PlanDuration => _duration;
    // Latest believed target bearing (before any motor bias); used by the execution
    // feedback layer to push the target spot toward where the model wants to look.
    public Vector2 LastDesiredAngles => _lastDesired;
    public FireDecision LastFireDecision {get;private set;}=new(false,"no-observation",null,null);
    public HumanAimModel(HumanAimProfile profile,Vector2 initial,float now,int seed)
    { Profile=profile;_view=_intendedView=initial;_lastStep=now;_random=new(seed);_visual=new(profile.PerceptionError,seed^0x5143A9); }
    private float Symmetric() => (float)(_random.NextDouble()+_random.NextDouble()-1);
    private Vector2 Noise(float magnitude) => new(Symmetric()*magnitude,Symmetric()*magnitude);
    public bool NeedsObservation(float now) => now>=_nextSample;
    public VisualEstimate? ObserveVisual(float now,Vector3 eye,Vector3 visiblePoint,Vector2 actualView,float radius,float extraUncertainty=0)
    {
        if(!NeedsObservation(now))return null;
        var estimate=_visual.Estimate(now,eye,visiblePoint,actualView,radius);
        Observe(now,estimate.Position,radius,estimate.UncertaintyDegrees+MathF.Max(0,extraUncertainty));
        return estimate;
    }
    public void Observe(float now,Vector3 point,float radius=8,float uncertainty=0)
    {
        if(now<_nextSample || !float.IsFinite(now+point.X+point.Y+point.Z+radius+uncertainty) || radius<=0 || uncertainty<0)return;
        _nextSample=now+Profile.SampleInterval;_lastVisible=now;
        _pending.Enqueue(new(now,point,radius,uncertainty));
        while(_pending.Count>32)_pending.Dequeue();
    }
    public AimOutput Step(float now,Vector3 eye,bool visible,HumanFireContext context=default,Vector2? actualView=null)
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
        if(_latest is not {} latest){LastFireDecision=new(false,"no-observation",null,null);return new(_view,false,"perception",float.PositiveInfinity);}
        float age=now-latest.Time;
        if(!visible && now-_lastVisible>.20f){LastFireDecision=new(false,"memory-expired",null,null);return new(_view,false,"memory-expired",age);}
        // Aim at the believed future position: delayed observation age plus an
        // approximate projectile flight time. Humans learn to lead moving targets;
        // without this the planned view systematically lags the target.
        float targetDistance=Vector3.Distance(eye,latest.Position);
        float flightTime=Math.Clamp(targetDistance/MathF.Max(1,context.BulletSpeed),0,.35f);
        float leadSeconds=Math.Clamp(age+flightTime,0,.45f);
        var predicted=latest.Position+_velocity*leadSeconds*Profile.Prediction;
        var desired=AimMath.Angles(eye,predicted);
        float? actualError=actualView is {} observedView && float.IsFinite(observedView.X+observedView.Y)
            ? AimMath.Delta(desired,observedView).Length() : null;
        // Track how fast the desired bearing itself is moving. A motor controller
        // without this term lags a constant-rate target by roughly rate*TrackingTime,
        // which would make moving targets effectively un-fireable at range.
        Vector2 desiredRate=Vector2.Zero;
        if(_hasDesired && dt>0)
        {
            var rawRate=AimMath.Delta(desired,_lastDesired)/dt;
            if(float.IsFinite(rawRate.X+rawRate.Y))
                desiredRate=new(Math.Clamp(rawRate.X,-400,400),Math.Clamp(rawRate.Y,-400,400));
        }
        _desiredRate=Vector2.Lerp(_desiredRate,desiredRate,.35f);
        _lastDesired=desired;_hasDesired=true;
        var controlTarget=AimMath.Normalize(desired+_desiredRate*Profile.TrackingTime);
        float angularRadius=MathF.Max(.015f,MathF.Atan2(latest.Radius,MathF.Max(1,targetDistance))*180/MathF.PI);
        if(!_planned)
        {
            _planned=true;
            float amplitude=AimMath.Delta(desired,_view).Length();
            // Prepared contact: the crosshair was already close to the believed
            // direction. Unexpected contact: larger travel plus a short startle
            // penalty before the flick starts. Both are modeling hypotheses to
            // calibrate, not measured human constants.
            float reactionScale=amplitude<10f ? .80f : amplitude>45f ? 1.15f : 1f;
            float startle=amplitude>45f ? .04f : 0f;
            _start=latest.Time+Profile.Reaction*(1+Symmetric()*.12f)*reactionScale+startle;
            ContactGapDegrees=amplitude;
            ContactKind=amplitude<10f ? "prepared" : amplitude>45f ? "unexpected" : "neutral";
            _startView=_view;
            float angularWidth=MathF.Max(.12f,MathF.Atan2(8,targetDistance)*180/MathF.PI);
            _duration=amplitude<angularWidth*.5f ? .015f : Profile.FlickTime+.018f*MathF.Log2(1+amplitude/angularWidth);
            // Endpoint error persists for this movement instead of per-frame random jitter.
            _bias=Noise(Profile.MotorError+amplitude*Profile.AmplitudeError);
            _endView=AimMath.Normalize(desired+_bias);
            _intendedEndView=desired;
            _nextBias=_start+_duration+.25f;
        }
        if(now<_start){LastFireDecision=new(false,"reaction",null,null);return new(_view,false,"reaction",age);}
        if(now<_start+_duration)
        {
            // Early smooth regulation uses delayed observations; it never snaps to the live target.
            if(now-_start>.08f)
            {
                _endView=AimMath.Advance(_endView,AimMath.Normalize(controlTarget+_bias),dt,.18f,60);
                _intendedEndView=AimMath.Advance(_intendedEndView,controlTarget,dt,.18f,60);
            }
            var planned=_startView+AimMath.Delta(_endView,_startView)*AimMath.Ease((now-_start)/_duration);
            var intended=_startView+AimMath.Delta(_intendedEndView,_startView)*AimMath.Ease((now-_start)/_duration);
            _view=AimMath.Advance(_view,planned,dt,.012f,Profile.MaxSpeed);
            _intendedView=AimMath.Advance(_intendedView,intended,dt,.012f,Profile.MaxSpeed);
            LastFireDecision=HumanFireDecision.Evaluate(_intendedView,desired,angularRadius,latest.Uncertainty,visible,age,false,context,Profile.MotorError,actualError,Profile.FeedbackWeight);
            return new(_view,LastFireDecision.Allow,"flick",age);
        }
        if(now>=_nextBias)
        {
            _bias=Vector2.Lerp(_bias,Noise(Profile.MotorError),.35f);
            _nextBias=now+.22f+(float)_random.NextDouble()*.18f;
        }
        var target=AimMath.Normalize(controlTarget+_bias);
        float error=AimMath.Delta(target,_view).Length();
        // Large missed flicks receive a delayed second movement, not an automatic pause on every shot.
        if(!_correcting && error>2.5f) { _correcting=true;_nextCorrection=now+Profile.SensorDelay; }
        bool waiting=_correcting && now<_nextCorrection;
        if(!waiting)
        {
            _view=AimMath.Advance(_view,target,dt,Profile.TrackingTime,Profile.MaxSpeed);
            _intendedView=AimMath.Advance(_intendedView,controlTarget,dt,Profile.TrackingTime,Profile.MaxSpeed);
        }
        if(error<1)_correcting=false;
        LastFireDecision=HumanFireDecision.Evaluate(_intendedView,desired,angularRadius,latest.Uncertainty,visible,age,waiting,context,Profile.MotorError,actualError,Profile.FeedbackWeight);
        return new(_view,LastFireDecision.Allow,waiting ? "correction" : "tracking",age);
    }
}
