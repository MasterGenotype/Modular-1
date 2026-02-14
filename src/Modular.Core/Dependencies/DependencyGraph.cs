using Modular.Core.Metadata;

namespace Modular.Core.Dependencies;

/// <summary>
/// Directed multigraph representing mod dependencies and conflicts.
/// Thread-safe for concurrent access.
/// </summary>
public class DependencyGraph
{
    private readonly Dictionary<string, ModNode> _nodes = new();
    private readonly List<DependencyEdge> _edges = new();
    private readonly object _lock = new();

    /// <summary>
    /// Adds a node to the graph. If a node with the same ID already exists, it is replaced.
    /// </summary>
    public void AddNode(ModNode node)
    {
        lock (_lock)
        {
            _nodes[node.GetNodeId()] = node;
        }
    }

    /// <summary>
    /// Removes a node and all associated edges from the graph.
    /// </summary>
    public bool RemoveNode(ModNode node)
    {
        lock (_lock)
        {
            var nodeId = node.GetNodeId();
            if (!_nodes.Remove(nodeId))
                return false;

            // Remove all edges connected to this node
            _edges.RemoveAll(e =>
                e.From.GetNodeId() == nodeId ||
                e.To.GetNodeId() == nodeId);

            return true;
        }
    }

    /// <summary>
    /// Gets a node by its ID.
    /// </summary>
    public ModNode? GetNode(string nodeId)
    {
        lock (_lock)
        {
            return _nodes.TryGetValue(nodeId, out var node) ? node : null;
        }
    }

    /// <summary>
    /// Gets all nodes in the graph.
    /// </summary>
    public IReadOnlyList<ModNode> GetAllNodes()
    {
        lock (_lock)
        {
            return _nodes.Values.ToList();
        }
    }

    /// <summary>
    /// Adds a directed edge to the graph.
    /// </summary>
    public void AddEdge(DependencyEdge edge)
    {
        lock (_lock)
        {
            // Ensure nodes exist
            if (!_nodes.ContainsKey(edge.From.GetNodeId()))
                AddNode(edge.From);
            if (!_nodes.ContainsKey(edge.To.GetNodeId()))
                AddNode(edge.To);

            _edges.Add(edge);
        }
    }

    /// <summary>
    /// Removes an edge from the graph.
    /// </summary>
    public bool RemoveEdge(DependencyEdge edge)
    {
        lock (_lock)
        {
            return _edges.Remove(edge);
        }
    }

    /// <summary>
    /// Removes all edges matching a predicate.
    /// </summary>
    public int RemoveEdges(Func<DependencyEdge, bool> predicate)
    {
        lock (_lock)
        {
            return _edges.RemoveAll(e => predicate(e));
        }
    }

    /// <summary>
    /// Gets all edges in the graph.
    /// </summary>
    public IReadOnlyList<DependencyEdge> GetAllEdges()
    {
        lock (_lock)
        {
            return _edges.ToList();
        }
    }

    /// <summary>
    /// Gets all dependencies of a node (outgoing edges).
    /// </summary>
    public IReadOnlyList<DependencyEdge> GetDependencies(ModNode node)
    {
        lock (_lock)
        {
            var nodeId = node.GetNodeId();
            return _edges.Where(e => e.From.GetNodeId() == nodeId).ToList();
        }
    }

    /// <summary>
    /// Gets all dependents of a node (incoming edges).
    /// </summary>
    public IReadOnlyList<DependencyEdge> GetDependents(ModNode node)
    {
        lock (_lock)
        {
            var nodeId = node.GetNodeId();
            return _edges.Where(e => e.To.GetNodeId() == nodeId).ToList();
        }
    }

    /// <summary>
    /// Gets all edges between two nodes.
    /// </summary>
    public IReadOnlyList<DependencyEdge> GetEdgesBetween(ModNode from, ModNode to)
    {
        lock (_lock)
        {
            var fromId = from.GetNodeId();
            var toId = to.GetNodeId();
            return _edges.Where(e =>
                e.From.GetNodeId() == fromId &&
                e.To.GetNodeId() == toId).ToList();
        }
    }

    /// <summary>
    /// Gets all edges of a specific type.
    /// </summary>
    public IReadOnlyList<DependencyEdge> GetEdgesByType(DependencyType type)
    {
        lock (_lock)
        {
            return _edges.Where(e => e.Type == type).ToList();
        }
    }

    /// <summary>
    /// Checks if a node exists in the graph.
    /// </summary>
    public bool ContainsNode(ModNode node)
    {
        lock (_lock)
        {
            return _nodes.ContainsKey(node.GetNodeId());
        }
    }

    /// <summary>
    /// Checks if there's a path from one node to another (transitive dependency).
    /// </summary>
    public bool HasPath(ModNode from, ModNode to)
    {
        lock (_lock)
        {
            var visited = new HashSet<string>();
            return HasPathRecursive(from.GetNodeId(), to.GetNodeId(), visited);
        }
    }

