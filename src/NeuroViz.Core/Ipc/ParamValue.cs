using MessagePack;

namespace NeuroViz.Core.Ipc;

[MessagePackObject]
public sealed class ParamValue
{
    [Key(0)] public float Value { get; init; }
    [Key(1)] public double SetAtSeconds { get; init; }
    [Key(2)] public int DecayMs { get; init; }

    public float EvaluateAt(double nowSeconds)
    {
        if (DecayMs <= 0) return Value;
        var age = nowSeconds - SetAtSeconds;
        if (age < 0) return Value;
        var k = (float)Math.Exp(-age / (DecayMs / 1000.0));
        return Value * k;
    }
}