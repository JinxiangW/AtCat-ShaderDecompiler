namespace Ruri.ShaderTools.Spirv.Patcher.Patch;

// Mutable state for the symbol-injection sub-pipeline. PatchByIds delivers a list of
// `(id, name)` and `(typeId, memberIndex, name)` overrides; we walk the module to (1) drop
// any existing OpName / OpMemberName for those ids and (2) splice fresh ones in at the
// debug-section boundary.
internal sealed class PatchPipelineState
{
    public PatchPipelineState(
        byte[] inputSpirv,
        IReadOnlyList<(uint Id, string Name)> names,
        IReadOnlyList<(uint TypeId, uint MemberIndex, string Name)>? memberNames)
    {
        InputSpirv = inputSpirv;
        Names = names;
        MemberNames = memberNames ?? Array.Empty<(uint, uint, string)>();
    }

    public byte[] InputSpirv { get; }
    public IReadOnlyList<(uint Id, string Name)> Names { get; }
    public IReadOnlyList<(uint TypeId, uint MemberIndex, string Name)> MemberNames { get; }

    // Ids for which we'll *replace* an existing OpName / OpMemberName rather than append.
    // Required because dxil-spirv often emits machine debug names already; leaving them in
    // place lets spirv-cross prefer them and the metadata names lose the priority race.
    public HashSet<uint> ReplacedIds { get; set; } = new();
    public HashSet<(uint TypeId, uint MemberIndex)> ReplacedMembers { get; set; } = new();

    // Pass020 outputs: filtered original module (replaced names dropped) + the freshly-
    // encoded OpName / OpMemberName instructions to splice in.
    public uint[]? FilteredWords { get; set; }
    public List<uint[]> NewInstructions { get; set; } = new();

    // Final output — null until Pass030.
    public byte[]? OutputSpirv { get; set; }
}
