using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace JungleTracker
{
    /// <summary>
    /// Manages the minimap zone polygons, including their visibility, highlighting, and transformations.
    /// </summary>
    public class ZoneManager
    {
        // --- Constants ---
        // Reference size used for zone polygon coordinates
        private const double ZONE_REFERENCE_SIZE = 510.0;

        // --- Fields ---
        private readonly List<Polygon> _allZonePolygons;
        private readonly ScaleTransform _zoneScaleTransform;
        private readonly TranslateTransform _zoneTranslateTransform;
        private readonly System.Windows.Media.Brush _defaultZoneFill;
        private readonly System.Windows.Media.Brush _highlightZoneFill;
        private readonly bool _isDebugMode;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZoneManager"/> class.
        /// </summary>
        /// <param name="allZonePolygons">The list of Polygon UI elements representing the zones.</param>
        /// <param name="zoneScaleTransform">The ScaleTransform applied to the zone canvas.</param>
        /// <param name="zoneTranslateTransform">The TranslateTransform applied to the zone canvas.</param>
        /// <param name="defaultZoneFill">The default brush for zone polygons.</param>
        /// <param name="highlightZoneFill">The brush used to highlight the active zone.</param>
        /// <param name="isDebugMode">Flag indicating if the application is running in debug mode.</param>
        public ZoneManager(
            List<Polygon> allZonePolygons,
            ScaleTransform zoneScaleTransform,
            TranslateTransform zoneTranslateTransform,
            System.Windows.Media.Brush defaultZoneFill,
            System.Windows.Media.Brush highlightZoneFill,
            bool isDebugMode)
        {
            _allZonePolygons = allZonePolygons ?? throw new ArgumentNullException(nameof(allZonePolygons));
            _zoneScaleTransform = zoneScaleTransform ?? throw new ArgumentNullException(nameof(zoneScaleTransform));
            _zoneTranslateTransform = zoneTranslateTransform ?? throw new ArgumentNullException(nameof(zoneTranslateTransform));
            _defaultZoneFill = defaultZoneFill ?? throw new ArgumentNullException(nameof(defaultZoneFill));
            _highlightZoneFill = highlightZoneFill ?? throw new ArgumentNullException(nameof(highlightZoneFill));
            _isDebugMode = isDebugMode;

            // Initial setup (like debug visibility) can be done here if needed
            // Or handled by the caller after construction.
             if (_isDebugMode)
             {
                 InitializeDebugVisuals();
             }
             else
             {
                 // Ensure all polygons start hidden in release mode
                 HideAll(); 
             }
        }
        
         /// <summary>
        /// Initializes the debug visuals for zones (makes them all visible with default fill).
        /// Should only be called when isDebugMode is true.
        /// </summary>
        private void InitializeDebugVisuals()
        {
             if (!_isDebugMode || _allZonePolygons == null) return;

             Debug.WriteLine("[ZoneManager DEBUG] Making all zone polygons visible in Constructor.");
             foreach (var polygon in _allZonePolygons)
             {
                 polygon.Visibility = Visibility.Visible;
                 polygon.Fill = _defaultZoneFill;
             }
        }

        /// <summary>
        /// Updates the visibility and fill of the zone polygons based on the last known location.
        /// Hides all polygons (or resets fill in debug), then highlights the one containing the location (if found).
        /// </summary>
        /// <param name="lastKnownLocation">The last known location of the champion within the captured minimap area.</param>
        /// <param name="captureSize">The current size of the captured minimap area.</param>
        public void UpdateHighlight(System.Drawing.Point lastKnownLocation, double captureSize)
        {
            HideAll(); // Start by resetting all fills (and hiding if not debug)

            double scaleFactor = captureSize > 0 ? captureSize / ZONE_REFERENCE_SIZE : 1.0;
            if (scaleFactor <= 0) return;

            System.Windows.Point referencePoint = new System.Windows.Point(
                lastKnownLocation.X / scaleFactor,
                lastKnownLocation.Y / scaleFactor
            );

            Polygon? foundPolygon = null;
            foreach (var polygon in _allZonePolygons)
            {
                if (polygon.RenderedGeometry != null && polygon.RenderedGeometry.FillContains(referencePoint))
                {
                    foundPolygon = polygon;
                    break;
                }
            }

            if (foundPolygon != null)
            {
                foundPolygon.Fill = _highlightZoneFill;
                if (!_isDebugMode)
                {
                    foundPolygon.Visibility = Visibility.Visible;
                     Debug.WriteLine($"[ZoneManager] Point {referencePoint} found in polygon {foundPolygon.Name}. Making it visible with highlight.");
                }
                else
                {
                    Debug.WriteLine($"[ZoneManager DEBUG] Point {referencePoint} found in polygon {foundPolygon.Name}. Applied highlight fill.");
                }
            }
            else
            {
                 Debug.WriteLine($"[ZoneManager] Point {referencePoint} not found in any defined zone polygon."); 
            }
        }

        /// <summary>
        /// Resets all zone polygons to their default fill color.
        /// If not in DEBUG_MODE, also sets their visibility to Collapsed.
        /// </summary>
        public void HideAll()
        {
            if (_allZonePolygons == null) return;

            foreach (var polygon in _allZonePolygons)
            {
                 polygon.Fill = _defaultZoneFill; 
                 if (!_isDebugMode)
                 {
                    polygon.Visibility = Visibility.Collapsed;
                 }
                 // In debug mode, they remain visible but are reset to default fill.
            }
        }

        /// <summary>
        /// Updates the RenderTransform (Scale and Translate) of the ZoneHighlightCanvas
        /// based on the current overlay and capture sizes.
        /// </summary>
        /// <param name="overlaySize">The total size of the overlay window.</param>
        /// <param name="captureSize">The size of the actual captured minimap area.</param>
        public void UpdateTransforms(double overlaySize, double captureSize)
        {
            if (captureSize <= 0 || _zoneScaleTransform == null || _zoneTranslateTransform == null) return;

            double scaleFactor = captureSize / ZONE_REFERENCE_SIZE;
            double padding = (overlaySize - captureSize) / 2.0;

            _zoneScaleTransform.ScaleX = scaleFactor;
            _zoneScaleTransform.ScaleY = scaleFactor;
            _zoneTranslateTransform.X = padding;
            _zoneTranslateTransform.Y = padding;

             Debug.WriteLine($"[ZoneManager] Updated Zone Transforms: Scale={scaleFactor:F2}, Padding={padding:F1}");
        }
    }
} 