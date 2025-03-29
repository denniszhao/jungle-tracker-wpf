using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.ComponentModel;
using WPFMessageBox = System.Windows.MessageBox;
using System.Diagnostics;

namespace JungleTracker
{
    public partial class MainWindow : Window
    {
        private OverlayWindow? _overlayWindow = null;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Ask for data consent on first load.
            var consentWindow = new ConsentWindow();
            bool? result = consentWindow.ShowDialog();
            if (result != true)
            {
                WPFMessageBox.Show("Consent not given. The application will now close.", "Consent Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                this.Close();
            }
        }

        private void MinimapScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ScaleValueTextBlock != null)
            {
                ScaleValueTextBlock.Text = ((int)e.NewValue).ToString();
            }
        }

        private void ToggleOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_overlayWindow == null)
            {
                // Only allow overlay if the game is running
                if (!IsLeagueGameRunning() && !IsRiotClientRunning())
                {
                    WPFMessageBox.Show("League of Legends is not running. Please start the game or client first.", 
                        "Game Not Running", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Create overlay window
                _overlayWindow = new OverlayWindow();
                _overlayWindow.Closed += OverlayWindow_Closed;

                // Configure and show overlay
                string location = ((ComboBoxItem)MinimapLocationComboBox.SelectedItem).Content.ToString() ?? "Bottom Right";
                int scale = (int)MinimapScaleSlider.Value;
                _overlayWindow.ShowOverlay(location, scale);
                
                ToggleOverlayButton.Content = "Close Overlay";
            }
            else
            {
                _overlayWindow.Close();
            }
        }

        // Check if the League game is running
        private bool IsLeagueGameRunning()
        {
            return Process.GetProcessesByName("League of Legends").Length > 0;
        }

        // Check if the Riot client is running
        private bool IsRiotClientRunning()
        {
            return Process.GetProcessesByName("RiotClientServices").Length > 0 || 
                   Process.GetProcessesByName("LeagueClient").Length > 0;
        }

        private void OverlayWindow_Closed(object? sender, EventArgs e)
        {
            _overlayWindow = null;
            ToggleOverlayButton.Content = "Open Overlay";
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            _overlayWindow?.Close();
        }
    }
} 