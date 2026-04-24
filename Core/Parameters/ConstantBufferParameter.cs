namespace Ruri.ShaderTools;

public class ConstantBufferParameter
{
    public string ParamName = string.Empty;
    public ShaderParamType ParamType;
    public int Rows;
    public int Columns;
    public bool IsMatrix;
    public int ArraySize;
    public int ByteOffset;
}
