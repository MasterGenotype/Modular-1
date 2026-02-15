// Re-export SDK type for backward compatibility.
// This file maintains API compatibility while the actual interface is defined in Modular.Sdk.
//
// This shim allows code in Modular.Core.Backends namespace to implement IModBackend
// without needing to reference Modular.Sdk.Backends directly. Backends like
// NexusModsBackend and GameBananaBackend implement this interface.
//
// Note: This shim is kept intentionally for backward compatibility. See also
// SdkTypeAliases.cs which provides global type aliases for other SDK types.

namespace Modular.Core.Backends;

/// <summary>
/// Backward-compatible interface that extends the SDK's IModBackend.
/// Implementations in Modular.Core should implement this interface.
/// </summary>
public interface IModBackend : Modular.Sdk.Backends.IModBackend
{
}
