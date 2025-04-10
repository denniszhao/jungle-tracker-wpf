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
        }

        private static void OnChampionInfoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ChampionPortrait portrait)
            {
                portrait.UpdatePortraitImage();
            }
        }

        private void UpdatePortraitImage()
        {
            if (string.IsNullOrEmpty(ChampionName) || string.IsNullOrEmpty(Team))
            {
                PortraitImage.Source = null;
                Debug.WriteLine($"[ChampionPortrait] Clearing image due to missing info (Champion: '{ChampionName}', Team: '{Team}')");
                return;
            }

            try
            {
                string championFileName = ChampionNameHelper.Normalize(ChampionName);

                string resourceFolder = Team.Equals("CHAOS", StringComparison.OrdinalIgnoreCase) ? "champions_altered_red" : "champions_altered_blue";
                string resourceName = $"JungleTracker.Assets.Champions.{resourceFolder}.{championFileName}.png";

                BitmapImage? bitmap = LoadBitmapImageFromResource(resourceName);
                PortraitImage.Source = bitmap;

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
                Debug.WriteLine($"[ChampionPortrait] Error loading champion portrait image for {ChampionName}: {ex.Message}");
                PortraitImage.Source = null;
            }
        }

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
                        return null;
                    }

                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = resourceStream;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChampionPortrait] Exception loading resource '{resourceName}': {ex.Message}");
                return null;
            }
        }
    }
}