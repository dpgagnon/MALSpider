using System.Windows.Media;

namespace MALSpider
{
    public static class MALSpiderConstants
    {
        // --- Graph Layout Constants ---
        /// <summary>
        /// Vertical pixels per compressed year.
        /// Gaps between consecutive nodes are capped at TimeCompressionThresholdDays.
        /// </summary>
        public const double VerticalSpacing = 500;

        /// <summary>
        /// Minimum horizontal gap between lanes.
        /// </summary>
        public const double HorizontalGap = 100;

        /// <summary>
        /// Minimum vertical gap between nodes in the same lane to avoid overlap.
        /// </summary>
        public const double VerticalGap = 50;

        /// <summary>
        /// Width of a single node box.
        /// </summary>
        public const double NodeWidth = 220;

        /// <summary>
        /// Height of a single node box.
        /// </summary>
        public const double NodeHeight = 200;

        /// <summary>
        /// Horizontal spacing between clusters within the same lane.
        /// </summary>
        public const double ClusterHorizontalSpacing = 20;

        /// <summary>
        /// Detection padding for node collisions.
        /// </summary>
        public const double CollisionPadding = 5;

        /// <summary>
        /// Increment for resolving Y collisions.
        /// </summary>
        public const double YCollisionResolutionIncrement = 10;

        /// <summary>
        /// Margin added to the right side of the canvas.
        /// </summary>
        public const double CanvasWidthMargin = 40;

        /// <summary>
        /// Extra padding on the left to allow panning beyond the first lane.
        /// </summary>
        public const double GraphPaddingX = 10000;

        /// <summary>
        /// Extra padding on the top to allow panning above the graph.
        /// </summary>
        public const double GraphPaddingY = 10000;

        /// <summary>
        /// Margin added to the bottom of the canvas.
        /// </summary>
        public const double CanvasHeightMargin = 100;

        /// <summary>
        /// Max number of connections for a node to be considered "closely related" for clustering.
        /// </summary>
        public const int ClusteringThreshold = 2;

        // --- Connection Logic Constants ---
        /// <summary>
        /// Vertical distance threshold to switch to side-based anchors.
        /// (Source Bottom) - (Destination Top) > SideAnchorThreshold
        /// </summary>
        public const double SideAnchorThreshold = NodeHeight * 0.5;

        /// <summary>
        /// Horizontal distance threshold to consider nodes in different columns within the same lane.
        /// </summary>
        public const double ColumnDetectionThreshold = 10;

        // --- Visual Styling - Colors ---
        public static readonly Color PrimaryAccentColor = Color.FromRgb(187, 134, 252); // BB86FC
        public static readonly Color SecondaryAccentColor = Color.FromRgb(55, 0, 179);  // 3700B3
        public static readonly Color BackgroundDarkColor = Color.FromRgb(18, 18, 18);
        public static readonly Color SurfaceDarkColor = Color.FromRgb(30, 30, 30);
        public static readonly Color LaneSeparatorColor = Color.FromRgb(51, 51, 51);

        public static readonly Color DownwardConnectionColor = Color.FromRgb(100, 100, 100);
        public static readonly Color HorizontalConnectionColor = Color.FromRgb(80, 80, 80);
        public static readonly Color ConnectionLabelBackgroundColor = Color.FromArgb(200, 20, 20, 20);
        public static readonly Color NodeBackgroundColor = Color.FromRgb(30, 30, 30);
        public static readonly Color NodeBorderColor = Color.FromRgb(64, 64, 64);
        public static readonly Color NodeErrorColor = Brushes.Red.Color;
        public static readonly Color NodeErrorTextColor = Color.FromRgb(255, 99, 71); // Tomato

        // --- Visual Styling - Sizes ---
        public const double ScrollBarSize = 12;
        public const double HeaderFontSize = 24;
        public const double ConnectionLabelFontSize = 10;
        public const double TimeAxisYearFontSize = 12;
        public const double TimeAxisMonthFontSize = 9;
        public const double NodeCornerRadius = 8;
        public const double NodePadding = 8;
        public const double NodeImageHeight = 90;
        public const double NodeTitleFontSize = 14;
        public const double NodeErrorFontSize = 10;
        public const double NodeSourceTypeFontSize = 10;
        public const double ToolTipMaxWidth = 400;
        public const double ToolTipFontSize = 14;

        // --- Jikan Service Constants ---
        public const string JikanBaseUrl = "https://api.jikan.moe/v4";
        public const int JikanRequestsPerSecond = 2;
        public const int JikanRequestsPerMinute = 120;
        public const int JikanRetryDelayMs = 2000;
        public const int MaxRetries = 3;

        // --- Carousel Constants ---
        public const int CarouselMaxItems = 20;
        public const double CarouselSpacing = 280;
        public const double CarouselLerpFactor = 0.1;
        public const double CarouselScaleMin = 0.6;
        public const double CarouselScaleMax = 1.0;
        public const double CarouselAlphaMin = 0.0;
        public const double CarouselAlphaMax = 1.0;

        // --- Time Compression Constants ---
        /// <summary>
        /// Threshold in days to consider a gap as "large" for compression.
        /// Gaps larger than this will be capped in the vertical layout.
        /// </summary>
        public const double TimeCompressionThresholdDays = 200;

        // --- Zoom Constants ---
        public const double MinZoom = 0.1;
        public const double MaxZoom = 2.0;
        public const double DefaultZoom = 1.0;

        // --- Other Constants ---
        public const string SettingsFile = "settings.json";
        public const string CacheDirectory = "cache";

        // --- Relation Filtering ---
        public static readonly string[] ExcludedRelationTypes = { "Other", "Character" };

        /// <summary>
        /// Relation types that should cause a horizontal shift instead of vertical alignment.
        /// Only "Sequel" (and implicitly "Prequel") relations usually stay vertical.
        /// </summary>
        public static readonly string[] HorizontalRelationTypes =
        {
            "Side story",
            "Spin-off",
            "Alternative version",
            "Alternative setting",
            "Other"
        };

        // --- DWM Constants for Dark Mode Title Bar ---
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    }
}
