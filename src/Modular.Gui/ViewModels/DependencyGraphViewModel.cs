using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Dependencies;
using Modular.Core.Metadata;
using Modular.Core.Versioning;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the Dependency Graph view — visualizes mod dependency relationships
/// and runs the constraint solver.
/// </summary>
public partial class DependencyGraphViewModel : ViewModelBase
{
    private readonly IModVersionProvider? _versionProvider;

    [ObservableProperty]
    private ObservableCollection<DependencyNodeItem> _nodes = new();

    [ObservableProperty]
    private ObservableCollection<DependencyEdgeItem> _edges = new();

    [ObservableProperty]
    private ObservableCollection<string> _resolutionLog = new();

    [ObservableProperty]
    private string _statusMessage = "Add mods and resolve dependencies";

    [ObservableProperty]
    private bool _isResolving;

    [ObservableProperty]
    private string _newModId = string.Empty;

    [ObservableProperty]
    private string _newModConstraint = string.Empty;

    [ObservableProperty]
    private bool _includeOptional;

    [ObservableProperty]
    private bool _resolutionSuccess;

    [ObservableProperty]
    private ObservableCollection<string> _installOrder = new();

    [ObservableProperty]
    private ObservableCollection<string> _conflicts = new();

    [ObservableProperty]
    private ObservableCollection<string> _optionalFailures = new();

    private readonly List<(string canonicalId, VersionRange? constraint)> _requirements = new();

    // Designer constructor
    public DependencyGraphViewModel()
    {
        Nodes.Add(new DependencyNodeItem { Id = "ModA", Version = "1.0.0", IsRoot = true });
        Nodes.Add(new DependencyNodeItem { Id = "ModB", Version = "2.1.0" });
        Edges.Add(new DependencyEdgeItem { From = "ModA", To = "ModB", Type = "Required" });
    }

    // DI constructor
    public DependencyGraphViewModel(IModVersionProvider versionProvider)
    {
        _versionProvider = versionProvider;
    }

    [RelayCommand]
    private void AddRequirement()
    {
        if (string.IsNullOrWhiteSpace(NewModId)) return;

        VersionRange? constraint = null;
        if (!string.IsNullOrWhiteSpace(NewModConstraint))
        {
            if (!VersionRange.TryParse(NewModConstraint, out constraint))
            {
                StatusMessage = $"Invalid version constraint: {NewModConstraint}";
                return;
            }
        }

        _requirements.Add((NewModId, constraint));
        Nodes.Add(new DependencyNodeItem
        {
            Id = NewModId,
            ConstraintStr = constraint?.ToString() ?? "*",
            IsRoot = true
        });

        ResolutionLog.Add($"Added requirement: {NewModId} {constraint?.ToString() ?? "(any)"}");
        NewModId = string.Empty;
        NewModConstraint = string.Empty;
        StatusMessage = $"{_requirements.Count} root requirement(s)";
    }

    [RelayCommand]
    private void ClearRequirements()
    {
        _requirements.Clear();
        Nodes.Clear();
        Edges.Clear();
        InstallOrder.Clear();
        Conflicts.Clear();
        OptionalFailures.Clear();
        ResolutionLog.Clear();
        StatusMessage = "Cleared all requirements";
    }

    [RelayCommand]
    private async Task ResolveAsync()
    {
        if (_versionProvider == null)
        {
            StatusMessage = "Version provider not available";
            return;
        }

        if (_requirements.Count == 0)
        {
            StatusMessage = "Add at least one mod requirement first";
            return;
        }

        IsResolving = true;
        StatusMessage = "Resolving dependencies...";
        Edges.Clear();
        InstallOrder.Clear();
        Conflicts.Clear();
        OptionalFailures.Clear();

        try
        {
            var resolver = new BacktrackingDependencyResolver(_versionProvider);
            var result = await resolver.ResolveAsync(_requirements, IncludeOptional);

            ResolutionSuccess = result.Success;

            if (result.Success)
            {
                // Update nodes with resolved versions.
                var existingRoots = Nodes.Where(n => n.IsRoot).ToList();
                Nodes.Clear();

                foreach (var root in existingRoots)
                {
                    if (result.ResolvedVersions.TryGetValue(root.Id, out var ver))
                        root.Version = ver.ToString();
                    Nodes.Add(root);
                }

                foreach (var (modId, version) in result.ResolvedVersions)
                {
                    if (!existingRoots.Any(r => r.Id == modId))
                    {
                        Nodes.Add(new DependencyNodeItem
                        {
                            Id = modId,
                            Version = version.ToString()
                        });
                    }
                }

                // Add edges from graph.
                if (result.Graph != null)
                {
                    foreach (var edge in result.Graph.GetAllEdges())
                    {
                        Edges.Add(new DependencyEdgeItem
                        {
                            From = edge.From.CanonicalId,
                            To = edge.To.CanonicalId,
                            Type = edge.Type.ToString()
                        });
                    }
                }

                // Install order.
                if (result.InstallOrder != null)
                {
                    for (int i = 0; i < result.InstallOrder.Count; i++)
                    {
                        var node = result.InstallOrder[i];
                        InstallOrder.Add($"{i + 1}. {node.CanonicalId} @ {node.Version}");
                    }
                }

                // Optional failures.
                foreach (var opt in result.OptionalFailures)
                    OptionalFailures.Add($"{opt.CanonicalId}: {opt.Reason}");

                ResolutionLog.Add($"Resolution successful: {result.ResolvedVersions.Count} mods resolved");
                StatusMessage = $"Resolved {result.ResolvedVersions.Count} mod(s) successfully";
            }
            else
            {
                foreach (var conflict in result.Conflicts)
                    Conflicts.Add($"[{conflict.Type}] {conflict.Explanation}");

                ResolutionLog.Add($"Resolution failed: {result.FailureReason}");
                StatusMessage = $"Resolution failed: {result.FailureReason}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            ResolutionLog.Add($"Error: {ex.Message}");
        }
        finally
        {
            IsResolving = false;
        }
    }
}

public class DependencyNodeItem
{
    public string Id { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? ConstraintStr { get; set; }
    public bool IsRoot { get; set; }

    public string DisplayLabel => Version != null ? $"{Id} @ {Version}" : Id;
}

public class DependencyEdgeItem
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Type { get; set; } = "Required";
}
