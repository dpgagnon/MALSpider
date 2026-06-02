i am just messing around, never vibe coded before

i even vibe coded this README, would you look at that

**MALSpider**

**MALSpider** is a powerful WPF-based desktop application designed to visualize the complex web of relationships between anime and manga titles. By leveraging the **Jikan API (v4)**, it "spiders" through MyAnimeList data to build an interactive, time-scaled graph of sequels, prequels, adaptations, and side stories.

---

**✨ Key Features**

*   **🕸️ Intelligent Spider Crawling**: Start with a single anime/manga search or a MyAnimeList URL, and watch as the app automatically discovers and connects related entries.
*   **⏳ Time-Scaled Graph Layout**: Entries are positioned vertically based on their release dates. 
*   **📉 Smart Time Compression**: Long gaps between releases (e.g., a decade-long wait for a sequel) are automatically compressed to keep the graph readable and compact.
*   **🛣️ Multi-Lane Organization**: Clearly see different mediums in dedicated lanes:
    *   **Anime Lane**: TV Series, Movies, OVAs, Specials.
    *   **Manga Lane**: Manga, One-shots.
    *   **Light Novel Lane**: Light Novels, Novels.
*   **🔍 Interactive Navigation**: Smooth pan and zoom controls, subgraph isolation, and quick-focus features.
*   **💾 Local Caching**: Metadata and images are cached locally to reduce API calls and improve performance on repeat visits.
*   **🌙 Modern Dark UI**: A sleek, immersive dark theme designed for comfortable long-term use.

---

**🎮 Controls**

| Action | Control |
| :--- | :--- |
| **Pan Graph** | Left Mouse Button (LMB) Drag |
| **Zoom In/Out** | Ctrl + Mouse Wheel (or Status Bar Slider) |
| **View on MAL** | Left Mouse Button (LMB) Click on Node |
| **Focus Node** | Press `F` while hovering or selected |
| **Isolate Subgraph** | Ctrl + Left Mouse Button (LMB) Click |
| **Back to Main View** | "Back to Main" Button or Mouse Side Button 1 |
| **Reset Zoom** | "Reset" Button in Status Bar |

---

**🚀 Getting Started**

**Prerequisites**
*   Windows 10/11
*   .NET 6.0 Runtime (or higher)

**How to Use**
1.  **Search**: Enter the name of an anime/manga or paste a full MyAnimeList URL into the search box.
2.  **Crawl**: Click the **CRAWL** button. The app will begin fetching data from the Jikan API.
3.  **Explore**: Use the sidebar toggles to filter lanes, and use the mouse to navigate the generated graph.
4.  **Refresh**: If you want to force an update of the data, use the **Refresh** button to clear the cache for that specific entry.

---

**🛠️ Technical Details**

*   **Language**: C# / .NET
*   **Framework**: WPF (Windows Presentation Foundation)
*   **API**: [Jikan API v4](https://jikan.moe/) (Unofficial MyAnimeList API)
*   **Rate Limiting**: Built-in handling for Jikan's rate limits (2 requests/second) to ensure stability.
*   **Layout Engine**: Custom-built layout engine for handling collision resolution, lane-based clustering, and vertical time-mapping.

---

**📝 Note**
This project is an unofficial tool and is not affiliated with MyAnimeList.net. It is intended for personal use to help fans visualize series timelines and find related content.
