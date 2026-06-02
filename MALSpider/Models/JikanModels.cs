using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MALSpider.Models
{
    public class JikanResponse<T>
    {
        [JsonPropertyName("data")]
        public T Data { get; set; }
    }

    public class Anime
    {
        [JsonPropertyName("mal_id")]
        public int MalId { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("images")]
        public Images Images { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("title_english")]
        public string TitleEnglish { get; set; }

        [JsonPropertyName("title_japanese")]
        public string TitleJapanese { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("aired")]
        public Aired Aired { get; set; }

        [JsonPropertyName("synopsis")]
        public string Synopsis { get; set; }
    }

    public class Manga
    {
        [JsonPropertyName("mal_id")]
        public int MalId { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("images")]
        public Images Images { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("title_english")]
        public string TitleEnglish { get; set; }

        [JsonPropertyName("title_japanese")]
        public string TitleJapanese { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("published")]
        public Published Published { get; set; }

        [JsonPropertyName("synopsis")]
        public string Synopsis { get; set; }
    }

    public class Aired
    {
        [JsonPropertyName("from")]
        public DateTime? From { get; set; }
    }

    public class Published
    {
        [JsonPropertyName("from")]
        public DateTime? From { get; set; }
    }

    public class Images
    {
        [JsonPropertyName("jpg")]
        public ImageType Jpg { get; set; }
    }

    public class AnimeFull : Anime
    {
        [JsonPropertyName("relations")]
        public List<Relation> Relations { get; set; }
    }

    public class MangaFull : Manga
    {
        [JsonPropertyName("relations")]
        public List<Relation> Relations { get; set; }
    }

    public class ImageType
    {
        [JsonPropertyName("image_url")]
        public string ImageUrl { get; set; }

        [JsonPropertyName("small_image_url")]
        public string SmallImageUrl { get; set; }

        [JsonPropertyName("large_image_url")]
        public string LargeImageUrl { get; set; }
    }

    public class Relation
    {
        [JsonPropertyName("relation")]
        public string RelationType { get; set; }

        [JsonPropertyName("entry")]
        public List<RelationEntry> Entry { get; set; }
    }

    public class RelationEntry
    {
        [JsonPropertyName("mal_id")]
        public int MalId { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }
    }

    public class EntryNode
    {
        public int MalId { get; set; }
        public string Type { get; set; } // "anime" or "manga"
        public string SourceType { get; set; } // e.g. "TV", "Movie", "Manga", "Light Novel"
        public string Title { get; set; }
        public string TitleEnglish { get; set; }
        public string TitleJapanese { get; set; }
        public string ImageUrl { get; set; }
        public string MalUrl { get; set; }
        public string Synopsis { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public bool IsInputRoot { get; set; }
        public List<EntryRelation> Relations { get; set; } = new List<EntryRelation>();
    }

    public class EntryRelation
    {
        public string RelationType { get; set; }
        public EntryNode Target { get; set; }
    }
}
