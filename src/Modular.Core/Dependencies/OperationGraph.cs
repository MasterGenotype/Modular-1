using Modular.Sdk.Installers;

namespace Modular.Core.Dependencies;

/// <summary>
/// Directed acyclic graph for file-operation scheduling.
/// Uses Kahn's algorithm for topological sort to produce a valid execution order.
/// Separate from DependencyGraph (which handles mod-level dependencies).
/// </summary>
public class OperationGraph
{
    /// <summary>
    /// Builds a dependency-sorted execution order from a list of file operations.
    /// Automatically infers directory-creation dependencies (parent dirs must be created
    /// before child files are written).
    /// </summary>
    /// <param name="operations">Operations with DependsOn edges.</param>
    /// <returns>Topologically sorted operations, or null if a cycle exists.</returns>
    public static List<FileOperation>? Sort(IReadOnlyList<FileOperation> operations)
    {
        if (operations.Count == 0)
            return new List<FileOperation>();

        // Build index: operationId -> operation
        var opById = new Dictionary<string, FileOperation>();
        foreach (var op in operations)
            opById[op.OperationId] = op;

        // Build adjacency list (forward edges) and in-degree counts
        var inDegree = new Dictionary<string, int>();
        var adjacency = new Dictionary<string, List<string>>();

        foreach (var op in operations)
        {
            if (!inDegree.ContainsKey(op.OperationId))
                inDegree[op.OperationId] = 0;
            if (!adjacency.ContainsKey(op.OperationId))
                adjacency[op.OperationId] = new List<string>();

            foreach (var depId in op.DependsOn)
            {
                if (!opById.ContainsKey(depId))
                    continue; // Skip dangling references

                if (!adjacency.ContainsKey(depId))
                    adjacency[depId] = new List<string>();

                adjacency[depId].Add(op.OperationId);
                inDegree[op.OperationId] = inDegree.GetValueOrDefault(op.OperationId) + 1;
            }
        }

        // Kahn's algorithm
        var queue = new Queue<string>();
        foreach (var (id, deg) in inDegree)
        {
            if (deg == 0)
                queue.Enqueue(id);
        }

        var sorted = new List<FileOperation>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (opById.TryGetValue(current, out var op))
                sorted.Add(op);

            if (adjacency.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    inDegree[neighbor]--;
                    if (inDegree[neighbor] == 0)
                        queue.Enqueue(neighbor);
                }
            }
        }

        // If we didn't visit all nodes, there's a cycle
        if (sorted.Count != operations.Count)
            return null;

        return sorted;
    }

    /// <summary>
    /// Validates that the operation graph is acyclic.
    /// </summary>
    /// <param name="operations">Operations to validate.</param>
    /// <returns>True if the graph is valid (no cycles).</returns>
    public static bool IsValid(IReadOnlyList<FileOperation> operations)
    {
        return Sort(operations) != null;
    }

    /// <summary>
    /// Infers directory-creation dependencies for a list of operations.
    /// Ensures that CreateDirectory operations for parent directories are listed
    /// as dependencies of file operations targeting those directories.
    /// </summary>
    /// <param name="operations">Operations to augment with directory dependencies.</param>
    /// <returns>Augmented operations list with CreateDirectory operations and dependency edges.</returns>
    public static List<FileOperation> InferDirectoryDependencies(IReadOnlyList<FileOperation> operations)
    {
        var result = new List<FileOperation>(operations);
        var dirOps = new Dictionary<string, FileOperation>(StringComparer.OrdinalIgnoreCase);

        // Collect all unique parent directories needed
        foreach (var op in operations)
        {
            if (op.Type == FileOperationType.CreateDirectory)
            {
                dirOps[op.DestinationPath.TrimEnd('/', '\\')] = op;
                continue;
            }

            var dir = Path.GetDirectoryName(op.DestinationPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir))
                continue;

            // Create directory operations for all ancestor directories
            var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = string.Empty;
            foreach (var part in parts)
            {
                current = string.IsNullOrEmpty(current) ? part : $"{current}/{part}";
                if (!dirOps.ContainsKey(current))
                {
                    var mkdirOp = new FileOperation
                    {
                        Type = FileOperationType.CreateDirectory,
                        DestinationPath = current,
                        SourcePath = string.Empty,
                        SizeBytes = 0
                    };
                    dirOps[current] = mkdirOp;
                    result.Add(mkdirOp);
                }
            }
        }

        // Wire directory dependencies
        foreach (var op in result)
        {
            if (op.Type == FileOperationType.CreateDirectory)
                continue;

            var dir = Path.GetDirectoryName(op.DestinationPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && dirOps.TryGetValue(dir, out var dirOp))
            {
                if (!op.DependsOn.Contains(dirOp.OperationId))
                    op.DependsOn.Add(dirOp.OperationId);
            }
        }

        // Wire parent → child directory dependencies
        foreach (var (dirPath, dirOp) in dirOps)
        {
            var parentDir = Path.GetDirectoryName(dirPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parentDir) && dirOps.TryGetValue(parentDir, out var parentOp))
            {
                if (!dirOp.DependsOn.Contains(parentOp.OperationId))
                    dirOp.DependsOn.Add(parentOp.OperationId);
            }
        }

        return result;
    }
}