    private bool HasPathRecursive(string fromId, string toId, HashSet<string> visited)
    {
        if (fromId == toId)
            return true;

        if (visited.Contains(fromId))
            return false;

        visited.Add(fromId);

        var outgoingEdges = _edges.Where(e => e.From.GetNodeId() == fromId);
        foreach (var edge in outgoingEdges)
        {
            if (HasPathRecursive(edge.To.GetNodeId(), toId, visited))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detects circular dependencies in the graph.
    /// </summary>
    public List<List<ModNode>> DetectCycles()
    {
        lock (_lock)
        {
            var cycles = new List<List<ModNode>>();
            var visited = new HashSet<string>();
            var recursionStack = new HashSet<string>();
            var path = new Stack<string>();

            foreach (var nodeId in _nodes.Keys)
            {
                if (!visited.Contains(nodeId))
                {
                    DetectCyclesRecursive(nodeId, visited, recursionStack, path, cycles);
                }
            }

            return cycles;
        }
    }

    private void DetectCyclesRecursive(
        string nodeId,
        HashSet<string> visited,
        HashSet<string> recursionStack,
        Stack<string> path,
        List<List<ModNode>> cycles)
    {
        visited.Add(nodeId);
        recursionStack.Add(nodeId);
        path.Push(nodeId);

        var outgoingEdges = _edges.Where(e => e.From.GetNodeId() == nodeId);
        foreach (var edge in outgoingEdges)
        {
            var targetId = edge.To.GetNodeId();

            if (!visited.Contains(targetId))
            {
                DetectCyclesRecursive(targetId, visited, recursionStack, path, cycles);
            }
            else if (recursionStack.Contains(targetId))
            {
                // Found a cycle
                var cycle = new List<ModNode>();
                var cycleNodes = path.TakeWhile(id => id != targetId).ToList();
                cycleNodes.Add(targetId);
                cycleNodes.Reverse();

                foreach (var id in cycleNodes)
                {
                    if (_nodes.TryGetValue(id, out var node))
                        cycle.Add(node);
                }

                cycles.Add(cycle);
            }
        }

        path.Pop();
        recursionStack.Remove(nodeId);
    }

    /// <summary>
    /// Performs a topological sort of the graph.
    /// Returns null if the graph contains cycles.
    /// </summary>
    public List<ModNode>? TopologicalSort()
    {
        lock (_lock)
        {
            var sorted = new List<ModNode>();
            var visited = new HashSet<string>();
            var temporaryMarks = new HashSet<string>();

            foreach (var node in _nodes.Values)
            {
                if (!visited.Contains(node.GetNodeId()))
                {
                    if (!TopologicalSortVisit(node, visited, temporaryMarks, sorted))
                        return null; // Cycle detected
                }
            }

            sorted.Reverse();
            return sorted;
        }
    }

    private bool TopologicalSortVisit(
        ModNode node,
        HashSet<string> visited,
        HashSet<string> temporaryMarks,
        List<ModNode> sorted)
    {
        var nodeId = node.GetNodeId();

        if (temporaryMarks.Contains(nodeId))
            return false; // Cycle detected

        if (visited.Contains(nodeId))
            return true;

        temporaryMarks.Add(nodeId);

        var dependencies = _edges.Where(e => e.From.GetNodeId() == nodeId);
        foreach (var edge in dependencies)
        {
            if (!TopologicalSortVisit(edge.To, visited, temporaryMarks, sorted))
                return false;
        }

        temporaryMarks.Remove(nodeId);
        visited.Add(nodeId);
        sorted.Add(node);

        return true;
    }

    /// <summary>
    /// Clears all nodes and edges from the graph.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _nodes.Clear();
            _edges.Clear();
        }
    }

    /// <summary>
    /// Gets statistics about the graph.
    /// </summary>
    public GraphStatistics GetStatistics()
    {
        lock (_lock)
        {
            return new GraphStatistics
            {
                NodeCount = _nodes.Count,
                EdgeCount = _edges.Count,
                RequiredEdgeCount = _edges.Count(e => e.Type == DependencyType.Required),
                OptionalEdgeCount = _edges.Count(e => e.Type == DependencyType.Optional),
                IncompatibleEdgeCount = _edges.Count(e => e.Type == DependencyType.Incompatible),
                HasCycles = DetectCycles().Count > 0
            };
        }
    }
}

/// <summary>
/// Statistics about a dependency graph.
/// </summary>
public class GraphStatistics
{
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public int RequiredEdgeCount { get; set; }
    public int OptionalEdgeCount { get; set; }
    public int IncompatibleEdgeCount { get; set; }
    public bool HasCycles { get; set; }
}
