using BotState;
int count=0;void Check(bool value,string name){if(!value)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
var policy=new BurstPosturePolicy();int draws=0;double Draw(){draws++;return .1;}
Check(Enumerable.Range(0,30).All(i=>policy.OnShot(1,42,"ak",i*.1,.5,Draw)),"crouch held through 30-shot burst");
Check(draws==1,"one decision for a full burst");
Check(policy.Update(1,42,3,true)==PostureUpdate.Crouch,"held briefly after last shot");
Check(policy.Update(1,42,3.5,true)==PostureUpdate.Release,"release after pause");
Check(policy.Update(1,42,3.6,true)==PostureUpdate.Unowned,"release is not repeated");
Check(!policy.OnShot(1,42,"ak",4,.5,()=>.9),"standing decision supported");
Check(policy.Update(1,42,4.1,true)==PostureUpdate.Stand,"standing stable");
Check(policy.Update(1,43,4.2,true)==PostureUpdate.Unowned,"new pawn not touched");
policy.OnShot(1,43,"ak",5,.5,Draw);Check(policy.OnShot(1,43,"m4",5.1,.5,Draw),"weapon change creates fresh decision");
Check(draws==3,"one draw per new context");
Check(policy.Update(1,43,5.2,false)==PostureUpdate.Release,"leaving combat releases");
policy.OnShot(1,43,"ak",6,.5,Draw);policy.Clear();Check(policy.Update(1,43,6.1,true)==PostureUpdate.Unowned,"round reset clears ownership");
Console.WriteLine($"{count} posture checks passed");
