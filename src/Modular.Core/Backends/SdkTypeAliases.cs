// =============================================================================
// SDK Type Aliases for Backward Compatibility
// =============================================================================
//
// This file provides global type aliases that re-export types from Modular.Sdk
// under the Modular.Core.Backends namespace. This allows code in Modular.Core
// to reference these types without explicit Modular.Sdk imports.
//
// These aliases serve two purposes:
// 1. Backward compatibility - existing code using Modular.Core.Backends types
//    continues to work without modification
// 2. Abstraction - Core code doesn't need to know that types are defined in SDK
//
// Note: These aliases apply globally within the Modular.Core project.
// If you need to reference the SDK types explicitly, use the full namespace path.
//
// See also: IModBackend.cs which provides a similar shim for the IModBackend interface.
// =============================================================================

// Backend capability and progress types
global using BackendCapabilities = Modular.Sdk.Backends.BackendCapabilities;
global using DownloadOptions = Modular.Sdk.Backends.DownloadOptions;
global using DownloadProgress = Modular.Sdk.Backends.DownloadProgress;
global using DownloadPhase = Modular.Sdk.Backends.DownloadPhase;

// Common data transfer objects
global using BackendMod = Modular.Sdk.Backends.Common.BackendMod;
global using BackendModFile = Modular.Sdk.Backends.Common.BackendModFile;
global using FileFilter = Modular.Sdk.Backends.Common.FileFilter;
