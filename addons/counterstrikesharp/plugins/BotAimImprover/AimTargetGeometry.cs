namespace BotAimImprover;

// One synchronous sampling pass reads the target transform once. This is not a
// cross-frame perception cache; callers still sample at their original cadence.
internal readonly struct AimTargetGeometry(float x,float y,float z,float eyeZ,float yawDegrees)
{
    private readonly float _x=x,_y=y,_z=z,_eyeZ=eyeZ;
    private readonly float _rightX=(float)Math.Sin(yawDegrees*Math.PI/180.0);
    private readonly float _rightY=(float)-Math.Cos(yawDegrees*Math.PI/180.0);
    public bool TryPoint(float fraction,float lateral,bool feet,out float x,out float y,out float z)
    {
        x=feet?_x:_x+_rightX*lateral;
        y=feet?_y:_y+_rightY*lateral;
        z=feet?_z+fraction:_z+_eyeZ*fraction;
        return float.IsFinite(x)&&float.IsFinite(y)&&float.IsFinite(z);
    }
}
