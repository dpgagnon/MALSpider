using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Linq;
using MALSpider.Models;

namespace MALSpider.Services
{
    public class JikanService
    {
        public Action<EntryNode>? OnNodeImageLoaded { get; set; }
        private readonly HttpClient _httpClient;
        private static string BaseUrl => MALSpiderConstants.JikanBaseUrl;

        // Jikan rate limit is ~60 requests/min (1 req/sec average)
        // We'll use a SemaphoreSlim to control concurrency and a delay to ensure we don't burst too fast.
        private readonly SemaphoreSlim _rateLimitSemaphore = new SemaphoreSlim(2, 2);
        private DateTime _lastRequestTime = DateTime.MinValue;
        private readonly TimeSpan _minInterval = TimeSpan.FromSeconds(MALSpiderConstants.JikanMinIntervalSeconds); // Slightly more aggressive with 2 parallel reqs

        public JikanService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "MALSpider/1.0");

            if (!Directory.Exists(MALSpiderConstants.CacheDirectory)) 
                Directory.CreateDirectory(MALSpiderConstants.CacheDirectory);
            
            string imgDir = Path.Combine(MALSpiderConstants.CacheDirectory, "images");
            if (!Directory.Exists(imgDir)) 
                Directory.CreateDirectory(imgDir);
        }

        public void ClearCache()
        {
            try
            {
                if (Directory.Exists(MALSpiderConstants.CacheDirectory))
                {
                    Directory.Delete(MALSpiderConstants.CacheDirectory, true);
                    Directory.CreateDirectory(MALSpiderConstants.CacheDirectory);
                    Directory.CreateDirectory(Path.Combine(MALSpiderConstants.CacheDirectory, "images"));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DEBUG_LOG] Failed to clear cache: {ex.Message}");
            }
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
                await Task.Delay(MALSpiderConstants.JikanRetryDelayMs); // Wait longer if hit
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
            var discovered = new HashSet<string>();
            discovered.Add($"{type}_{malId}");
            var totalDiscovered = 1;

            var root = await TraverseRecursive(malId, type, visited, discovered, s => onStatusUpdate?.Invoke($"{visited.Count}/{totalDiscovered}: {s}"), () => Interlocked.Increment(ref totalDiscovered), onNodeFetched);
            if (root != null) root.IsInputRoot = true;
            return root;
        }

        private async Task<EntryNode> TraverseRecursive(int malId, string type, Dictionary<string, EntryNode> visited, HashSet<string> discovered, Action<string> onStatusUpdate, Action onNewDiscovered = null, Action<EntryNode> onNodeFetched = null, string fallbackTitle = null)
        {
            string key = $"{type}_{malId}";
            lock (visited)
            {
                if (visited.TryGetValue(key, out var existing)) return existing;
            }

            onStatusUpdate?.Invoke($"Fetching {type} {malId}...");
            var node = new EntryNode { MalId = malId, Type = type, Title = fallbackTitle };
            lock (visited)
            {
                visited[key] = node;
            }

            try
            {
                List<Relation> relations = null;
                string json = null;
                string cachePath = Path.Combine(MALSpiderConstants.CacheDirectory, $"{key}.json");

                if (File.Exists(cachePath))
                {
                    json = await File.ReadAllTextAsync(cachePath);
                }

                if (json == null)
                {
                    if (type == "anime")
                    {
                        json = await GetAsyncWithRateLimit($"{BaseUrl}/anime/{malId}/full");
                    }
                    else if (type == "manga")
                    {
                        json = await GetAsyncWithRateLimit($"{BaseUrl}/manga/{malId}/full");
                    }

                    if (json != null)
                    {
                        await File.WriteAllTextAsync(cachePath, json);
                    }
                }

                if (json != null)
                {
                    if (type == "anime")
                    {
                        var result = JsonSerializer.Deserialize<JikanResponse<AnimeFull>>(json);
                        if (result?.Data != null)
                        {
                            PopulateNode(node, result.Data);
                            node.SourceType = result.Data.Type;
                            relations = result.Data.Relations;
                        }
                        else
                        {
                            node.ErrorMessage = "Malformed API response (Anime)";
                        }
                    }
                    else if (type == "manga")
                    {
                        var result = JsonSerializer.Deserialize<JikanResponse<MangaFull>>(json);
                        if (result?.Data != null)
                        {
                            PopulateNode(node, result.Data);
                            node.SourceType = result.Data.Type;
                            relations = result.Data.Relations;
                        }
                        else
                        {
                            node.ErrorMessage = "Malformed API response (Manga)";
                        }
                    }
                }
                else
                {
                    node.ErrorMessage = $"Failed to fetch data or not found ({type})";
                }

                // Notify that this node's details are now available
                onNodeFetched?.Invoke(node);

                if (relations != null)
                {
                    foreach (var rel in relations)
                    {
                        foreach (var entry in rel.Entry)
                        {
                            string relType = rel.RelationType;
                            int targetMalId = entry.MalId;
                            string targetType = entry.Type.ToLower();
                            if (targetType != "anime" && targetType != "manga") continue;

                            string targetKey = $"{targetType}_{targetMalId}";
                            bool isNew = false;
                            lock (discovered)
                            {
                                if (discovered.Add(targetKey))
                                {
                                    isNew = true;
                                }
                            }
                            if (isNew) onNewDiscovered?.Invoke();

                            var targetNode = await TraverseRecursive(targetMalId, targetType, visited, discovered, onStatusUpdate, onNewDiscovered, onNodeFetched, entry.Name);
                            if (targetNode != null)
                            {
                                lock (node.Relations)
                                {
                                    if (!node.Relations.Any(r => r.Target == targetNode))
                                    {
                                        string relationType = relType;
                                        // Requirement: "when choosing between 'parent story' and 'side story' for bidirectional links, always choose 'side story' -- this is a downward relationship"
                                        lock (targetNode.Relations)
                                        {
                                            var backRel = targetNode.Relations.FirstOrDefault(r => r.Target == node);
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
                                            Target = targetNode
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG_LOG] Error fetching {key}: {ex.Message}");
                node.ErrorMessage = ex.Message;
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
            _ = LoadImageAsync(node);
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
            _ = LoadImageAsync(node);
        }

        private async Task LoadImageAsync(EntryNode node)
        {
            if (string.IsNullOrEmpty(node.ImageUrl)) return;
            try
            {
                string extension = Path.GetExtension(node.ImageUrl);
                if (string.IsNullOrEmpty(extension)) extension = ".jpg";
                string cachePath = Path.Combine(MALSpiderConstants.CacheDirectory, "images", $"{node.Type}_{node.MalId}{extension}");

                byte[] bytes;
                if (File.Exists(cachePath))
                {
                    bytes = await File.ReadAllBytesAsync(cachePath);
                }
                else
                {
                    bytes = await _httpClient.GetByteArrayAsync(node.ImageUrl);
                    await File.WriteAllBytesAsync(cachePath, bytes);
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = new MemoryStream(bytes);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    node.LoadedImage = bitmap;
                    OnNodeImageLoaded?.Invoke(node);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DEBUG_LOG] Failed to load image for {node.Title}: {ex.Message}");
            }
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
