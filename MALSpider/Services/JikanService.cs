using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MALSpider.Models;

namespace MALSpider.Services
{
    public class JikanService
    {
        private readonly HttpClient _httpClient;
        private const string BaseUrl = "https://api.jikan.moe/v4";
        
        // Jikan rate limit is ~60 requests/min (1 req/sec average)
        // We'll use a SemaphoreSlim to control concurrency and a delay to ensure we don't burst too fast.
        private readonly SemaphoreSlim _rateLimitSemaphore = new SemaphoreSlim(2, 2);
        private DateTime _lastRequestTime = DateTime.MinValue;
        private readonly TimeSpan _minInterval = TimeSpan.FromSeconds(0.6); // Slightly more aggressive with 2 parallel reqs

        public JikanService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "MALSpider/1.0");
        }

        private async Task EnsureRateLimit()
        {
            await _rateLimitSemaphore.WaitAsync();
            try
            {
                var now = DateTime.Now;
                var elapsed = now - _lastRequestTime;
                if (elapsed < _minInterval)
                {
                    await Task.Delay(_minInterval - elapsed);
                }
                _lastRequestTime = DateTime.Now;
            }
            finally
            {
                _rateLimitSemaphore.Release();
            }
        }

        private async Task<string> GetAsyncWithRateLimit(string url)
        {
            await EnsureRateLimit();
            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            if ((int)response.StatusCode == 429) // Too Many Requests
            {
                await Task.Delay(2000); // Wait longer if hit
                return await GetAsyncWithRateLimit(url);
            }
            return null;
        }

        public async Task<EntryNode> GetFullHierarchy(string searchOrUrl, Action<string> onStatusUpdate = null, Action<EntryNode> onNodeFetched = null)
        {
            int malId;
            string type;

            onStatusUpdate?.Invoke("Searching...");
            if (searchOrUrl.Contains("myanimelist.net"))
            {
                var parts = searchOrUrl.Split('/');
                // https://myanimelist.net/anime/37999/...
                if (parts.Length < 5) throw new Exception("Invalid MAL URL");
                type = parts[3].ToLower();
                if (!int.TryParse(parts[4], out malId)) throw new Exception("Invalid MAL ID in URL");
            }
            else
            {
                // Search by name
                var searchResult = await Search(searchOrUrl);
                if (searchResult == null) return null;
                malId = searchResult.Value.MalId;
                type = searchResult.Value.Type;
            }

            var visited = new Dictionary<string, EntryNode>();
            var totalDiscovered = 1;
    
            var root = await TraverseRecursive(malId, type, visited, s => onStatusUpdate?.Invoke($"{visited.Count}/{totalDiscovered}: {s}"), () => Interlocked.Increment(ref totalDiscovered), onNodeFetched);
            if (root != null) root.IsInputRoot = true;
            return root;
        }

        private async Task<EntryNode> TraverseRecursive(int malId, string type, Dictionary<string, EntryNode> visited, Action<string> onStatusUpdate, Action onNewDiscovered = null, Action<EntryNode> onNodeFetched = null)
        {
            string key = $"{type}_{malId}";
            lock (visited)
            {
                if (visited.TryGetValue(key, out var existing)) return existing;
            }

            onStatusUpdate?.Invoke($"Fetching {type} {malId}...");
            var node = new EntryNode { MalId = malId, Type = type };
            lock (visited)
            {
                visited[key] = node;
            }

            try
            {
                List<Relation> relations = null;
                if (type == "anime")
                {
                    var json = await GetAsyncWithRateLimit($"{BaseUrl}/anime/{malId}/full");
                    if (json != null)
                    {
                        var result = JsonSerializer.Deserialize<JikanResponse<AnimeFull>>(json);
                        if (result?.Data != null)
                        {
                            PopulateNode(node, result.Data);
                            node.SourceType = result.Data.Type;
                            relations = result.Data.Relations;
                        }
                    }
                }
                else if (type == "manga")
                {
                    var json = await GetAsyncWithRateLimit($"{BaseUrl}/manga/{malId}/full");
                    if (json != null)
                    {
                        var result = JsonSerializer.Deserialize<JikanResponse<MangaFull>>(json);
                        if (result?.Data != null)
                        {
                            PopulateNode(node, result.Data);
                            node.SourceType = result.Data.Type;
                            relations = result.Data.Relations;
                        }
                    }
                }

                // Notify that this node's details are now available
                onNodeFetched?.Invoke(node);

                if (relations != null)
                {
                    var tasks = new List<Task>();
                    foreach (var rel in relations)
                    {
                        foreach (var entry in rel.Entry)
                        {
                            onNewDiscovered?.Invoke();
                            string relType = rel.RelationType;
                            int targetMalId = entry.MalId;
                            string targetType = entry.Type.ToLower();

                            var task = TraverseRecursive(targetMalId, targetType, visited, onStatusUpdate, onNewDiscovered, onNodeFetched).ContinueWith(t =>
                            {
                                if (t.Result != null)
                                {
                                    lock (node.Relations)
                                    {
                                        if (node.Relations.Any(r => r.Target == t.Result)) return;

                                        string relationType = relType;
                                        // Requirement: "when choosing between 'parent story' and 'side story' for bidirectional links, always choose 'side story' -- this is a downward relationship"
                                        lock (t.Result.Relations)
                                        {
                                            var backRel = t.Result.Relations.FirstOrDefault(r => r.Target == node);
                                            if (backRel != null)
                                            {
                                                if ((relationType == "Parent story" && backRel.RelationType == "Side story") ||
                                                    (relationType == "Side story" && backRel.RelationType == "Parent story"))
                                                {
                                                    relationType = "Side story";
                                                    backRel.RelationType = "Side story";
                                                }
                                            }
                                        }

                                        node.Relations.Add(new EntryRelation
                                        {
                                            RelationType = relationType,
                                            Target = t.Result
                                        });
                                    }
                                }
                            });
                            tasks.Add(task);

                            // For progressive rendering: add the node to relations as soon as it's available in 'visited'
                            // so that the graph can start drawing connections to "skeleton" nodes.
                            lock (visited)
                            {
                                if (visited.TryGetValue($"{targetType}_{targetMalId}", out var targetNode))
                                {
                                    lock (node.Relations)
                                    {
                                        if (!node.Relations.Any(r => r.Target == targetNode))
                                        {
                                            node.Relations.Add(new EntryRelation
                                            {
                                                RelationType = relType,
                                                Target = targetNode
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                    await Task.WhenAll(tasks);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG_LOG] Error fetching {key}: {ex.Message}");
            }

            return node;
        }

        private void PopulateNode(EntryNode node, Anime details)
        {
            if (details == null) return;
            node.Title = details.Title;
            node.TitleEnglish = details.TitleEnglish;
            node.TitleJapanese = details.TitleJapanese;
            node.ImageUrl = details.Images?.Jpg?.LargeImageUrl ?? details.Images?.Jpg?.ImageUrl;
            node.MalUrl = details.Url;
            node.Synopsis = details.Synopsis;
            node.ReleaseDate = details.Aired?.From;
        }

        private void PopulateNode(EntryNode node, Manga details)
        {
            if (details == null) return;
            node.Title = details.Title;
            node.TitleEnglish = details.TitleEnglish;
            node.TitleJapanese = details.TitleJapanese;
            node.ImageUrl = details.Images?.Jpg?.LargeImageUrl ?? details.Images?.Jpg?.ImageUrl;
            node.MalUrl = details.Url;
            node.Synopsis = details.Synopsis;
            node.ReleaseDate = details.Published?.From;
        }

        private async Task<(int MalId, string Type)?> Search(string name)
        {
            var json = await GetAsyncWithRateLimit($"{BaseUrl}/anime?q={Uri.EscapeDataString(name)}&limit=1");
            if (json != null)
            {
                var result = JsonSerializer.Deserialize<JikanResponse<List<Anime>>>(json);
                if (result?.Data?.Count > 0)
                {
                    return (result.Data[0].MalId, "anime");
                }
            }
            return null;
        }
    }
}
