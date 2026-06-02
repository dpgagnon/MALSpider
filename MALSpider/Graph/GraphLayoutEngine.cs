using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MALSpider.Models;
using MALSpider;

namespace MALSpider.Graph
{
    public class VisualRelation
    {
        public EntryNode Source { get; set; } = null!;
        public EntryNode Target { get; set; } = null!;
        public string Label { get; set; } = null!;
    }

    public class NodeLayout
    {
        public Dictionary<EntryNode, Point> NodePositions { get; } = new();
        public List<(string Title, double X, bool IsVisible, double LaneLeft, double LaneRight)> LaneHeaders { get; } = new();
        public List<double> SeparatorXPositions { get; } = new();
        public List<VisualRelation> VisualRelations { get; } = new();
        public double MaxBottom { get; set; }
        public double MaxRight { get; set; }
        public DateTime MinDate { get; set; }
        public DateTime MaxDate { get; set; }
        public List<DateTime> VisibleDates { get; set; } = new();
        public TimeCompressor Compressor { get; set; } = null!;
        public List<EntryNode> AllNodes { get; set; } = new();
        public Point RootPos { get; set; }
    }

    public static class GraphLayoutEngine
    {
        public static List<EntryNode> GetAllNodes(EntryNode root)
        {
            var visited = new HashSet<EntryNode>();
            var stack = new Stack<EntryNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (visited.Add(node))
                {
                    lock (node.Relations)
                    {
                        foreach (var rel in node.Relations) stack.Push(rel.Target);
                    }
                }
            }
            return visited.ToList();
        }

        public static List<EntryNode> GetConnectedNodes(EntryNode root, int maxDepth = 1)
        {
            var visited = new HashSet<EntryNode>();
            var queue = new Queue<(EntryNode Node, int Depth)>();
            queue.Enqueue((root, 0));
            visited.Add(root);

            while (queue.Count > 0)
            {
                var (node, depth) = queue.Dequeue();
                if (depth >= maxDepth) continue;

                lock (node.Relations)
                {
                    foreach (var rel in node.Relations)
                    {
                        if (visited.Add(rel.Target))
                        {
                            queue.Enqueue((rel.Target, depth + 1));
                        }
                    }
                }
            }
            return visited.ToList();
        }

        public static List<List<EntryNode>> ClusterNodes(List<EntryNode> allNodes)
        {
            var clusters = new List<List<EntryNode>>();
            var remaining = new HashSet<EntryNode>(allNodes);

            while (remaining.Count > 0)
            {
                var seed = remaining.First();
                var cluster = new List<EntryNode>();
                var queue = new Queue<EntryNode>();
                queue.Enqueue(seed);
                remaining.Remove(seed);

                while (queue.Count > 0)
                {
                    var node = queue.Dequeue();
                    cluster.Add(node);

                    var relatedInCluster = allNodes.Where(n => remaining.Contains(n) && IsCloselyRelated(node, n)).ToList();
                    foreach (var r in relatedInCluster)
                    {
                        int countForNode = allNodes.Count(n => IsCloselyRelated(node, n));
                        int countForR = allNodes.Count(n => IsCloselyRelated(r, n));

                        if (countForNode <= MALSpiderConstants.ClusteringThreshold && countForR <= MALSpiderConstants.ClusteringThreshold)
                        {
                            remaining.Remove(r);
                            queue.Enqueue(r);
                        }
                    }
                }
                clusters.Add(cluster);
            }

            var rootCluster = clusters.FirstOrDefault(c => c.Any(n => n.IsInputRoot));
            if (rootCluster != null)
            {
                clusters.Remove(rootCluster);
                clusters.Insert(0, rootCluster);
            }

            return clusters;
        }

        public static bool IsCloselyRelated(EntryNode a, EntryNode b)
        {
            return a.Relations.Any(r => r.Target == b && (r.RelationType == "Prequel" || r.RelationType == "Sequel" || r.RelationType == "Full story"))
                || b.Relations.Any(r => r.Target == a && (r.RelationType == "Prequel" || r.RelationType == "Sequel" || r.RelationType == "Full story"));
        }

