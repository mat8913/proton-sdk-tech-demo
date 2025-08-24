namespace Proton.Sdk.Drive;

public readonly record struct VolumeEventId(string Value)
{
    public string Value { get; } = Value;

    public override string ToString()
    {
        return Value;
    }
}
