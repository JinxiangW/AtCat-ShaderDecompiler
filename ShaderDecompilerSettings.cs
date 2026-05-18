namespace Ruri.ShaderTools;

// Persistent user-configurable settings for the shader decompiler
// surface. Read/written by the host UI layer (FModel hook tab,
// RipperHookGUI settings menu) via Ruri.Hook.Config.HookModuleSettings
// keyed on `ModuleKey` below. Adding a new field here is non-breaking:
// `HookModuleSettings.Get<T>` round-trips unknown JSON nodes so older
// settings files load forward and newer files load back without losing
// data on round-trip.
public sealed class ShaderDecompilerSettings
{
    /// <summary>
    /// Module key string the host UI uses when calling
    /// `HookModuleSettings.Get<ShaderDecompilerSettings>(ModuleKey)`.
    /// </summary>
    public const string ModuleKey = "ShaderDecompiler";

    /// <summary>
    /// When true (default), multi-variant stages emit per-variant
    /// `<stem>/<variantKey>.hlsl` files and the .shader file uses
    /// `#include` distributors per `#if defined(KEYWORD)` branch.
    /// When false, every variant body stays inline inside the .shader
    /// file under its `#if defined` block. Single-variant stages always
    /// inline regardless — distribution is only useful when there's
    /// actually a chain to slim down.
    ///
    /// Default is true: multi-variant Unity shaders (e.g. URP scene
    /// shaders) routinely have 30+ vertex variants × 30+ fragment
    /// variants per pass and multiple passes; inlining produces
    /// .shader files of 200K+ lines that block Unity's importer for
    /// minutes. Splitting keeps each file workable while preserving
    /// the same logical structure.
    /// </summary>
    public bool SplitVariantsToHlslFiles { get; set; } = true;

    /// <summary>
    /// When true (default), the FModel-side decompile hook pops a
    /// warning before the first export of an `.ushaderbytecode`
    /// archive when no `.usmap` mappings are loaded — the resulting
    /// `.shader` files lose all author-facing parameter names. Set
    /// false to skip the prompt entirely (e.g. for headless batch
    /// export setups that never load mappings on purpose).
    /// </summary>
    public bool WarnIfNoMappings { get; set; } = true;

    /// <summary>
    /// When true (default), if no engine-UB metadata folder matches the
    /// game's exact EGame name (e.g. GAME_InfinityNikki), the loader falls
    /// back to the base UE folder (e.g. GAME_UE5_4). Most games (~99%)
    /// don't customize CB layouts, so this fallback is virtually always
    /// correct and significantly reduces the amount of manual seeding
    /// required.
    /// </summary>
    public bool TryMatchBaseEngineVersion { get; set; } = true;
}
