namespace Ruri.ShaderTools;

public enum ShaderArchitecture
{
    Unknown,
    Dxbc,
    Dxil,
    SpirV
}

public enum ShaderParamType
{
    Float = 0,
    Int = 1,
    Bool = 2,
    Half = 3,
    Short = 4,
    UInt = 5,
}