        public static List<EntryNode> OrderByDateAndRelation(List<EntryNode> cluster)
        {
            var sequelAdj = GraphConnectivity.BuildSequelAdj(cluster);
            var roots = cluster.Where(n => !cluster.Any(m => sequelAdj.ContainsKey(m) && sequelAdj[m].Contains(n))).ToList();
            if (!roots.Any() && cluster.Any()) roots.Add(cluster[0]);

            var ordered = new List<EntryNode>();
            var visited = new HashSet<EntryNode>();
            var queue = new Queue<EntryNode>(roots.OrderBy(r => r.ReleaseDate ?? DateTime.MaxValue));

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (visited.Add(current))
                {
                    ordered.Add(current);
                    if (sequelAdj.TryGetValue(current, out var neighbors))
                    {
                        foreach (var neighbor in neighbors.OrderBy(n => n.ReleaseDate ?? DateTime.MaxValue))
                        {
                            if (!visited.Contains(neighbor)) queue.Enqueue(neighbor);
                        }
                    }
                }
            }

            // Catch any disconnected nodes
            foreach (var node in cluster.OrderBy(n => n.ReleaseDate ?? DateTime.MaxValue))
            {
                if (visited.Add(node)) ordered.Add(node);
            }

            return ordered;
        }

        public static NodeLayout ComputeLayout(List<EntryNode> allNodes, double nodeWidth, double nodeHeight, double verticalSpacing, bool showLN, bool showManga, bool showAnime)
        {
            var layout = new NodeLayout { AllNodes = allNodes };
            foreach (var n in allNodes)
            {
                if (n.SourceType == "Light Novel" || n.SourceType == "Novel") n.Lane = 0;
                else if (n.Type == "manga") n.Lane = 1;
                else n.Lane = 2;
            }

            var sequelAdj = GraphConnectivity.BuildSequelAdj(allNodes);
            var predAdj = GraphConnectivity.BuildPredecessorAdj(sequelAdj);

            var clusters = ClusterNodes(allNodes);

            // Filter nodes based on their lane and the user's selected visibility
            var visibleNodes = allNodes.Where(n =>
                (n.Lane == 0 && showLN) ||
                (n.Lane == 1 && showManga) ||
                (n.Lane == 2 && showAnime)).ToHashSet();

            var allDates = visibleNodes.Select(n => n.ReleaseDate).Where(d => d.HasValue).Cast<DateTime>().ToList();
            layout.VisibleDates = allDates;
            layout.MinDate = allDates.Any() ? allDates.Min() : DateTime.Now.AddYears(-10);
            layout.MaxDate = allDates.Any() ? allDates.Max() : DateTime.Now;
            if (layout.MinDate == layout.MaxDate) layout.MaxDate = layout.MinDate.AddDays(1);

            layout.Compressor = new TimeCompressor(layout.MinDate, layout.MaxDate, allDates);

            double currentX = 0;
            double maxBottom = 0;

            double GetY(DateTime? date) => layout.Compressor.GetY(date, 100, verticalSpacing);

            var columnGroups = new[] {
                (Title: "LIGHT NOVEL", LaneIndex: 0, IsVisible: showLN),
                (Title: "MANGA", LaneIndex: 1, IsVisible: showManga),
                (Title: "ANIME", LaneIndex: 2, IsVisible: showAnime)
            };

            foreach (var group in columnGroups)
            {
                double columnStartX = currentX;
                var occupiedY = new List<(double Top, double Bottom, double X)>();

                if (group.IsVisible)
                {
                    // Find nodes in this lane that are visible, grouped by their original clusters
                    var nodesByClusterInThisLane = clusters.Select(c => c.Where(n => n.Lane == group.LaneIndex && visibleNodes.Contains(n)).ToList())
                                                           .Where(c => c.Any())
                                                           .ToList();

                    if (nodesByClusterInThisLane.Any())
                    {
                        foreach (var clusterNodes in nodesByClusterInThisLane)
                        {
                            var orderedCluster = OrderByDateAndRelation(clusterNodes);
                            var sequelAdjInLane = GraphConnectivity.BuildSequelAdj(clusterNodes);

                            bool clusterPlaced = false;
                            int laneShift = 0;

                            while (!clusterPlaced)
                            {
                                double clusterBaseX = columnStartX + laneShift * (nodeWidth + MALSpiderConstants.ClusterHorizontalSpacing);
                                var testPositions = new Dictionary<EntryNode, Point>();
                                var nodeOffsets = new Dictionary<EntryNode, int>();

                                // Determine horizontal offsets within the cluster
                                var processedInCluster = new HashSet<EntryNode>();
                                foreach (var node in orderedCluster)
                                {
                                    int offset = 0;
                                    // Only sequels should be straight down vertically (offset 0 relative to parent)
                                    // Side stories and alternatives should be shifted horizontally.

                                    // Find all potential parents in the same cluster that have already been processed
                                    var parents = processedInCluster.Where(p => p.Relations.Any(r => r.Target == node)).ToList();

                                    if (parents.Any())
                                    {
                                        // Priority 1: If there's a Sequel relation, maintain its offset
                                        var sequelParent = parents.FirstOrDefault(p => p.Relations.Any(r => r.Target == node && r.RelationType == "Sequel"));
                                        if (sequelParent != null)
                                        {
                                            offset = nodeOffsets[sequelParent];
                                        }
                                        else
                                        {
                                            // Priority 2: Check for Prequel (node is a Sequel to someone else's Prequel)
                                            // OR: someone else is a Sequel to this node (which means we should be in their vertical line)
                                            var prequelChild = clusterNodes.FirstOrDefault(p => node.Relations.Any(r => r.Target == p && r.RelationType == "Prequel") && nodeOffsets.ContainsKey(p));
                                            var sequelChild = clusterNodes.FirstOrDefault(p => p.Relations.Any(r => r.Target == node && r.RelationType == "Prequel") && nodeOffsets.ContainsKey(p));

                                            if (prequelChild != null)
                                            {
                                                offset = nodeOffsets[prequelChild];
                                            }
                                            else if (sequelChild != null)
                                            {
                                                offset = nodeOffsets[sequelChild];
                                            }
                                            else
                                            {
                                                // Priority 3: Branch off from the most relevant parent (highest offset)
                                                var bestParent = parents.OrderByDescending(p => nodeOffsets[p]).First();
                                                bool isHorizontal = bestParent.Relations.Any(r => r.Target == node && MALSpiderConstants.HorizontalRelationTypes.Contains(r.RelationType));

                                                // If it's not a sequel, and not explicitly a horizontal type, we still branch if the user said "only sequels should be straight down"
                                                if (!isHorizontal)
                                                {
                                                    // Check if it's explicitly a vertical type. If not, default to branching.
                                                    bool isVertical = bestParent.Relations.Any(r => r.Target == node && r.RelationType == "Sequel");
                                                    if (!isVertical) isHorizontal = true;
                                                }

                                                offset = isHorizontal ? nodeOffsets[bestParent] + 1 : nodeOffsets[bestParent];
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // If no direct parent was found in processedInCluster, check if it's a Prequel/Sequel to something already processed
                                        var prequelChild = clusterNodes.FirstOrDefault(p => node.Relations.Any(r => r.Target == p && r.RelationType == "Prequel") && nodeOffsets.ContainsKey(p));
                                        var sequelChild = clusterNodes.FirstOrDefault(p => p.Relations.Any(r => r.Target == node && r.RelationType == "Prequel") && nodeOffsets.ContainsKey(p));

                                        if (prequelChild != null)
                                        {
                                            offset = nodeOffsets[prequelChild];
                                        }
                                        else if (sequelChild != null)
                                        {
                                            offset = nodeOffsets[sequelChild];
                                        }
                                    }

                                    nodeOffsets[node] = offset;
                                    processedInCluster.Add(node);
                                }

                                bool collision = false;
                                foreach (var node in orderedCluster)
                                {
                                    double y = GetY(node.ReleaseDate);
                                    double testX = clusterBaseX + nodeOffsets[node] * (nodeWidth + MALSpiderConstants.ClusterHorizontalSpacing);

                                    double top = y - MALSpiderConstants.CollisionPadding;
                                    double bottom = y + nodeHeight + MALSpiderConstants.CollisionPadding;

                                    if (occupiedY.Any(o => Math.Abs(o.X - testX) < 1.0 && !(bottom < o.Top || top > o.Bottom)))
                                    {
                                        collision = true;
                                        break;
                                    }
                                    testPositions[node] = new Point(testX, y);
                                }

                                if (!collision)
                                {
                                    foreach (var node in orderedCluster)
                                    {
                                        var pos = testPositions[node];
                                        layout.NodePositions[node] = pos;
                                        occupiedY.Add((pos.Y, pos.Y + nodeHeight, pos.X));
                                        maxBottom = Math.Max(maxBottom, pos.Y + nodeHeight);
                                        if (node.IsInputRoot) layout.RootPos = layout.NodePositions[node];
                                    }
                                    clusterPlaced = true;
                                }
                                else
                                {
                                    laneShift++;
                                }
                            }
                        }

                        double columnMaxX = occupiedY.Any() ? occupiedY.Max(o => o.X) : columnStartX;
                        double columnWidth = (columnMaxX + nodeWidth) - columnStartX;
                        double headerX = columnStartX + (columnWidth - nodeWidth) / 2;

                        double laneLeft = columnStartX;
                        double laneRight = columnMaxX + nodeWidth;

                        layout.LaneHeaders.Add((group.Title, headerX, group.IsVisible, laneLeft, laneRight));

                        currentX = columnMaxX + nodeWidth + MALSpiderConstants.HorizontalGap;
                        layout.SeparatorXPositions.Add(currentX - MALSpiderConstants.HorizontalGap / 2);
                    }
                    else
                    {
                        layout.LaneHeaders.Add((group.Title, currentX, group.IsVisible, currentX, currentX + nodeWidth));
                        currentX += nodeWidth + MALSpiderConstants.HorizontalGap;
                        layout.SeparatorXPositions.Add(currentX - MALSpiderConstants.HorizontalGap / 2);
                    }
                }
                else
                {
                    layout.LaneHeaders.Add((group.Title, currentX, group.IsVisible, currentX, currentX + nodeWidth));
                    currentX += nodeWidth + MALSpiderConstants.HorizontalGap;
                    layout.SeparatorXPositions.Add(currentX - MALSpiderConstants.HorizontalGap / 2);
                }
            }

            if (layout.SeparatorXPositions.Any()) layout.SeparatorXPositions.RemoveAt(layout.SeparatorXPositions.Count - 1);

            layout.MaxBottom = maxBottom;
            layout.MaxRight = currentX;

            // Compute Visual Relations
            var drawnPairs = new HashSet<(EntryNode, EntryNode)>();
            foreach (var sourceNode in allNodes)
            {
                if (!layout.NodePositions.TryGetValue(sourceNode, out var sourcePos)) continue;
                foreach (var rel in sourceNode.Relations)
                {
                    var targetNode = rel.Target;
                    if (!layout.NodePositions.TryGetValue(targetNode, out var targetPos)) continue;

                    bool isRedundant = false;
                    if (rel.RelationType == "Adaptation" || rel.RelationType == "Parent story" || rel.RelationType == "Other" || rel.RelationType == "Side story" || rel.RelationType == "Spin-off" || rel.RelationType == "Alternative version")
                    {
                        foreach (var otherRel in sourceNode.Relations)
                        {
                            if (otherRel == rel) continue;
                            if (GraphConnectivity.IsReachable(otherRel.Target, targetNode, sequelAdj, true))
                            {
                                isRedundant = true;
                                break;
                            }
                        }
                        if (!isRedundant)
                        {
                            var visitedAncestors = new HashSet<EntryNode>();
                            var ancestorQueue = new Queue<EntryNode>();
                            ancestorQueue.Enqueue(sourceNode);
                            visitedAncestors.Add(sourceNode);
                            while (ancestorQueue.Count > 0)
                            {
                                var current = ancestorQueue.Dequeue();
                                if (current != sourceNode && current.Relations.Any(r => (r.RelationType == "Adaptation" || r.RelationType == "Other" || r.RelationType == "Parent story" || r.RelationType == "Side story" || r.RelationType == "Spin-off" || r.RelationType == "Alternative version") &&
                                    (r.Target == targetNode || GraphConnectivity.IsReachable(r.Target, targetNode, sequelAdj, true))))
                                {
                                    isRedundant = true;
                                    break;
                                }
                                if (predAdj.TryGetValue(current, out var predecessors))
                                    foreach (var neighbor in predecessors) if (visitedAncestors.Add(neighbor)) ancestorQueue.Enqueue(neighbor);
                            }
                        }
                    }

                    if (!isRedundant)
                    {
                        EntryNode vSource, vTarget;
                        string vLabel;
                        if (GraphConnectivity.IsReachable(sourceNode, targetNode, sequelAdj, true))
                        {
                            vSource = sourceNode; vTarget = targetNode; vLabel = rel.RelationType;
                        }
                        else if (GraphConnectivity.IsReachable(targetNode, sourceNode, sequelAdj, true))
                        {
                            vSource = targetNode; vTarget = sourceNode; vLabel = GraphConnectivity.InvertRelationType(rel.RelationType);
                        }
                        else if (targetPos.Y >= sourcePos.Y) { vSource = sourceNode; vTarget = targetNode; vLabel = rel.RelationType; }
                        else { vSource = targetNode; vTarget = sourceNode; vLabel = GraphConnectivity.InvertRelationType(rel.RelationType); }

                        if (drawnPairs.Add((vSource, vTarget)))
                        {
                            layout.VisualRelations.Add(new VisualRelation { Source = vSource, Target = vTarget, Label = vLabel });
                        }
                    }
                }
            }

            return layout;
        }
    }
}
