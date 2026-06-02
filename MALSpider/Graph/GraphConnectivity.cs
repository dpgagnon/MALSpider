using System;
using System.Collections.Generic;
using System.Linq;
using MALSpider.Models;

namespace MALSpider.Graph
{
    public static class GraphConnectivity
    {
        public static Dictionary<EntryNode, List<EntryNode>> BuildSequelAdj(List<EntryNode> allNodes)
        {
            var sequelAdj = new Dictionary<EntryNode, List<EntryNode>>();
            foreach (var n in allNodes)
            {
                if (!sequelAdj.ContainsKey(n)) sequelAdj[n] = new List<EntryNode>();
                foreach (var r in n.Relations)
                {
                    if (r.RelationType == "Sequel" || r.RelationType == "Side story" || r.RelationType == "Spin-off" ||
                        r.RelationType == "Alternative version" || r.RelationType == "Alternative setting" ||
                        r.RelationType == "Summary" || r.RelationType == "Full story")
                    {
                        if (r.Target.ReleaseDate >= n.ReleaseDate || !n.ReleaseDate.HasValue || !r.Target.ReleaseDate.HasValue)
                        {
                            if (!sequelAdj[n].Contains(r.Target)) sequelAdj[n].Add(r.Target);
                        }
                        else
                        {
                            if (!sequelAdj.ContainsKey(r.Target)) sequelAdj[r.Target] = new List<EntryNode>();
                            if (!sequelAdj[r.Target].Contains(n)) sequelAdj[r.Target].Add(n);
                        }
                    }
                    else if (r.RelationType == "Prequel" || r.RelationType == "Parent story")
                    {
                        if (!sequelAdj.ContainsKey(r.Target)) sequelAdj[r.Target] = new List<EntryNode>();
                        if (!sequelAdj[r.Target].Contains(n)) sequelAdj[r.Target].Add(n);
                    }
                }
            }
            return sequelAdj;
        }

        public static Dictionary<EntryNode, List<EntryNode>> BuildPredecessorAdj(Dictionary<EntryNode, List<EntryNode>> sequelAdj)
        {
            var predAdj = new Dictionary<EntryNode, List<EntryNode>>();
            foreach (var kvp in sequelAdj)
            {
                foreach (var neighbor in kvp.Value)
                {
                    if (!predAdj.ContainsKey(neighbor)) predAdj[neighbor] = new List<EntryNode>();
                    predAdj[neighbor].Add(kvp.Key);
                }
            }
            return predAdj;
        }

        public static bool IsReachable(EntryNode start, EntryNode target, Dictionary<EntryNode, List<EntryNode>> sequelAdj, bool includeSideStories)
        {
            if (start == target) return true;

            var visited = new HashSet<EntryNode>();
            var queue = new Queue<EntryNode>();
            queue.Enqueue(start);
            visited.Add(start);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                if (sequelAdj.TryGetValue(current, out var neighbors))
                {
                    foreach (var neighbor in neighbors)
                    {
                        if (neighbor == target) return true;
                        if (visited.Add(neighbor))
                        {
                            queue.Enqueue(neighbor);
                        }
                    }
                }

                if (includeSideStories)
                {
                    lock (current.Relations)
                    {
                        foreach (var rel in current.Relations)
                        {
                            if (rel.RelationType == "Side story" || rel.RelationType == "Parent story" ||
                                rel.RelationType == "Spin-off" || rel.RelationType == "Alternative version" ||
                                rel.RelationType == "Alternative setting" || rel.RelationType == "Summary" || rel.RelationType == "Full story")
                            {
                                if (rel.Target == target) return true;
                                if (visited.Add(rel.Target))
                                {
                                    queue.Enqueue(rel.Target);
                                }
                            }
                        }
                    }
                }
            }
            return false;
        }

        public static string InvertRelationType(string type)
        {
            return type switch
            {
                "Prequel" => "Sequel",
                "Sequel" => "Prequel",
                "Parent story" => "Side story",
                "Side story" => "Parent story",
                _ => type
            };
        }
    }
}
