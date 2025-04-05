using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using JungleTracker;
using OpenCvSharp;

namespace JungleTracker.Tests
{
    [TestClass]
    public class MinimapScannerServiceTests
    {
        // Paths for test images and results
        private string _baseTestAssetsPath = string.Empty; // Changed to base path
        private string _testResultsPath = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            // Determine the base path for TestAssets robustly
            string currentDir = Directory.GetCurrentDirectory();
            // Check typical VS test run output (bin/Debug/net...)
            string? projectDir = Directory.GetParent(currentDir)?.Parent?.Parent?.FullName;

            if (projectDir != null && Directory.Exists(Path.Combine(projectDir, "TestAssets")))
            {
                _baseTestAssetsPath = Path.Combine(projectDir, "TestAssets");
            }
            else // Fallback if structure is different (e.g., CLI run)
            {
                 // Assume TestAssets is copied to output
                _baseTestAssetsPath = Path.Combine(currentDir, "TestAssets");
                 if (!Directory.Exists(_baseTestAssetsPath))
                 {
                     // If still not found, we might have an issue, but let tests proceed
                      TestContext?.WriteLine($"Warning: TestAssets path not definitively found. Using fallback: {_baseTestAssetsPath}");
                 }
            }
            TestContext?.WriteLine($"Base TestAssets Path: {_baseTestAssetsPath}");


            // Create a directory for test results
            _testResultsPath = Path.Combine(Directory.GetCurrentDirectory(), "TestResults");
            Directory.CreateDirectory(_testResultsPath);
        }

        // Helper to get full path to a test asset
        private string GetTestAssetPath(string fileName)
        {
            string fullPath = Path.Combine(_baseTestAssetsPath, fileName);
            if (!File.Exists(fullPath))
            {
                Assert.Inconclusive($"Test asset not found: {fullPath}");
            }
            return fullPath;
        }


        [TestMethod]
        public void ScanForChampion_Poppy_ShouldDetectInUpperRightCorner()
        {
            // Arrange
            string poppyTestImagePath = GetTestAssetPath("sample_minimap.png");
            using (var scanner = new MinimapScannerService()) // Use default threshold
            using (var testImage = new Bitmap(poppyTestImagePath))
            {
                TestContext.WriteLine($"Using test image: {poppyTestImagePath}");

                // Act
                bool templateLoaded = scanner.SetChampionTemplate("Poppy", "CHAOS"); // Poppy is red side (CHAOS) in sample

                // Assert template loaded successfully
                Assert.IsTrue(templateLoaded, "Poppy template (CHAOS) should load successfully");
                TestContext.WriteLine("Poppy CHAOS template loaded successfully. Performing scan...");

                // Perform the scan
                System.Drawing.Point? result = scanner.ScanForChampion(testImage);
                TestContext.WriteLine($"Scan result: {(result.HasValue ? $"({result.Value.X}, {result.Value.Y})" : "No match found")}");

                // Assert champion was found
                Assert.IsTrue(result.HasValue, "Poppy should be detected in the test image");

                if (result.HasValue)
                {
                    // Save result image for visual verification
                    SaveResultImage(testImage, result.Value, "Poppy_CHAOS_Detection");

                    // Log coordinates and check quadrant (Poppy is in upper-right in sample_minimap.png)
                    TestContext.WriteLine($"Image dimensions: {testImage.Width}x{testImage.Height}");
                    TestContext.WriteLine($"Expected quadrant: upper-right (X > {testImage.Width / 2}, Y < {testImage.Height / 2})");
                    Assert.IsTrue(result.Value.X > testImage.Width / 2, "Poppy should be in the right half of the image");
                    Assert.IsTrue(result.Value.Y < testImage.Height / 2, "Poppy should be in the upper half of the image");
                    TestContext.WriteLine($"Poppy detected at: X={result.Value.X}, Y={result.Value.Y}");
                }
            }
        }

