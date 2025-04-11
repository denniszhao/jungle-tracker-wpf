using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;

namespace JungleTracker
{
    /// <summary>
    /// Manages the state, position, and animation of the ChampionPortraitControl.
    /// </summary>
    public class PortraitManager
    {
        // --- Constants ---
        private const int FADE_DURATION_MS = 10000;
        private const double FINAL_FADE_OPACITY = 0.3;

        // --- Fields ---
        private readonly ChampionPortrait _portraitControl;
        private Storyboard? _fadeOutStoryboard;
        private bool _isPortraitFadingOut = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="PortraitManager"/> class.
        /// </summary>
        /// <param name="portraitControl">The ChampionPortraitControl UI element to manage.</param>
        public PortraitManager(ChampionPortrait portraitControl)
        {
            _portraitControl = portraitControl ?? throw new ArgumentNullException(nameof(portraitControl));
        }

        /// <summary>
        /// Gets whether the portrait is currently fading out or has completed fading out.
        /// </summary>
        public bool IsFadingOut => _isPortraitFadingOut;

        /// <summary>
        /// Updates the position of the champion portrait based on its detected location,
        /// considering overlay and capture sizes to calculate padding.
        /// </summary>
        /// <param name="matchLocation">The location where the champion was detected within the captured minimap area.</param>
        /// <param name="overlaySize">The total size of the overlay window.</param>
        /// <param name="captureSize">The size of the actual captured minimap area.</param>
        public void UpdatePosition(System.Drawing.Point matchLocation, double overlaySize, double captureSize)
        {
            if (_portraitControl == null) return;

            double padding = (overlaySize > captureSize && captureSize > 0) ? (overlaySize - captureSize) / 2.0 : 0;
            double adjustedMatchX = matchLocation.X + padding;
            double adjustedMatchY = matchLocation.Y + padding;

            double controlWidth = _portraitControl.ActualWidth > 0 ? _portraitControl.ActualWidth : _portraitControl.Width;
            double controlHeight = _portraitControl.ActualHeight > 0 ? _portraitControl.ActualHeight : _portraitControl.Height;

            // Fallback logic for dimensions
            if (controlWidth <= 0 || controlHeight <= 0)
            {
                if (_portraitControl.RenderSize.Width > 0 && _portraitControl.RenderSize.Height > 0)
                {
                    controlWidth = _portraitControl.RenderSize.Width;
                    controlHeight = _portraitControl.RenderSize.Height;
                }
                else
                {
                    // Use defined Width/Height as last resort
                    controlWidth = _portraitControl.Width;
                    controlHeight = _portraitControl.Height;
                    if (controlWidth <= 0 || controlHeight <= 0)
                    {
                         // Cannot position if size is invalid
                         Debug.WriteLine($"[PortraitManager] Error: ChampionPortraitControl Width/Height are invalid ({controlWidth}x{controlHeight}). Cannot update position.");
                         return;
                    }
                     Debug.WriteLine($"[PortraitManager] Warning: Cannot determine valid dimensions for ChampionPortraitControl in UpdatePosition. Using default size ({_portraitControl.Width}x{_portraitControl.Height}).");
                }
            }

            double x = adjustedMatchX - (controlWidth / 2);
            double y = adjustedMatchY - (controlHeight / 2);

            // Ensure staying within overlay bounds
            x = Math.Max(0, Math.Min(x, overlaySize - controlWidth));
            y = Math.Max(0, Math.Min(y, overlaySize - controlHeight));

            _portraitControl.Margin = new Thickness(x, y, 0, 0);
        }

