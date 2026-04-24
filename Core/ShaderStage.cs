namespace Ruri.ShaderTools;

public enum ShaderStage
{
    Unknown = 0,
    Vertex,
    Pixel,
    Compute,
    Geometry,
    TessellationControl,
    TessellationEvaluation,
    RayGeneration,
    RayClosestHit,
    RayMiss,
    RayAnyHit,
    RayIntersection,
    Callable,
    Task,
    Mesh,
}
