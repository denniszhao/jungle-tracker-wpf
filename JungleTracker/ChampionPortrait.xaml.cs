using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace JungleTracker
{
    public partial class ChampionPortrait : System.Windows.Controls.UserControl
    {
        // Dependency Property for ChampionName
        public static readonly DependencyProperty ChampionNameProperty =
            DependencyProperty.Register("ChampionName", typeof(string), typeof(ChampionPortrait),
                                        new PropertyMetadata(string.Empty, OnChampionInfoChanged));

        public string ChampionName
        {
            get { return (string)GetValue(ChampionNameProperty); }
            set { SetValue(ChampionNameProperty, value); }
        }

        // Dependency Property for Team
        public static readonly DependencyProperty TeamProperty =
            DependencyProperty.Register("Team", typeof(string), typeof(ChampionPortrait),
                                        new PropertyMetadata(string.Empty, OnChampionInfoChanged));

        public string Team
        {
            get { return (string)GetValue(TeamProperty); }
            set { SetValue(TeamProperty, value); }
        }

        // Fallback image for designer preview (optional)
        public BitmapImage? FallbackImage { get; set; }

        public ChampionPortrait()
        {
            InitializeComponent();
            // Example fallback image (replace with a generic icon if desired)
            // FallbackImage = LoadBitmapImageFromResource("JungleTracker.Assets.some_default_icon.png");
        }

        private static void OnChampionInfoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // When ChampionName or Team changes, update the image source
            if (d is ChampionPortrait portrait)
            {
                portrait.UpdatePortraitImage();
            }
        }

        private void UpdatePortraitImage()
        {
            // Basic validation
            if (string.IsNullOrEmpty(ChampionName) || string.IsNullOrEmpty(Team))
            {
                PortraitImage.Source = null;
                Debug.WriteLine($"[ChampionPortrait] Clearing image due to missing info (Champion: '{ChampionName}', Team: '{Team}')");
                return;
            }

            try
            {
                string championFileName = ChampionName;
                // Handle Wukong naming inconsistency if necessary
                if (championFileName == "Wukong") championFileName = "MonkeyKing";

                string resourceFolder = Team.Equals("CHAOS", StringComparison.OrdinalIgnoreCase) ? "champions_altered_red" : "champions_altered_blue";
                string resourceName = $"JungleTracker.Assets.Champions.{resourceFolder}.{championFileName}.png";

                BitmapImage? bitmap = LoadBitmapImageFromResource(resourceName);
                PortraitImage.Source = bitmap; // Set the image source

                if (bitmap == null)
                {
                    Debug.WriteLine($"[ChampionPortrait] Failed to load or find resource: {resourceName}");
                }
                else
                {
                    Debug.WriteLine($"[ChampionPortrait] Successfully loaded resource: {resourceName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChampionPortrait] Error loading champion portrait image: {ex.Message}");
                PortraitImage.Source = null; // Clear on error
            }
        }

        // Helper to load BitmapImage from embedded resources
        private BitmapImage? LoadBitmapImageFromResource(string resourceName)
        {
            ArgumentNullException.ThrowIfNull(resourceName);
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream? resourceStream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (resourceStream == null)
                    {
                        // Log missing resource if needed. UpdatePortraitImage handles the null return.
                        return null;
                    }

                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = resourceStream;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // Load fully into memory
                    bitmap.EndInit();
                    bitmap.Freeze(); // Freeze for performance and cross-thread access
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                // Log specific exception for loading this resource
                Debug.WriteLine($"[ChampionPortrait] Exception loading resource '{resourceName}': {ex.Message}");
                return null;
            }
        }
    }
}