        /// <summary>
        /// Shows the portrait in the corresponding enemy team's base location.
        /// </summary>
        /// <param name="enemyTeam">The team of the enemy jungler ("CHAOS" for red side, "ORDER" for blue side).</param>
        /// <param name="overlaySize">The size of the overlay window.</param>
        public void ShowAtBase(string enemyTeam, double overlaySize)
        {
            if (_portraitControl == null) return;

            // Stop any existing fade animation
            StopFade();

            // Get the size of the portrait
            double controlWidth = _portraitControl.ActualWidth > 0 ? _portraitControl.ActualWidth : _portraitControl.Width;
            double controlHeight = _portraitControl.ActualHeight > 0 ? _portraitControl.ActualHeight : _portraitControl.Height;

            // Fallback logic for dimensions (same as in UpdatePosition)
            if (controlWidth <= 0 || controlHeight <= 0)
            {
                if (_portraitControl.RenderSize.Width > 0 && _portraitControl.RenderSize.Height > 0)
                {
                    controlWidth = _portraitControl.RenderSize.Width;
                    controlHeight = _portraitControl.RenderSize.Height;
                }
                else
                {
                    controlWidth = _portraitControl.Width;
                    controlHeight = _portraitControl.Height;
                    if (controlWidth <= 0 || controlHeight <= 0)
                    {
                        Debug.WriteLine($"[PortraitManager] Error: ChampionPortraitControl Width/Height are invalid ({controlWidth}x{controlHeight}). Cannot position in base.");
                        return;
                    }
                }
            }
            
            // Calculate position based on team
            double padding = 10; // Add a small padding from the edge
            double x, y;
            
            if (enemyTeam.Equals("CHAOS", StringComparison.OrdinalIgnoreCase))
            {
                // Red team (CHAOS) is top-right
                x = overlaySize - controlWidth - padding;
                y = padding; // Changed from bottom-right to top-right
            }
            else
            {
                // Blue team (ORDER) is bottom-left
                x = padding;
                y = overlaySize - controlHeight - padding; // Changed from top-left to bottom-left
            }
            
            // Set position
            _portraitControl.Margin = new Thickness(x, y, 0, 0);
            
            // Show portrait at full opacity
            _portraitControl.Opacity = 1.0;
            _portraitControl.Visibility = Visibility.Visible;
            
            // Reset animation state
            _isPortraitFadingOut = false;
            
            Debug.WriteLine($"[PortraitManager] Portrait shown at {enemyTeam} base position ({x}, {y})");
        }

        /// <summary>
        /// Makes the portrait visible at full opacity and starts the fade-out animation.
        /// If already fading, this method does nothing.
        /// </summary>
        public void StartFadeOut()
        {
            if (_portraitControl == null || _isPortraitFadingOut) return;

            _isPortraitFadingOut = true; // Set the flag *before* starting
            _portraitControl.Visibility = Visibility.Visible;
            _portraitControl.Opacity = 1.0;

            _fadeOutStoryboard = new Storyboard();
            DoubleAnimation fadeOutAnimation = new DoubleAnimation
            {
                From = 1.0,
                To = FINAL_FADE_OPACITY,
                Duration = new Duration(TimeSpan.FromMilliseconds(FADE_DURATION_MS)),
                // FillBehavior defaults to HoldEnd
            };

            Storyboard.SetTarget(fadeOutAnimation, _portraitControl);
            Storyboard.SetTargetProperty(fadeOutAnimation, new PropertyPath(UIElement.OpacityProperty)); // Use UIElement

            fadeOutAnimation.Completed += (s, e) => {
                 if (_isPortraitFadingOut) // Check if still supposed to be fading
                 {
                    _fadeOutStoryboard = null; // Animation is done, clear reference
                    Debug.WriteLine($"[PortraitManager] Fade out completed. Opacity held at {FINAL_FADE_OPACITY}.");
                 }
                 else
                 {
                    Debug.WriteLine("[PortraitManager] Fade out completed, but StopFade was called before completion.");
                 }
            };

            _fadeOutStoryboard.Children.Add(fadeOutAnimation);
            _fadeOutStoryboard.Begin(_portraitControl, HandoffBehavior.SnapshotAndReplace, true);
            Debug.WriteLine("[PortraitManager] Started fade out animation.");
        }

        /// <summary>
        /// Stops any active fade-out animation and resets the fading flag.
        /// Does not change the portrait's visibility or opacity directly.
        /// </summary>
        public void StopFade()
        {
             if (_isPortraitFadingOut)
             {
                 if (_fadeOutStoryboard != null)
                 {
                     _fadeOutStoryboard.Stop(_portraitControl);
                     _fadeOutStoryboard = null;
                     Debug.WriteLine("[PortraitManager] Stopped active fade out animation.");
                 } else {
                      Debug.WriteLine("[PortraitManager] Stopping fade state (animation likely already completed).");
                 }
                 _isPortraitFadingOut = false;
             }
        }

        /// <summary>
        /// Immediately hides the portrait by setting its visibility to Collapsed and opacity to 0.
        /// Also stops any ongoing fade animation.
        /// </summary>
        public void Hide()
        {
             StopFade(); // Ensure animation is stopped first
             if (_portraitControl != null)
             {
                _portraitControl.Visibility = Visibility.Collapsed;
                _portraitControl.Opacity = 0.0;
             }
             Debug.WriteLine("[PortraitManager] Portrait hidden immediately.");
        }

         /// <summary>
        /// Resets the portrait manager state, stopping fades and hiding the control.
        /// Useful when jungler info changes or game closes.
        /// </summary>
        public void Reset()
        {
            Hide(); // Hide implicitly stops fade and sets visibility/opacity
        }
    }
} 