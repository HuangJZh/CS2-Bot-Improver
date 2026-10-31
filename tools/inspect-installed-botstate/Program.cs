using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
var root=args[0];Assembly.LoadFrom(Path.Combine(root,"shared/BotControllerApi/BotControllerApi.dll"));
var assembly=Assembly.LoadFrom(Path.Combine(root,"plugins/BotState/BotState.dll"));
var type=assembly.GetType("BotState.BotState")!;var method=type.GetMethod("OnWeaponFire",BindingFlags.Instance|BindingFlags.NonPublic)!;
var codes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>unchecked((ushort)o.Value));
var bytes=method.GetMethodBody()!.GetILAsByteArray()!;var calls=new List<object>();int pos=0;
while(pos<bytes.Length)
{
 int start=pos;ushort key=bytes[pos++];if(key==0xfe)key=(ushort)(0xfe00|bytes[pos++]);var op=codes[key];
 if(op.OperandType==OperandType.InlineMethod){int token=BitConverter.ToInt32(bytes,pos);var member=method.Module.ResolveMethod(token);calls.Add(new{offset=start,opcode=op.Name,member=member!.DeclaringType!.FullName+"."+member.Name});}
 pos+=op.OperandType switch {OperandType.InlineNone=>0,OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,OperandType.InlineVar=>2,OperandType.InlineI8 or OperandType.InlineR=>8,OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(bytes,pos),_=>4};
}
Console.WriteLine(JsonSerializer.Serialize(calls,new JsonSerializerOptions{WriteIndented=true}));
