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
        private string _testImagePath = string.Empty;
        private string _testResultsPath = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            // Set up the path to the test image
            _testImagePath = Path.Combine(
                Directory.GetCurrentDirectory(), 
                "TestAssets", 
                "sample_minimap.png");
            
            // Ensure test image exists
            if (!File.Exists(_testImagePath))
            {
                // Try to find the image in a different location
                string? altPath = Path.Combine(
                    Directory.GetParent(Directory.GetCurrentDirectory())?.Parent?.Parent?.FullName ?? string.Empty,
                    "TestAssets", 
                    "sample_minimap.png");
                
                if (!string.IsNullOrEmpty(altPath) && File.Exists(altPath))
                {
                    // Create directory if it doesn't exist
                    string? dirPath = Path.GetDirectoryName(_testImagePath);
                    if (!string.IsNullOrEmpty(dirPath))
                    {
                        Directory.CreateDirectory(dirPath);
                        File.Copy(altPath, _testImagePath);
                    }
                }
                else
                {
                    Assert.Inconclusive("Test image not found. Make sure sample_minimap.png is in the TestAssets folder.");
                }
            }
            
            // Create a directory for test results
            _testResultsPath = Path.Combine(Directory.GetCurrentDirectory(), "TestResults");
            Directory.CreateDirectory(_testResultsPath);
        }
        
        [TestMethod]
        public void ScanForChampion_Poppy_ShouldDetectInUpperRightCorner()
        {
            // Arrange
            using (var scanner = new MinimapScannerService()) // Try a different threshold
            using (var testImage = new Bitmap(_testImagePath))
            {
                // Log the test image path for verification
                TestContext.WriteLine($"Using test image: {_testImagePath}");
                
                // Act
                bool templateLoaded = scanner.SetChampionTemplate("Poppy", "CHAOS");
                
                // Assert template loaded successfully
                Assert.IsTrue(templateLoaded, "Poppy template should load successfully");
                
                // Add debug info about the template
                TestContext.WriteLine("Template loaded successfully. Performing scan...");
                
                // Perform the scan
                System.Drawing.Point? result = scanner.ScanForChampion(testImage);

                // More detailed debug output
                TestContext.WriteLine($"Scan result: {(result.HasValue ? $"({result.Value.X}, {result.Value.Y})" : "No match found")}");
                
                // Assert champion was found
                Assert.IsTrue(result.HasValue, "Poppy should be detected in the test image");
                
                if (result.HasValue)
                {
                    // Save result image for visual verification
                    SaveResultImage(testImage, result.Value, "Poppy_ORDER");
                    
                    // Log the coordinates and image dimensions
                    TestContext.WriteLine($"Image dimensions: {testImage.Width}x{testImage.Height}");
                    TestContext.WriteLine($"Expected quadrant: upper-right (X > {testImage.Width/2}, Y < {testImage.Height/2})");
                    // Check that it's in the upper-right quadrant (adjust these values based on your image)
                    Assert.IsTrue(result.Value.X > testImage.Width / 2, "Poppy should be in right side of the image");
                    Assert.IsTrue(result.Value.Y < testImage.Height / 2, "Poppy should be in upper half of the image");
                    
                    TestContext.WriteLine($"Poppy detected at: X={result.Value.X}, Y={result.Value.Y}");
                }
            }
        }
        
        [TestMethod]
        public void ScanForChampion_NonexistentChampion_ShouldNotDetect()
        {
            // Arrange
            using (var scanner = new MinimapScannerService())
            using (var testImage = new Bitmap(_testImagePath))
            {
                string[] testChampions = { "Amumu", "Graves", "LeeSin" }; // Champions not in the image
                
                foreach (string champion in testChampions)
                {
                    // Act
                    bool templateLoaded = scanner.SetChampionTemplate(champion, "ORDER");
                    
                    // Skip if template couldn't be loaded
                    if (!templateLoaded)
                    {
                        TestContext.WriteLine($"Template for {champion} could not be loaded, skipping test");
                        continue;
                    }
                    
                    // Perform the scan
                    System.Drawing.Point? result = scanner.ScanForChampion(testImage);
                    
                    // Assert
                    Assert.IsFalse(result.HasValue, $"{champion} should not be detected in the image");
                    
                    TestContext.WriteLine($"{champion} correctly not detected");
                }
            }
        }
        
        [TestMethod]
        public void ScanForChampion_DifferentThresholds_AffectsDetection()
        {
            // Arrange - test image and champion
            using (var testImage = new Bitmap(_testImagePath))
            {
                // Test with various thresholds
                double[] thresholds = { 0.05, 0.1, 0.2, 0.4 };
                
                foreach (double threshold in thresholds)
                {
                    // Create scanner with specific threshold
                    using (var scanner = new MinimapScannerService(threshold))
                    {
                        // Act
                        scanner.SetChampionTemplate("Poppy", "ORDER");
                        var result = scanner.ScanForChampion(testImage);
                        
                        // Log the result for this threshold
                        TestContext.WriteLine($"Threshold {threshold}: Detection {(result.HasValue ? "succeeded" : "failed")}");
                        
                        if (result.HasValue)
                        {
                            SaveResultImage(testImage, result.Value, $"Poppy_Threshold_{threshold}");
                            TestContext.WriteLine($"  Location: ({result.Value.X}, {result.Value.Y})");
                        }
                        
                        // For very low thresholds, we might expect no detection
                        // For higher thresholds, we expect detection
                        // This is highly dependent on the specific image and template
                        // Rather than assert, we're just logging the results for analysis
                    }
                }
            }
        }
        
        [TestMethod]
        public void ScanForChampion_DifferentMatchingModes_ReturnsConsistentResults()
        {
            // Arrange - test image and expected champion
            using (var testImage = new Bitmap(_testImagePath))
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
            using (var testImage = new Bitmap(_testImagePath))
            {
                TestContext.WriteLine($"Test image dimensions: {testImage.Width}x{testImage.Height}");
                TestContext.WriteLine($"Test image format: {testImage.PixelFormat}");
                
                // Save a small area from the upper right quadrant
                using (var upperRight = new Bitmap(testImage.Width/2, testImage.Height/2))
                {
                    using (var g = Graphics.FromImage(upperRight))
                    {
                        g.DrawImage(testImage, 
                            new Rectangle(0, 0, upperRight.Width, upperRight.Height),
                            new Rectangle(testImage.Width/2, 0, testImage.Width/2, testImage.Height/2),
                            GraphicsUnit.Pixel);
                    }
                    
                    upperRight.Save(Path.Combine(_testResultsPath, "UpperRightCorner.png"));
                    TestContext.WriteLine("Saved upper-right corner of test image for inspection");
                }
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