using Ruri.ShaderTools.Spirv.Patcher.Helpers;

namespace Ruri.ShaderTools.Spirv.Patcher.Patch;

// Pass 020 (patch): encode the caller-supplied (id → name) and (typeId, memberIndex → name)
// overrides into ready-to-splice OpName / OpMemberName instruction word arrays. No module
// mutation yet — this just produces a list, Pass030 inserts it at the right offset.
internal static class Pass020_BuildNewInstructions
{
    public static void DoPass(PatchPipelineState state)
    {
        var instructions = new List<uint[]>(state.Names.Count + state.MemberNames.Count);
        foreach ((uint id, string name) in state.Names)
        {
            instructions.Add(DebugInstructionFactory.CreateOpName(id, name));
        }
        foreach ((uint typeId, uint memberIndex, string name) in state.MemberNames)
        {
            instructions.Add(DebugInstructionFactory.CreateOpMemberName(typeId, memberIndex, name));
        }
        state.NewInstructions = instructions;
    }
}
