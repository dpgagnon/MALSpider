using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MALSpider.Models;

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
        public List<(string Title, double X)> LaneHeaders { get; } = new();
        public List<double> SeparatorXPositions { get; } = new();
        public List<VisualRelation> VisualRelations { get; } = new();
        public double MaxBottom { get; set; }
        public double MaxRight { get; set; }
        public DateTime MinDate { get; set; }
        public DateTime MaxDate { get; set; }
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
            return cluster.OrderBy(n => n.ReleaseDate ?? DateTime.MaxValue)
                          .ThenBy(n => n.Title)
                          .ToList();
        }

        public static NodeLayout ComputeLayout(List<EntryNode> allNodes, double nodeWidth, double nodeHeight, double verticalSpacing)
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
            var lnClusters = clusters.Where(c => c.Any(n => n.Lane == 0)).ToList();
            var mangaClusters = clusters.Where(c => c.Any(n => n.Lane == 1)).ToList();
            var animeClusters = clusters.Where(c => c.Any(n => n.Lane == 2)).ToList();

            var allDates = allNodes.Select(n => n.ReleaseDate).Where(d => d.HasValue).Cast<DateTime>().ToList();
            layout.MinDate = allDates.Any() ? allDates.Min() : DateTime.Now.AddYears(-10);
            layout.MaxDate = allDates.Any() ? allDates.Max() : DateTime.Now;
            if (layout.MinDate == layout.MaxDate) layout.MaxDate = layout.MinDate.AddDays(1);

            layout.Compressor = new TimeCompressor(layout.MinDate, layout.MaxDate, allDates);

            double currentX = 20;
            double maxBottom = 0;

            double GetY(DateTime? date) => layout.Compressor.GetY(date, 100, verticalSpacing);

            var columnGroups = new[] {
                (Title: "LIGHT NOVEL", Clusters: lnClusters),
                (Title: "MANGA", Clusters: mangaClusters),
                (Title: "ANIME", Clusters: animeClusters)
            };

            foreach (var group in columnGroups)
            {
                if (!group.Clusters.Any()) continue;
                layout.LaneHeaders.Add((group.Title, currentX));

                var occupiedY = new List<(double Top, double Bottom, double X)>();
                double columnStartX = currentX;

                foreach (var cluster in group.Clusters)
                {
                    var orderedCluster = OrderByDateAndRelation(cluster);
                    double clusterPreferredX = columnStartX;
                    bool clusterPlaced = false;
                    int laneShift = 0;

                    while (!clusterPlaced)
                    {
                        double testX = columnStartX + laneShift * (nodeWidth + MALSpiderConstants.ClusterHorizontalSpacing);
                        bool collision = false;
                        foreach (var node in orderedCluster)
                        {
                            double y = GetY(node.ReleaseDate);
                            double top = y - MALSpiderConstants.CollisionPadding;
                            double bottom = y + nodeHeight + MALSpiderConstants.CollisionPadding;

                            if (occupiedY.Any(o => Math.Abs(o.X - testX) < 1.0 && !(bottom < o.Top || top > o.Bottom)))
                            {
                                collision = true;
                                break;
                            }
                        }

                        if (!collision)
                        {
                            foreach (var node in orderedCluster)
                            {
                                double y = GetY(node.ReleaseDate);
                                layout.NodePositions[node] = new Point(testX, y);
                                occupiedY.Add((y, y + nodeHeight, testX));
                                maxBottom = Math.Max(maxBottom, y + nodeHeight);
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

                double columnMaxX = occupiedY.Any() ? occupiedY.Max(o => o.X) : currentX;
                currentX = columnMaxX + nodeWidth + MALSpiderConstants.HorizontalGap;
                layout.SeparatorXPositions.Add(currentX - MALSpiderConstants.HorizontalGap / 2);
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