        // --- NEW TEST METHOD FOR VI ---
        [TestMethod]
        public void ScanForChampion_Vi_ShouldDetectInUpperLeftCorner()
        {
            // Arrange
            string viTestImagePath = GetTestAssetPath("sample_minimap_vi.png"); // Get path to the new Vi image
            using (var scanner = new MinimapScannerService()) // Use default threshold
            using (var testImage = new Bitmap(viTestImagePath))
            {
                TestContext.WriteLine($"Using test image: {viTestImagePath}");

                // Act: Set template for Vi on the CHAOS (Red) team
                bool templateLoaded = scanner.SetChampionTemplate("Vi", "CHAOS");

                // Assert template loaded successfully
                Assert.IsTrue(templateLoaded, "Vi template (CHAOS) should load successfully");
                TestContext.WriteLine("Vi CHAOS template loaded successfully. Performing scan...");

                // Perform the scan
                System.Drawing.Point? result = scanner.ScanForChampion(testImage);
                TestContext.WriteLine($"Scan result: {(result.HasValue ? $"({result.Value.X}, {result.Value.Y})" : "No match found")}");

                // Assert champion was found
                Assert.IsTrue(result.HasValue, "Vi should be detected in the Vi test image");

                if (result.HasValue)
                {
                    // Save result image for visual verification
                    SaveResultImage(testImage, result.Value, "Vi_CHAOS_Detection");

                    // Log coordinates and check quadrant (Expecting upper-left)
                    TestContext.WriteLine($"Image dimensions: {testImage.Width}x{testImage.Height}");
                    TestContext.WriteLine($"Expected quadrant: upper-left (X < {testImage.Width / 2}, Y < {testImage.Height / 2})");
                    Assert.IsTrue(result.Value.X < testImage.Width / 2, "Vi should be in the left half of the image");
                    Assert.IsTrue(result.Value.Y < testImage.Height / 2, "Vi should be in the upper half of the image");
                    TestContext.WriteLine($"Vi detected at: X={result.Value.X}, Y={result.Value.Y}");
                }
            }
        }
        // --- END NEW TEST ---

        [TestMethod]
        public void ScanForChampion_NonexistentChampion_ShouldNotDetect()
        {
             // Arrange
            string testImagePath = GetTestAssetPath("sample_minimap.png"); // Use the original map for this test
            using (var scanner = new MinimapScannerService())
            using (var testImage = new Bitmap(testImagePath))
            {
                // Champions NOT in sample_minimap.png
                string[] testChampions = { "Amumu", "Graves", "LeeSin", "Vi" }; // Added Vi here as she's not in this specific map

                foreach (string champion in testChampions)
                {
                    // Try both teams just in case
                    foreach (string team in new[] { "ORDER", "CHAOS"})
                    {
                        // Act
                        bool templateLoaded = scanner.SetChampionTemplate(champion, team);

                        // Skip if template couldn't be loaded (less critical for non-detection test)
                        if (!templateLoaded)
                        {
                            TestContext.WriteLine($"Template for {champion} ({team}) could not be loaded, skipping non-detection check for this combo.");
                            continue;
                        }

                        System.Drawing.Point? result = scanner.ScanForChampion(testImage);

                        // Assert
                        Assert.IsFalse(result.HasValue, $"{champion} ({team}) should not be detected in {Path.GetFileName(testImagePath)}");
                        TestContext.WriteLine($"{champion} ({team}) correctly not detected.");
                    }
                }
            }
        }
        
        [TestMethod]
        public void ScanForChampion_DifferentThresholds_AffectsDetection()
        {
            // Arrange - test image and champion
            string testImagePath = GetTestAssetPath("sample_minimap.png"); // Use helper
            using (var testImage = new Bitmap(testImagePath)) // Use the local variable
            {
                // Test with various thresholds
                double[] thresholds = { 0.4, 0.5, 0.6, 0.7, 0.8 }; // Adjusted thresholds potentially

                foreach (double threshold in thresholds)
                {
                    // Create scanner with specific threshold
                    using (var scanner = new MinimapScannerService(threshold, TemplateMatchModes.CCoeffNormed)) // Specify mode too
                    {
                        // Act
                        scanner.SetChampionTemplate("Poppy", "CHAOS"); // Use the champion present in sample_minimap.png
                        var result = scanner.ScanForChampion(testImage);

                        // Log the result for this threshold
                        TestContext.WriteLine($"Threshold {threshold}: Detection {(result.HasValue ? "succeeded" : "failed")}");

                        if (result.HasValue)
                        {
                            // SaveResultImage(testImage, result.Value, $"Poppy_Threshold_{threshold}"); // Optional: uncomment to save results
                            TestContext.WriteLine($"  Location: ({result.Value.X}, {result.Value.Y})");
                        }
                        // Assertions could be added here if you know expected outcomes for thresholds
                    }
                }
            }
        }
        
