using System.Collections.Generic;

namespace Ruri.ShaderTools.Unreal
{
    public class ShaderBundle
    {
        public byte[] NativeCode { get; set; }
        public ShaderArchitecture Architecture { get; set; }
        
        // Unified symbol metadata
        public ShaderSymbolMetadata Symbols { get; set; } = new();
        
        // Raw engine metadata for reference
        public object? EngineMetadata { get; set; } 
    }
}
