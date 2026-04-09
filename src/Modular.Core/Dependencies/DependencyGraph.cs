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

}