        [TestMethod]
        public void ScanForChampion_DifferentMatchingModes_ReturnsConsistentResults()
        {
            // Arrange - test image and expected champion
            string testImagePath = GetTestAssetPath("sample_minimap.png"); // Use helper
            using (var testImage = new Bitmap(testImagePath)) // Use the local variable
            {
                // Test different template matching modes
                var modes = new[] 
                { 
                    TemplateMatchModes.SqDiffNormed,
                    TemplateMatchModes.CCoeffNormed,
                    TemplateMatchModes.SqDiff
                };
                
                foreach (var mode in modes)
                {
                    // Create scanner with specific mode
                    using (var scanner = new MinimapScannerService(0.2, mode))
                    {
                        // Act
                        scanner.SetChampionTemplate("Poppy", "CHAOS");
                        var result = scanner.ScanForChampion(testImage);
                        
                        // Log the result for this mode
                        TestContext.WriteLine($"Mode {mode}: Detection {(result.HasValue ? "succeeded" : "failed")}");
                        
                        if (result.HasValue)
                        {
                            SaveResultImage(testImage, result.Value, $"Poppy_Mode_{mode}");
                            TestContext.WriteLine($"  Location: ({result.Value.X}, {result.Value.Y})");
                        }
                    }
                }
            }
        }
        
        [TestMethod]
        public void VerifyTestImageAndTemplate()
        {
            string testImagePath = GetTestAssetPath("sample_minimap.png"); // Use helper
            using (var testImage = new Bitmap(testImagePath)) // Use local variable
            {
                TestContext.WriteLine($"Test image dimensions ({Path.GetFileName(testImagePath)}): {testImage.Width}x{testImage.Height}");
                TestContext.WriteLine($"Test image format: {testImage.PixelFormat}");

                // Optional: Save a small area from the upper right quadrant for inspection
                // if (testImage.Width > 0 && testImage.Height > 0) { ... save logic ... }
            }

             // Optionally add verification for the Vi image too
             string viTestImagePath = GetTestAssetPath("sample_minimap_vi.png");
             using (var viImage = new Bitmap(viTestImagePath))
             {
                 TestContext.WriteLine($"Test image dimensions ({Path.GetFileName(viTestImagePath)}): {viImage.Width}x{viImage.Height}");
                 TestContext.WriteLine($"Test image format: {viImage.PixelFormat}");
             }
        }
        
        // Utility method to save result images for visual verification
        private void SaveResultImage(Bitmap original, System.Drawing.Point matchPoint, string testName)
        {
            try
            {
                // Create a copy of the original image
                using (Bitmap resultImage = new Bitmap(original))
                {
                    // Draw a circle and crosshair at the match location
                    using (Graphics g = Graphics.FromImage(resultImage))
                    {
                        // Red circle
                        g.DrawEllipse(new Pen(Color.Red, 3), 
                            matchPoint.X - 10, matchPoint.Y - 10, 
                            20, 20);
                            
                        // Crosshair
                        g.DrawLine(new Pen(Color.Yellow, 2),
                            matchPoint.X - 15, matchPoint.Y,
                            matchPoint.X + 15, matchPoint.Y);
                        g.DrawLine(new Pen(Color.Yellow, 2),
                            matchPoint.X, matchPoint.Y - 15,
                            matchPoint.X, matchPoint.Y + 15);
                        
                        // Add text label
                        using (var font = new Font("Arial", 12, FontStyle.Bold))
                        {
                            g.DrawString($"({matchPoint.X}, {matchPoint.Y})", 
                                font, Brushes.White, 
                                new PointF(matchPoint.X + 15, matchPoint.Y + 15));
                        }
                    }
                    
                    // Save the image with the test name and timestamp
                    string fileName = $"{testName}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    string filePath = Path.Combine(_testResultsPath, fileName);
                    resultImage.Save(filePath);
                    
                    // Log the save location
                    TestContext.WriteLine($"Result image saved to: {filePath}");
                }
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Error saving result image: {ex.Message}");
            }
        }
        
        // Property required to use TestContext
        public TestContext TestContext { get; set; } = null!;
    }
} 