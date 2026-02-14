// Re-export SDK type for backward compatibility
// This file maintains API compatibility while the actual interface is defined in Modular.Sdk

namespace Modular.Core.Backends;

// Type alias: Modular.Core.Backends.IModBackend now refers to Modular.Sdk.Backends.IModBackend
public interface IModBackend : Modular.Sdk.Backends.IModBackend
{
}
