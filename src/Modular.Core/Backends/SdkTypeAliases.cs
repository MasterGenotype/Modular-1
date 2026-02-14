// Type aliases for SDK types to maintain backward compatibility
// The SDK (Modular.Sdk) contains the canonical definitions, and this file
// re-exports them under the Modular.Core.Backends namespace

// Backend types
global using BackendCapabilities = Modular.Sdk.Backends.BackendCapabilities;
global using DownloadOptions = Modular.Sdk.Backends.DownloadOptions;
global using DownloadProgress = Modular.Sdk.Backends.DownloadProgress;
global using DownloadPhase = Modular.Sdk.Backends.DownloadPhase;

// Common types
global using BackendMod = Modular.Sdk.Backends.Common.BackendMod;
global using BackendModFile = Modular.Sdk.Backends.Common.BackendModFile;
global using FileFilter = Modular.Sdk.Backends.Common.FileFilter;
