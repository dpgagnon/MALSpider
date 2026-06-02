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

        // Jikan rate limit: 2 requests per second, 30 requests per minute.
        // Cached requests do not count towards the limit.
        private readonly SemaphoreSlim _rateLimitSemaphore = new SemaphoreSlim(1, 1);
        private readonly Queue<DateTime> _requestHistory = new Queue<DateTime>();

        private Dictionary<string, EntryNode> _lastVisited = new();
        private HashSet<string> _lastDiscovered = new();
        private int _lastTotalDiscovered = 0;

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

        public void DeleteNodeCache(EntryNode node)
        {
            try
            {
                string key = $"{node.Type}_{node.MalId}";
                string cachePath = Path.Combine(MALSpiderConstants.CacheDirectory, $"{key}.json");
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }

                // Also delete image cache
                string imgDir = Path.Combine(MALSpiderConstants.CacheDirectory, "images");
                if (Directory.Exists(imgDir))
                {
                    var files = Directory.GetFiles(imgDir, $"{key}.*");
                    foreach (var file in files)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DEBUG_LOG] Failed to delete node cache: {ex.Message}");
            }
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
                while (true)
                {
                    var now = DateTime.Now;

                    // Remove history older than 1 minute
                    while (_requestHistory.Count > 0 && (now - _requestHistory.Peek()).TotalSeconds >= 60)
                    {
                        _requestHistory.Dequeue();
                    }

                    // Check per-minute limit
                    if (_requestHistory.Count >= MALSpiderConstants.JikanRequestsPerMinute)
                    {
                        var oldest = _requestHistory.Peek();
                        var delay = TimeSpan.FromSeconds(60.1) - (now - oldest);
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay);
                            continue; // Re-evaluate after delay
                        }
                    }

                    // Check per-second limit
                    int requestsInLastSecond = 0;
                    foreach (var time in _requestHistory)
                    {
                        if ((now - time).TotalSeconds < 1)
                        {
                            requestsInLastSecond++;
                        }
                    }

                    if (requestsInLastSecond >= MALSpiderConstants.JikanRequestsPerSecond)
                    {
                        await Task.Delay(100); // Wait a bit and retry
                        continue;
                    }

                    // If we get here, we are within limits
                    _requestHistory.Enqueue(DateTime.Now);
                    break;
                }
            }
            finally
            {
                _rateLimitSemaphore.Release();
            }
        }

        private async Task<string> GetAsyncWithRateLimit(string url, CancellationToken ct = default)
        {
            await EnsureRateLimit();
            var response = await _httpClient.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(ct);
            }
            if ((int)response.StatusCode == 429) // Too Many Requests
            {
                await Task.Delay(MALSpiderConstants.JikanRetryDelayMs, ct); // Wait longer if hit
                return await GetAsyncWithRateLimit(url, ct);
            }
            return null;
        }

        public async Task<EntryNode> GetFullHierarchy(string searchOrUrl, Action<string> onStatusUpdate = null, Action<EntryNode> onNodeFetched = null, CancellationToken ct = default)
        {
            int malId;
            string type;
            string initialTitle = null;

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
                var searchResult = await Search(searchOrUrl, ct);
                if (searchResult == null) return null;
                malId = searchResult.Value.MalId;
                type = searchResult.Value.Type;
                initialTitle = searchResult.Value.Title;
            }

            _lastVisited = new Dictionary<string, EntryNode>();
            _lastDiscovered = new HashSet<string>();
            _lastTotalDiscovered = 0;

            var root = await TraverseIterative(malId, type, _lastVisited, _lastDiscovered, s => onStatusUpdate?.Invoke($"{_lastVisited.Count}/{_lastTotalDiscovered}: {s}"), onNodeFetched, initialTitle, ct);
            if (root != null) root.IsInputRoot = true;
            return root;
        }

        private async Task<EntryNode> TraverseIterative(int startMalId, string startType, Dictionary<string, EntryNode> visited, HashSet<string> discovered, Action<string> onStatusUpdate, Action<EntryNode> onNodeFetched = null, string startTitle = null, CancellationToken ct = default)
        {
            var queue = new Queue<(int MalId, string Type, string Title)>();

            string startKey = $"{startType}_{startMalId}";
            if (discovered.Add(startKey))
            {
                _lastTotalDiscovered++;
                queue.Enqueue((startMalId, startType, startTitle));
            }

            EntryNode rootNode = null;
            int processedCount = 0;
            var lastStatusTime = DateTime.MinValue;

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var (malId, type, title) = queue.Dequeue();
                string key = $"{type}_{malId}";

                EntryNode node;
                bool isNewNode = false;
                if (!visited.TryGetValue(key, out node))
                {
                    node = new EntryNode { MalId = malId, Type = type, Title = title };
                    visited[key] = node;
                    isNewNode = true;
                }

                if (malId == startMalId && type == startType) rootNode = node;

                if (!isNewNode) continue;

                string displayTitle = title ?? $"{type} {malId}";

                // Throttle status updates for cached items to prevent UI lag
                processedCount++;
                if ((DateTime.Now - lastStatusTime).TotalMilliseconds > 100)
                {
                    onStatusUpdate?.Invoke($"Fetching {displayTitle}...");
                    lastStatusTime = DateTime.Now;
                    await Task.Delay(1, ct); // Yield to UI thread
                }

                List<Relation> relations = null;
                string json = null;
                string cachePath = Path.Combine(MALSpiderConstants.CacheDirectory, $"{key}.json");

                if (File.Exists(cachePath))
                {
                    try
                    {
                        json = await File.ReadAllTextAsync(cachePath, ct);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DEBUG_LOG] Error reading cache file {cachePath}: {ex.Message}");
                    }
                }

                if (json == null)
                {
                    int retries = 0;
                    while (json == null && retries <= MALSpiderConstants.MaxRetries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (retries > 0)
                        {
                            onStatusUpdate?.Invoke($"Retrying {displayTitle} ({retries}/{MALSpiderConstants.MaxRetries})...");
                            await Task.Delay(MALSpiderConstants.JikanRetryDelayMs * retries, ct);
                        }

                        try
                        {
                            if (type == "anime")
                            {
                                json = await GetAsyncWithRateLimit($"{BaseUrl}/anime/{malId}/full", ct);
                            }
                            else if (type == "manga")
                            {
                                json = await GetAsyncWithRateLimit($"{BaseUrl}/manga/{malId}/full", ct);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[DEBUG_LOG] Error in API call for {displayTitle}: {ex.Message}");
                        }
                        retries++;
                    }

                    if (json != null)
                    {
                        bool isValid = false;
                        try
                        {
                            if (type == "anime") isValid = JsonSerializer.Deserialize<JikanResponse<AnimeFull>>(json)?.Data != null;
                            else if (type == "manga") isValid = JsonSerializer.Deserialize<JikanResponse<MangaFull>>(json)?.Data != null;
                        }
                        catch { }

                        if (isValid)
                        {
                            try
                            {
                                await File.WriteAllTextAsync(cachePath, json, ct);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[DEBUG_LOG] Error writing cache file {cachePath}: {ex.Message}");
                            }
                        }
                        else
                        {
                            json = null;
                        }
                    }
                    else
                    {
                        node.ErrorMessage = "Failed to fetch data after multiple attempts.";
                    }
                }

                if (json != null)
                {
                    bool isDataValid = false;
                    if (type == "anime")
                    {
                        var result = JsonSerializer.Deserialize<JikanResponse<AnimeFull>>(json);
                        if (result?.Data != null)
                        {
                            PopulateNode(node, result.Data);

                            if ((DateTime.Now - lastStatusTime).TotalMilliseconds > 100)
                            {
                                onStatusUpdate?.Invoke($"Fetching {node.Title}...");
                                lastStatusTime = DateTime.Now;
                            }

                            node.SourceType = result.Data.Type;
                            relations = result.Data.Relations;
                            isDataValid = true;
                        }
                        else { node.ErrorMessage = "Malformed API response (Anime)"; }
                    }
                    else if (type == "manga")
                    {
                        var result = JsonSerializer.Deserialize<JikanResponse<MangaFull>>(json);
                        if (result?.Data != null)
                        {
                            PopulateNode(node, result.Data);

                            if ((DateTime.Now - lastStatusTime).TotalMilliseconds > 100)
                            {
                                onStatusUpdate?.Invoke($"Fetching {node.Title}...");
                                lastStatusTime = DateTime.Now;
                            }

                            node.SourceType = result.Data.Type;
                            relations = result.Data.Relations;
                            isDataValid = true;
                        }
                        else { node.ErrorMessage = "Malformed API response (Manga)"; }
                    }

                    if (!isDataValid && File.Exists(cachePath))
                    {
                        try { File.Delete(cachePath); } catch { }
                    }
                }
                else if (node.ErrorMessage == null)
                {
                    node.ErrorMessage = $"Failed to fetch data or not found ({type})";
                }

                onNodeFetched?.Invoke(node);

                if (relations != null)
                {
                    foreach (var rel in relations)
                    {
                        string relType = rel.RelationType;
                        if (MALSpiderConstants.ExcludedRelationTypes.Contains(relType)) continue;

                        foreach (var entry in rel.Entry)
                        {
                            int targetMalId = entry.MalId;
                            string targetType = entry.Type.ToLower();
                            if (targetType != "anime" && targetType != "manga") continue;

                            string targetKey = $"{targetType}_{targetMalId}";

                            if (discovered.Add(targetKey))
                            {
                                _lastTotalDiscovered++;
                                queue.Enqueue((targetMalId, targetType, entry.Name));
                            }

                            visited.TryGetValue(targetKey, out var targetNode);

                            if (targetNode != null)
                            {
                                if (!node.Relations.Any(r => r.Target == targetNode))
                                {
                                    string relationType = relType;
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

                                    node.Relations.Add(new EntryRelation
                                    {
                                        RelationType = relationType,
                                        Target = targetNode
                                    });

                                    if (!targetNode.Relations.Any(r => r.Target == node))
                                    {
                                        string inferredBackType = Graph.GraphConnectivity.InvertRelationType(relationType);
                                        targetNode.Relations.Add(new EntryRelation
                                        {
                                            RelationType = inferredBackType,
                                            Target = node
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return rootNode;
        }

        public async Task<bool> RefreshNode(EntryNode node, Action<string> onStatusUpdate = null, Action<EntryNode> onNodeFetched = null, CancellationToken ct = default)
        {
            string key = $"{node.Type}_{node.MalId}";
            string cachePath = Path.Combine(MALSpiderConstants.CacheDirectory, $"{key}.json");
            if (File.Exists(cachePath)) File.Delete(cachePath);

            // Also delete image cache
            string imgDir = Path.Combine(MALSpiderConstants.CacheDirectory, "images");
            if (Directory.Exists(imgDir))
            {
                var files = Directory.GetFiles(imgDir, $"{key}.*");
                foreach (var file in files)
                {
                    File.Delete(file);
                }
            }

            node.ErrorMessage = null;
            node.IsRetrying = true;

            // Remove from visited so TraverseIterative doesn't just skip it
            _lastVisited.Remove(key);

            // Also remove from discovered to allow re-queueing
            _lastDiscovered.Remove(key);
            _lastTotalDiscovered--;

            // Re-run TraverseIterative for this node to fetch its data and relations
            var resultNode = await TraverseIterative(node.MalId, node.Type, _lastVisited, _lastDiscovered,
                s => onStatusUpdate?.Invoke($"{_lastVisited.Count}/{_lastTotalDiscovered}: {s}"),
                onNodeFetched, node.Title, ct);

            node.IsRetrying = false;
            return string.IsNullOrEmpty(node.ErrorMessage);
        }

        private void PopulateNode(EntryNode node, Anime details)
        {
            if (details == null) return;
            node.TitleEnglish = details.TitleEnglish;
            node.TitleJapanese = details.TitleJapanese;
            node.TitleRomaji = details.Title;
            node.Title = GetPreferredTitle(node.TitleEnglish, details.Title, node.TitleJapanese);
            node.ImageUrl = details.Images?.Jpg?.LargeImageUrl ?? details.Images?.Jpg?.ImageUrl;
            node.MalUrl = details.Url;
            node.Synopsis = details.Synopsis;
            node.ReleaseDate = details.Aired?.From;
            _ = LoadImageAsync(node);
        }

        private void PopulateNode(EntryNode node, Manga details)
        {
            if (details == null) return;
            node.TitleEnglish = details.TitleEnglish;
            node.TitleJapanese = details.TitleJapanese;
            node.TitleRomaji = details.Title;
            node.Title = GetPreferredTitle(node.TitleEnglish, details.Title, node.TitleJapanese);
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

        private async Task<(int MalId, string Type, string Title)?> Search(string name, CancellationToken ct = default)
        {
            var json = await GetAsyncWithRateLimit($"{BaseUrl}/anime?q={Uri.EscapeDataString(name)}&limit=1", ct);
            if (json != null)
            {
                var result = JsonSerializer.Deserialize<JikanResponse<List<Anime>>>(json);
                if (result?.Data?.Count > 0)
                {
                    var a = result.Data[0];
                    string title = GetPreferredTitle(a.TitleEnglish, a.Title, a.TitleJapanese);
                    return (a.MalId, "anime", title);
                }
            }
            return null;
        }

        private string GetPreferredTitle(string english, string romaji, string japanese)
        {
            if (!string.IsNullOrWhiteSpace(english)) return english;
            if (!string.IsNullOrWhiteSpace(romaji)) return romaji;
            return japanese;
        }
    }
}
