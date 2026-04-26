namespace Modular.Core.Installers.Detection;

public enum SupportedModGame
{
    Unknown,
    BaldursGate3,
    ChainedEchoes,
    Cyberpunk2077,
    FinalFantasyVIIRemake,
    FinalFantasyVIIRebirth,
    StardewValley,
    TheWitcher3
}

public enum ModInstallStore
{
    Unknown,
    Steam,
    Gog,
    Epic,
    Xbox,
    Manual
}

public enum HostOperatingSystem
{
    Unknown,
    Windows,
    Linux,
    MacOs
}

public enum TargetRuntime
{
    Unknown,
    Native,
    SteamProton,
    Wine
}

public enum CandidatePathKind
{
    Absolute,
    Tokenized,
    Relative
}

public enum PreExtractionValidation
{
    ValidExistingDestination,
    ValidMissingDestinationCanCreate,
    AmbiguousDestinationRequiresConfirmation,
    InvalidDestination,
    GameRootMissing,
    UnsupportedGame
}

public enum DetectedModType
{
    Unknown,
    CyberpunkRootStructuredMod,
    UnrealPakMod,
    SmapiModOrContentPack,
    BepInExMod,
    Witcher3ModFolder,
    Witcher3DlcContent
}

public sealed class ModInstallPathDetectionRequest
{
    public string Game { get; init; } = string.Empty;
    public ModInstallStore Store { get; init; } = ModInstallStore.Unknown;
    public HostOperatingSystem HostOs { get; init; } = HostOperatingSystem.Unknown;
    public TargetRuntime TargetRuntime { get; init; } = TargetRuntime.Unknown;
    public string? GameRoot { get; init; }
    public string? SteamLibrary { get; init; }
    public string? SteamAppId { get; init; }
    public string? ProtonPrefix { get; init; }
    public string? WinePrefix { get; init; }
    public string? WineUser { get; init; }
    public IReadOnlyList<string> ArchiveEntries { get; init; } = Array.Empty<string>();
    public ModInstallUserOverrides UserOverrides { get; init; } = new();
    public double ConfidenceThreshold { get; init; } = 0.75;
}

public sealed class ModInstallUserOverrides
{
    public string? GameDir { get; init; }
    public string? SteamLib { get; init; }
    public string? SteamAppId { get; init; }
    public string? SteamCompatData { get; init; }
    public string? ProtonPrefix { get; init; }
    public string? WinePrefix { get; init; }
    public string? WineUser { get; init; }
    public string? LocalAppData { get; init; }
    public string? AppData { get; init; }
    public string? UserProfile { get; init; }
    public IReadOnlyDictionary<string, string> CustomVariables { get; init; } = new Dictionary<string, string>();
    public string? ForcedDestination { get; init; }
}

public sealed class ModInstallPathDetectionResult
{
    public string Game { get; init; } = string.Empty;
    public string NormalizedGame { get; init; } = string.Empty;
    public string? CanonicalPath { get; init; }
    public IReadOnlyList<ModInstallPathCandidate> Candidates { get; init; } = Array.Empty<ModInstallPathCandidate>();
    public ArchiveInstallAnalysis ArchiveAnalysis { get; init; } = new();
    public ModInstallPathDecision Decision { get; init; } = new();
}

public sealed class ModInstallPathCandidate
{
    public string Path { get; init; } = string.Empty;
    public CandidatePathKind PathKind { get; init; }
    public double Confidence { get; init; }
    public bool Exists { get; init; }
    public bool CreateIfMissing { get; init; }
    public PreExtractionValidation Validation { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool Rejected { get; init; }
    public string? RejectionReason { get; init; }
    public bool IsCanonical { get; init; }
    public bool IsUserOverride { get; init; }
}

public sealed class ArchiveInstallAnalysis
{
    public IReadOnlyList<string> TopLevelEntries { get; init; } = Array.Empty<string>();
    public DetectedModType DetectedModType { get; init; } = DetectedModType.Unknown;
    public bool ExpectsRootExtraction { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
}

public sealed class ModInstallPathDecision
{
    public string? SelectedPath { get; init; }
    public double Confidence { get; init; }
    public string Explanation { get; init; } = string.Empty;
    public bool RequiresUserConfirmation { get; init; }
    public bool CanExtract { get; init; }
}

internal sealed class GameNameMatch
{
    public SupportedModGame Game { get; init; }
    public string NormalizedName { get; init; } = string.Empty;
    public bool IsAliasMatch { get; init; }
}

internal sealed class CandidateTemplate
{
    public string PathTemplate { get; init; } = string.Empty;
    public bool CreateIfMissing { get; init; }
    public bool IsCanonical { get; init; }
    public bool IsUserOverride { get; init; }
    public string Reason { get; init; } = string.Empty;
}

internal sealed class TokenExpansionResult
{
    public string Path { get; init; } = string.Empty;
    public CandidatePathKind PathKind { get; init; }
    public IReadOnlyList<string> UnresolvedTokens { get; init; } = Array.Empty<string>();
    public bool HasUnresolvedWindowsTokenOnLinux { get; init; }
}
