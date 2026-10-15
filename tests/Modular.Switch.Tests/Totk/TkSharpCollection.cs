using Xunit;

namespace Modular.Switch.Tests.Totk;

/// <summary>
/// Tests that use TkSharp run one at a time: it keeps process-wide state
/// (<c>TkChangelogBuilder.Init</c>) and its pack-file lookup decompresses through one
/// static, non-thread-safe zstd decompressor, so building two ROM providers at once
/// corrupts it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TkSharpCollection
{
    public const string Name = "TkSharp";
}
