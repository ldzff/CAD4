using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
// Explicitly using System.Windows.Shapes.Shape to avoid ambiguity
// using System.Windows.Shapes; // This line can be removed if all Shape usages are qualified
using RobTeach.Views; // Added for DirectionIndicator
using Microsoft.Win32;
using RobTeach.Services;
using RobTeach.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using IxMilia.Dxf; // Required for DxfFile
using IxMilia.Dxf.Entities;
using System.Diagnostics; // Added for Debug.WriteLine
using System.IO;
using System.Text; // Added for Encoding

// using netDxf.Header; // No longer needed with IxMilia.Dxf
using System.Windows.Threading; // Was for optional Dispatcher.Invoke, now used.
// using System.Text.RegularExpressions; // Was for optional IP validation, not currently used.
using MahApps.Metro.Controls; // Added for MetroWindow

namespace RobTeach.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml. This is the main window of the RobTeach application,
    /// handling UI events, displaying CAD data, managing configurations, and initiating Modbus communication.
    /// </summary>
    public partial class MainWindow : MetroWindow // Changed from Window to MetroWindow
    {
        // Services used by the MainWindow
        private readonly CadService _cadService = new CadService();
        private readonly ConfigurationService _configService = new ConfigurationService();
        private readonly ModbusService _modbusService = new ModbusService();

        // Current state variables
        private DxfFile? _currentDxfDocument; // Holds the currently loaded DXF document object.
        private string? _currentDxfFilePath;      // Path to the currently loaded DXF file.
        private string? _currentLoadedConfigPath; // Path to the last successfully loaded configuration file.
        private Models.Configuration _currentConfiguration; // The active configuration, either loaded or built from selections.
        private bool isConfigurationDirty = false;
        private RobTeach.Models.Trajectory? _trajectoryInDetailView; // Made nullable

        // Collections for managing DXF entities and their WPF shape representations
        private readonly List<DxfEntity> _selectedDxfEntities = new List<DxfEntity>(); // Stores original DXF entities selected by the user.
        // Qualified System.Windows.Shapes.Shape for dictionary key
        private readonly Dictionary<System.Windows.Shapes.Shape, DxfEntity> _wpfShapeToDxfEntityMap = new Dictionary<System.Windows.Shapes.Shape, DxfEntity>(); // Changed to DxfEntity
        private readonly Dictionary<string, DxfEntity> _dxfEntityHandleMap = new Dictionary<string, DxfEntity>(); // Maps DXF entity handles to entities for quick lookup when loading configs.
        private readonly List<System.Windows.Shapes.Polyline> _trajectoryPreviewPolylines = new List<System.Windows.Shapes.Polyline>(); // Keeps track of trajectory preview polylines for easy removal.
        private List<DirectionIndicator> _directionIndicators; // Field for the direction indicator arrow
        private List<System.Windows.Controls.TextBlock> _orderNumberLabels = new List<System.Windows.Controls.TextBlock>();

        // Fields for CAD Canvas Zoom/Pan functionality
        private ScaleTransform _scaleTransform;         // Handles scaling (zoom) of the canvas content.
        private TranslateTransform _translateTransform; // Handles translation (pan) of the canvas content.
        private TransformGroup _transformGroup;         // Combines scale and translate transforms.
        private System.Windows.Point _panStartPoint;    // Qualified: Stores the starting point of a mouse pan operation.
        private bool _isPanning;                        // Flag indicating if a pan operation is currently in progress.
        private Rect _dxfBoundingBox = Rect.Empty;      // Stores the calculated bounding box of the entire loaded DXF document.

        // Fields for Marquee Selection
        private System.Windows.Shapes.Rectangle? selectionRectangleUI = null; // The visual rectangle for selection
        private System.Windows.Point selectionStartPoint;                   // Start point of the selection rectangle
        private bool isSelectingWithRect = false;                         // Flag indicating if marquee selection is active

        // Styling constants for visual feedback
        private static readonly Brush DefaultStrokeBrush = Brushes.LightGray; // Default color for CAD shapes.
        private static readonly Brush SelectedStrokeBrush = Brushes.DodgerBlue;   // Color for selected CAD shapes.
        private const double DefaultStrokeThickness = 2;                          // Default stroke thickness.
        private const double SelectedStrokeThickness = 3.5;                       // Thickness for selected shapes and trajectories.
        private const string TrajectoryPreviewTag = "TrajectoryPreview";          // Tag for identifying trajectory polylines on canvas (not actively used for removal yet).
        private const double TrajectoryPointResolutionAngle = 15.0; // Default resolution for discretizing arcs/circles.


        /// <summary>
        /// Initializes a new instance of the <see cref="MainWindow"/> class.
        /// Sets up default values, initializes transformation objects for the canvas,
        /// and attaches necessary mouse event handlers for canvas interaction.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            if (CadCanvas.Background == null) CadCanvas.Background = Brushes.LightGray; // Ensure canvas has a background for hit testing.
            _directionIndicators = new List<DirectionIndicator>();

            // Initialize product name with a timestamp to ensure uniqueness for new configurations.
            ProductNameTextBox.Text = $"Product_{DateTime.Now:yyyyMMddHHmmss}";
            _currentConfiguration = new Models.Configuration();
            _currentConfiguration.ProductName = ProductNameTextBox.Text;

            // Setup transformations for the CAD canvas
            _scaleTransform = new ScaleTransform(1, 1);
            _translateTransform = new TranslateTransform(0, 0);
            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_scaleTransform);
            _transformGroup.Children.Add(_translateTransform);
            CadCanvas.RenderTransform = _transformGroup;

            // Attach mouse event handlers for canvas zoom and pan
            CadCanvas.MouseWheel += CadCanvas_MouseWheel;
            CadCanvas.MouseDown += CadCanvas_MouseDown; // For initiating pan
            CadCanvas.MouseMove += CadCanvas_MouseMove; // For active panning
            CadCanvas.MouseUp += CadCanvas_MouseUp;     // For ending pan

            // Attach event handler for canvas resize
            CadCanvas.SizeChanged += CadCanvas_SizeChanged;

            // Attach event handlers for nozzle checkboxes
            // UpperNozzleOnCheckBox.Checked += UpperNozzleOnCheckBox_Changed; // Removed
            // UpperNozzleOnCheckBox.Unchecked += UpperNozzleOnCheckBox_Changed; // Removed
            // LowerNozzleOnCheckBox.Checked += LowerNozzleOnCheckBox_Changed; // Removed
            // LowerNozzleOnCheckBox.Unchecked += LowerNozzleOnCheckBox_Changed; // Removed

            // Set initial state for dependent checkboxes
            // UpperNozzleOnCheckBox_Changed(null, null); // Removed
            // LowerNozzleOnCheckBox_Changed(null, null); // Removed

            // Initialize Spray Pass Management
            _selectedDxfEntities.Clear();
            _wpfShapeToDxfEntityMap.Clear(); // Assuming this map is for temporary DXF display, not persistent selection state

            if (_currentConfiguration.SprayPasses == null || !_currentConfiguration.SprayPasses.Any())
            {
                _currentConfiguration.SprayPasses = new List<SprayPass> { new SprayPass { PassName = "Default Pass 1" } };
                _currentConfiguration.CurrentPassIndex = 0;
            }
            else if (_currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count)
            {
                _currentConfiguration.CurrentPassIndex = 0;
            }

            SprayPassesListBox.ItemsSource = _currentConfiguration.SprayPasses;
            if (_currentConfiguration.CurrentPassIndex >= 0 && _currentConfiguration.CurrentPassIndex < SprayPassesListBox.Items.Count)
            {
                SprayPassesListBox.SelectedIndex = _currentConfiguration.CurrentPassIndex;
            }

            // Attach new event handlers
            AddPassButton.Click += AddPassButton_Click;
            RemovePassButton.Click += RemovePassButton_Click;
            RenamePassButton.Click += RenamePassButton_Click;
            SprayPassesListBox.SelectionChanged += SprayPassesListBox_SelectionChanged;

            CurrentPassTrajectoriesListBox.SelectionChanged += CurrentPassTrajectoriesListBox_SelectionChanged;
            MoveTrajectoryUpButton.Click += MoveTrajectoryUpButton_Click;
            MoveTrajectoryDownButton.Click += MoveTrajectoryDownButton_Click;

            // Event handlers for the new six checkboxes
            TrajectoryUpperNozzleEnabledCheckBox.Checked += TrajectoryUpperNozzleEnabledCheckBox_Changed;
            TrajectoryUpperNozzleEnabledCheckBox.Unchecked += TrajectoryUpperNozzleEnabledCheckBox_Changed;
            TrajectoryUpperNozzleGasOnCheckBox.Checked += TrajectoryUpperNozzleGasOnCheckBox_Changed;
            TrajectoryUpperNozzleGasOnCheckBox.Unchecked += TrajectoryUpperNozzleGasOnCheckBox_Changed;
            TrajectoryUpperNozzleLiquidOnCheckBox.Checked += TrajectoryUpperNozzleLiquidOnCheckBox_Changed;
            TrajectoryUpperNozzleLiquidOnCheckBox.Unchecked += TrajectoryUpperNozzleLiquidOnCheckBox_Changed;

            TrajectoryLowerNozzleEnabledCheckBox.Checked += TrajectoryLowerNozzleEnabledCheckBox_Changed;
            TrajectoryLowerNozzleEnabledCheckBox.Unchecked += TrajectoryLowerNozzleEnabledCheckBox_Changed;
            TrajectoryLowerNozzleGasOnCheckBox.Checked += TrajectoryLowerNozzleGasOnCheckBox_Changed;
            TrajectoryLowerNozzleGasOnCheckBox.Unchecked += TrajectoryLowerNozzleGasOnCheckBox_Changed;
            TrajectoryLowerNozzleLiquidOnCheckBox.Checked += TrajectoryLowerNozzleLiquidOnCheckBox_Changed;
            TrajectoryLowerNozzleLiquidOnCheckBox.Unchecked += TrajectoryLowerNozzleLiquidOnCheckBox_Changed;

            // Event handler for TrajectoryIsReversedCheckBox
            TrajectoryIsReversedCheckBox.Checked += TrajectoryIsReversedCheckBox_Changed;
            TrajectoryIsReversedCheckBox.Unchecked += TrajectoryIsReversedCheckBox_Changed;

            RefreshCurrentPassTrajectoriesListBox();
            UpdateSelectedTrajectoryDetailUI(); // Initial call (renamed)
            RefreshCadCanvasHighlights(); // Initial call for canvas highlights
        }

        // Removed UpperNozzleOnCheckBox_Changed and LowerNozzleOnCheckBox_Changed

        private void RefreshCadCanvasHighlights()
        {
            if (_currentConfiguration == null || CadCanvas == null) return; // Basic safety check

            // Determine entities in the current pass
            HashSet<DxfEntity> entitiesInCurrentPass = new HashSet<DxfEntity>();
            if (_currentConfiguration.CurrentPassIndex >= 0 &&
                _currentConfiguration.CurrentPassIndex < _currentConfiguration.SprayPasses.Count)
            {
                var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
                if (currentPass != null && currentPass.Trajectories != null)
                {
                    foreach (var trajectory in currentPass.Trajectories)
                    {
                        if (trajectory.OriginalDxfEntity != null) // Assuming Trajectory stores the original DxfEntity
                        {
                            entitiesInCurrentPass.Add(trajectory.OriginalDxfEntity);
                        }
                    }
                }
            }

            // Update all shapes on canvas
            foreach (var wpfShape in _wpfShapeToDxfEntityMap.Keys)
            {
                if (_wpfShapeToDxfEntityMap.TryGetValue(wpfShape, out DxfEntity associatedEntity))
                {
                    if (entitiesInCurrentPass.Contains(associatedEntity))
                    {
                        wpfShape.Stroke = SelectedStrokeBrush;
                        wpfShape.StrokeThickness = SelectedStrokeThickness;
                    }
                    else
                    {
                        wpfShape.Stroke = DefaultStrokeBrush;
                        wpfShape.StrokeThickness = DefaultStrokeThickness;
                    }
                }
                else // Should not happen if map is correct
                {
                    wpfShape.Stroke = DefaultStrokeBrush;
                    wpfShape.StrokeThickness = DefaultStrokeThickness;
                }
            }

            // Also, ensure that trajectory preview polylines are handled if they are separate
            // For now, this method focuses on the shapes mapped from _wpfShapeToDxfEntityMap
        }


        private void AddPassButton_Click(object sender, RoutedEventArgs e)
        {
            int passCount = _currentConfiguration.SprayPasses.Count;
            var newPass = new SprayPass { PassName = $"Pass {passCount + 1}" };
            _currentConfiguration.SprayPasses.Add(newPass);

            // Refresh ListBox - simple way for now
            SprayPassesListBox.ItemsSource = null;
            SprayPassesListBox.ItemsSource = _currentConfiguration.SprayPasses;
            SprayPassesListBox.SelectedItem = newPass;
            isConfigurationDirty = true;
            UpdateDirectionIndicator(); // New pass selected, current trajectory selection changes
            UpdateOrderNumberLabels();
        }

        private void RemovePassButton_Click(object sender, RoutedEventArgs e)
        {
            if (SprayPassesListBox.SelectedItem is SprayPass selectedPass)
            {
                if (_currentConfiguration.SprayPasses.Count <= 1)
                {
                    MessageBox.Show("Cannot remove the last spray pass.", "Action Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _currentConfiguration.SprayPasses.Remove(selectedPass);

                // Refresh ListBox and select a new item
                SprayPassesListBox.ItemsSource = null;
                SprayPassesListBox.ItemsSource = _currentConfiguration.SprayPasses;
                if (_currentConfiguration.SprayPasses.Any())
                {
                    _currentConfiguration.CurrentPassIndex = 0;
                    SprayPassesListBox.SelectedIndex = 0;
                }
                else
                {
                    _currentConfiguration.CurrentPassIndex = -1;
                    // Potentially add a new default pass here if needed
                }
                RefreshCurrentPassTrajectoriesListBox(); // Update trajectory list for new selected pass
                isConfigurationDirty = true;
                UpdateDirectionIndicator(); // Pass removed, current trajectory selection changes
                UpdateOrderNumberLabels();
            }
        }

        private void UpdateOrderNumberLabels()
        {
            // Clear existing labels
            foreach (var label in _orderNumberLabels)
            {
                if (CadCanvas.Children.Contains(label))
                {
                    CadCanvas.Children.Remove(label);
                }
            }
            _orderNumberLabels.Clear();

            // Check for valid current pass and trajectories
            if (_currentConfiguration == null ||
                _currentConfiguration.CurrentPassIndex < 0 ||
                _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count ||
                _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].Trajectories == null)
            {
                return;
            }

            var currentPassTrajectories = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].Trajectories;

            for (int i = 0; i < currentPassTrajectories.Count; i++)
            {
                var selectedTrajectory = currentPassTrajectories[i];

                if (selectedTrajectory.Points == null || !selectedTrajectory.Points.Any())
                {
                    continue; // Skip if no points to base position on
                }

                TextBlock orderLabel = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 10,
                    Foreground = Brushes.DarkSlateBlue, // Changed to DarkSlateBlue for better contrast potentially
                    Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 180)), // Semi-transparent light yellow
                    Padding = new Thickness(2, 0, 2, 0),
                    // ToolTip = $"Order: {i + 1}, Entity: {selectedTrajectory.PrimitiveType}" // Optional: add a tooltip
                };

                Point anchorPoint;
                if (selectedTrajectory.PrimitiveType == "Line" && selectedTrajectory.Points.Count >= 2)
                {
                    Point p_start = selectedTrajectory.Points[0];
                    Point p_end = selectedTrajectory.Points[selectedTrajectory.Points.Count - 1];
                    anchorPoint = new Point((p_start.X + p_end.X) / 2, (p_start.Y + p_end.Y) / 2);
                }
                else // For Arcs, Circles, or Lines with < 2 points (though points.Any() is already checked)
                {
                    int midIndex = selectedTrajectory.Points.Count / 2; // Integer division gives lower midpoint for even counts
                    anchorPoint = selectedTrajectory.Points[midIndex];
                }

                // Define offsets - these might need tweaking after visual review
                // Keeping previous offsets as a starting point
                double offsetX = 5;  // Offset to the right of the anchor point
                double offsetY = -15; // Offset above the anchor point (FontSize is 10, Padding makes it taller)

                Canvas.SetLeft(orderLabel, anchorPoint.X + offsetX);
                Canvas.SetTop(orderLabel, anchorPoint.Y + offsetY);
                Panel.SetZIndex(orderLabel, 100); // Ensure labels are on top

                CadCanvas.Children.Add(orderLabel);
                _orderNumberLabels.Add(orderLabel);
            }
        }

        private void RenamePassButton_Click(object sender, RoutedEventArgs e)
        {
            if (SprayPassesListBox.SelectedItem is SprayPass selectedPass)
            {
                // Simple rename for now, no input dialog
                selectedPass.PassName += "_Renamed";

                // Refresh ListBox
                SprayPassesListBox.ItemsSource = null;
                SprayPassesListBox.ItemsSource = _currentConfiguration.SprayPasses;
                SprayPassesListBox.SelectedItem = selectedPass;
                isConfigurationDirty = true;
            }
        }

        private void SprayPassesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SprayPassesListBox.SelectedIndex >= 0)
            {
                _currentConfiguration.CurrentPassIndex = SprayPassesListBox.SelectedIndex;
            }
            else if (!_currentConfiguration.SprayPasses.Any()) // All passes removed
            {
                 _currentConfiguration.CurrentPassIndex = -1;
            }
            // If selection cleared due to item removal, index might be -1 but a pass might still be selected by default.
            // The RemovePassButton_Click should handle setting a valid CurrentPassIndex.

            RefreshCurrentPassTrajectoriesListBox();
            UpdateSelectedTrajectoryDetailUI(); // Renamed
            RefreshCadCanvasHighlights();
            UpdateDirectionIndicator(); // Spray pass selection changed
            UpdateOrderNumberLabels();
        }

        private void RefreshCurrentPassTrajectoriesListBox()
        {
            CurrentPassTrajectoriesListBox.ItemsSource = null; // Clear existing items/binding
            if (_currentConfiguration.CurrentPassIndex >= 0 && _currentConfiguration.CurrentPassIndex < _currentConfiguration.SprayPasses.Count)
            {
                var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
                CurrentPassTrajectoriesListBox.ItemsSource = currentPass.Trajectories;
                // Assuming Trajectory.ToString() is overridden for display or DisplayMemberPath is set in XAML if needed
            }
            // else CurrentPassTrajectoriesListBox remains empty
            UpdateSelectedTrajectoryDetailUI(); // Renamed: Update nozzle UI as selected trajectory might change
        }

        private void CurrentPassTrajectoriesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Trace.WriteLine("++++ CurrentPassTrajectoriesListBox_SelectionChanged Fired ++++");
            Trace.Flush();
            UpdateSelectedTrajectoryDetailUI(); // Renamed
            UpdateDirectionIndicator(); // Add call to update direction indicator
    RefreshCadCanvasHighlights(); // <-- ADD THIS LINE
        }

        private void UpdateDirectionIndicator()
        {
            // Clear existing indicators
            foreach (var indicator in _directionIndicators)
            {
                if (CadCanvas.Children.Contains(indicator))
                {
                    CadCanvas.Children.Remove(indicator);
                }
            }
            _directionIndicators.Clear();

            // Check for valid current pass
            if (_currentConfiguration == null ||
                _currentConfiguration.CurrentPassIndex < 0 ||
                _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count ||
                _currentConfiguration.SprayPasses == null)
            {
                return;
            }

            var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
            if (currentPass == null || currentPass.Trajectories == null)
            {
                return;
            }

            const double fixedArrowLineLength = 8.0; // Fixed visual length for the arrow's line segment
            Trajectory? actuallySelectedItem = CurrentPassTrajectoriesListBox.SelectedItem as Trajectory; // Get selected item once

            foreach (var trajectoryInLoop in currentPass.Trajectories) // Renamed loop variable for clarity
            {
                if (trajectoryInLoop.Points == null || !trajectoryInLoop.Points.Any())
                {
                    continue; // Skip if no points
                }

                var newIndicator = new DirectionIndicator
                {
                    Color = (trajectoryInLoop == actuallySelectedItem) ? SelectedStrokeBrush : DefaultStrokeBrush,
                    ArrowheadSize = 8,
                    StrokeThickness = 1.5
                };

                List<System.Windows.Point> points = trajectoryInLoop.Points;
                Point arrowStartPoint = new Point();
                Point arrowEndPoint = new Point();
                bool addIndicator = false;

                switch (trajectoryInLoop.PrimitiveType)
                {
                    case "Line":
                        if (points.Count >= 2)
                        {
                            Point p_start = points[0];
                            Point p_end = points[points.Count - 1];
                            Point midPoint = new Point((p_start.X + p_end.X) / 2, (p_start.Y + p_end.Y) / 2);
                            Vector direction = p_end - p_start;

                            if (direction.Length > 0)
                            {
                                direction.Normalize();
                                arrowStartPoint = midPoint - direction * (fixedArrowLineLength / 2.0);
                                arrowEndPoint = midPoint + direction * (fixedArrowLineLength / 2.0);
                                addIndicator = true;
                            }
                        }
                        break;
                    case "Arc":
                        if (points.Count >= 2)
                        {
                            Point p0 = points[0]; // First point on arc
                            Point p1 = points[1]; // Second point to determine initial tangent
                            Vector direction = p1 - p0;

                            if (direction.Length > 0.001) // Check for non-zero length
                            {
                                direction.Normalize();
                                // Center the short arrow around p0
                                arrowStartPoint = p0 - direction * (fixedArrowLineLength / 2.0);
                                arrowEndPoint = p0 + direction * (fixedArrowLineLength / 2.0);
                                addIndicator = true;
                            }
                        }
                        break; // End of case "Arc"
                    case "Circle":
                        if (points.Count >= 2)
                        {
                            Point p0 = points[0]; // First point on circumference
                            Point p1 = points[1]; // Second point to determine initial tangent
                            Vector direction = p1 - p0;

                            if (direction.Length > 0)
                            {
                                direction.Normalize();
                                // Center the short arrow around p0
                                arrowStartPoint = p0 - direction * (fixedArrowLineLength / 2.0);
                                arrowEndPoint = p0 + direction * (fixedArrowLineLength / 2.0);
                                addIndicator = true;
                            }
                        }
                        break;
                    default:
                        // Unknown primitive type, do not show indicator
                        break;
                }

                if (addIndicator && arrowStartPoint != arrowEndPoint)
                {
                    newIndicator.StartPoint = arrowStartPoint;
                    newIndicator.EndPoint = arrowEndPoint;
                    System.Windows.Controls.Panel.SetZIndex(newIndicator, 99); // Set a high Z-index
                    CadCanvas.Children.Add(newIndicator);
                    _directionIndicators.Add(newIndicator);
                }
            }
        }

        private void MoveTrajectoryUpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count) return;
            var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
            var selectedIndex = CurrentPassTrajectoriesListBox.SelectedIndex;

            if (selectedIndex > 0 && currentPass.Trajectories.Count > selectedIndex)
            {
                var itemToMove = currentPass.Trajectories[selectedIndex];
                currentPass.Trajectories.RemoveAt(selectedIndex);
                currentPass.Trajectories.Insert(selectedIndex - 1, itemToMove);

                CurrentPassTrajectoriesListBox.ItemsSource = null; // Refresh
                CurrentPassTrajectoriesListBox.ItemsSource = currentPass.Trajectories;
                CurrentPassTrajectoriesListBox.SelectedIndex = selectedIndex - 1;
                isConfigurationDirty = true;
                RefreshCadCanvasHighlights(); // Visual update after reorder
                UpdateDirectionIndicator(); // Selection might change or visual needs refresh
                UpdateOrderNumberLabels();
            }
        }

        private void MoveTrajectoryDownButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count) return;
            var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
            var selectedIndex = CurrentPassTrajectoriesListBox.SelectedIndex;

            if (selectedIndex >= 0 && selectedIndex < currentPass.Trajectories.Count - 1)
            {
                var itemToMove = currentPass.Trajectories[selectedIndex];
                currentPass.Trajectories.RemoveAt(selectedIndex);
                currentPass.Trajectories.Insert(selectedIndex + 1, itemToMove);

                CurrentPassTrajectoriesListBox.ItemsSource = null; // Refresh
                CurrentPassTrajectoriesListBox.ItemsSource = currentPass.Trajectories;
                CurrentPassTrajectoriesListBox.SelectedIndex = selectedIndex + 1;
                isConfigurationDirty = true;
                RefreshCadCanvasHighlights(); // Visual update after reorder
                UpdateDirectionIndicator(); // Selection might change or visual needs refresh
                UpdateOrderNumberLabels();
            }
        }

        private void UpdateSelectedTrajectoryDetailUI() // Renamed method
        {
            // Nozzle settings part
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                _trajectoryInDetailView = selectedTrajectory; // Assign to the new field

                // Enable GasOn and LiquidOn checkboxes by default when a trajectory is selected.
                // Specific IsEnabled state for GasOn will be set based on LiquidOn state.
                TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = true;
                TrajectoryUpperNozzleLiquidOnCheckBox.IsEnabled = true;
                TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = true;
                TrajectoryLowerNozzleLiquidOnCheckBox.IsEnabled = true;

                // Set IsChecked status for nozzle settings from selected trajectory
                // TrajectoryUpperNozzleEnabledCheckBox.IsChecked = selectedTrajectory.UpperNozzleEnabled; // Hidden
                TrajectoryUpperNozzleLiquidOnCheckBox.IsChecked = selectedTrajectory.UpperNozzleLiquidOn;
                // Set GasOn AFTER LiquidOn, then apply logic
                TrajectoryUpperNozzleGasOnCheckBox.IsChecked = selectedTrajectory.UpperNozzleGasOn;

                // TrajectoryLowerNozzleEnabledCheckBox.IsChecked = selectedTrajectory.LowerNozzleEnabled; // Hidden
                TrajectoryLowerNozzleLiquidOnCheckBox.IsChecked = selectedTrajectory.LowerNozzleLiquidOn;
                // Set GasOn AFTER LiquidOn, then apply logic
                TrajectoryLowerNozzleGasOnCheckBox.IsChecked = selectedTrajectory.LowerNozzleGasOn;

                // Apply interdependency logic for Upper Nozzle
                if (TrajectoryUpperNozzleLiquidOnCheckBox.IsChecked == true)
                {
                    TrajectoryUpperNozzleGasOnCheckBox.IsChecked = true; // Ensure Gas is on if Liquid is on
                    selectedTrajectory.UpperNozzleGasOn = true; // Sync model if needed due to this rule
                    TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = false;
                }
                else
                {
                    TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = true;
                }

                // Apply interdependency logic for Lower Nozzle
                if (TrajectoryLowerNozzleLiquidOnCheckBox.IsChecked == true)
                {
                    TrajectoryLowerNozzleGasOnCheckBox.IsChecked = true; // Ensure Gas is on if Liquid is on
                    selectedTrajectory.LowerNozzleGasOn = true; // Sync model if needed due to this rule
                    TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = false;
                }
                else
                {
                    TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = true;
                }

                // The old EnabledCheckBox_Changed handlers are no longer needed for this.
                // TrajectoryUpperNozzleEnabledCheckBox_Changed(null, null); // Obsolete
                // TrajectoryLowerNozzleEnabledCheckBox_Changed(null, null); // Obsolete

                // Geometry settings part
                TrajectoryIsReversedCheckBox.IsChecked = selectedTrajectory.IsReversed;

                // Visibility of IsReversed checkbox (only for Line/Arc)
                if (selectedTrajectory.PrimitiveType == "Line" || selectedTrajectory.PrimitiveType == "Arc")
                {
                    TrajectoryIsReversedCheckBox.Visibility = Visibility.Visible;
                }
                else
                {
                    TrajectoryIsReversedCheckBox.Visibility = Visibility.Collapsed;
                }

                // Visibility and content of Z-coordinate panels
                LineHeightControlsPanel.Visibility = selectedTrajectory.PrimitiveType == "Line" ? Visibility.Visible : Visibility.Collapsed;
                ArcHeightControlsPanel.Visibility = selectedTrajectory.PrimitiveType == "Arc" ? Visibility.Visible : Visibility.Collapsed;
                CircleHeightControlsPanel.Visibility = selectedTrajectory.PrimitiveType == "Circle" ? Visibility.Visible : Visibility.Collapsed;

                if (selectedTrajectory.PrimitiveType == "Line")
                {
                    LineStartZTextBox.Text = selectedTrajectory.LineStartPoint.Z.ToString("F3");
                    LineEndZTextBox.Text = selectedTrajectory.LineEndPoint.Z.ToString("F3");
                }
                else if (selectedTrajectory.PrimitiveType == "Arc")
                {
                    // For an arc defined by 3 points, the concept of a single "ArcCenter.Z" is complex
                    // if the arc is tilted. For now, we might display the Z of the first point,
                    // or average Z, or leave it blank. Assuming P1's Z for simplicity if available.
                    if (selectedTrajectory.ArcPoint1 != null)
                    {
                        ArcCenterZTextBox.Text = selectedTrajectory.ArcPoint1.Coordinates.Z.ToString("F3");
                    }
                    else
                    {
                        ArcCenterZTextBox.Text = string.Empty; // Or some default like "N/A"
                    }
                }
                else if (selectedTrajectory.PrimitiveType == "Circle")
                {
                    CircleCenterZTextBox.Text = selectedTrajectory.CircleCenter.Z.ToString("F3");
                }

                // Set Tags for Z-coordinate TextBoxes
                LineStartZTextBox.Tag = selectedTrajectory;
                LineEndZTextBox.Tag = selectedTrajectory;
                ArcCenterZTextBox.Tag = selectedTrajectory;
                CircleCenterZTextBox.Tag = selectedTrajectory;
            }
            else // No trajectory selected
            {
                _trajectoryInDetailView = null; // Clear the field

                // Disable and uncheck all nozzle checkboxes
                TrajectoryUpperNozzleEnabledCheckBox.IsEnabled = false;
                TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = false;
                TrajectoryUpperNozzleLiquidOnCheckBox.IsEnabled = false;
                TrajectoryLowerNozzleEnabledCheckBox.IsEnabled = false;
                TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = false;
                TrajectoryLowerNozzleLiquidOnCheckBox.IsEnabled = false;

                TrajectoryUpperNozzleEnabledCheckBox.IsChecked = false;
                TrajectoryUpperNozzleGasOnCheckBox.IsChecked = false;
                TrajectoryUpperNozzleLiquidOnCheckBox.IsChecked = false;
                TrajectoryLowerNozzleEnabledCheckBox.IsChecked = false;
                TrajectoryLowerNozzleGasOnCheckBox.IsChecked = false;
                TrajectoryLowerNozzleLiquidOnCheckBox.IsChecked = false;

                // Collapse geometry UI elements
                TrajectoryIsReversedCheckBox.Visibility = Visibility.Collapsed;
                TrajectoryIsReversedCheckBox.IsChecked = false; // Uncheck
                LineHeightControlsPanel.Visibility = Visibility.Collapsed;
                ArcHeightControlsPanel.Visibility = Visibility.Collapsed;
                CircleHeightControlsPanel.Visibility = Visibility.Collapsed;
                LineStartZTextBox.Text = string.Empty;
                LineEndZTextBox.Text = string.Empty;
                ArcCenterZTextBox.Text = string.Empty;
                CircleCenterZTextBox.Text = string.Empty;

                // Clear Tags for Z-coordinate TextBoxes
                LineStartZTextBox.Tag = null;
                LineEndZTextBox.Tag = null;
                ArcCenterZTextBox.Tag = null;
                CircleCenterZTextBox.Tag = null;
            }
        }

        private void TrajectoryIsReversedCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                selectedTrajectory.IsReversed = TrajectoryIsReversedCheckBox.IsChecked ?? false;
                isConfigurationDirty = true;
                PopulateTrajectoryPoints(selectedTrajectory); // Regenerate points
                CurrentPassTrajectoriesListBox.Items.Refresh(); // Update display if ToString() changed or for other bound properties
                RefreshCadCanvasHighlights(); // May be needed if visual representation on canvas depends on points/direction
                UpdateDirectionIndicator(); // Update arrow when direction changes
            }
        }

        private void PopulateTrajectoryPoints(Trajectory trajectory)
        {
            if (trajectory == null) return;

            trajectory.Points.Clear();

            switch (trajectory.PrimitiveType)
            {
                case "Line":
                    trajectory.Points.AddRange(_cadService.ConvertLineTrajectoryToPoints(trajectory));
                    break;
                case "Arc":
                    if (trajectory.ArcPoint1 != null && trajectory.ArcPoint2 != null && trajectory.ArcPoint3 != null)
                    {
                        var arcParams = CalculateArcParametersFromThreePoints(
                            trajectory.ArcPoint1.Coordinates,
                            trajectory.ArcPoint2.Coordinates,
                            trajectory.ArcPoint3.Coordinates);

                        if (arcParams.HasValue)
                        {
                            var (center, radius, startAngle, endAngle, normal, isClockwise) = arcParams.Value;
                            // Create a temporary DxfArc to use with existing CadService method
                            // Note: CadService.ConvertArcToPoints expects angles in degrees.
                            // The DxfArc constructor also expects angles in degrees.
                            DxfArc tempArc = new DxfArc(center, radius, startAngle, endAngle) { Normal = normal };

                            // If the original intention was a CW arc from P1->P2->P3, but DxfArc always assumes CCW,
                            // and our CalculateArcParametersFromThreePoints also re-orders to ensure CCW for DxfArc,
                            // then the points should be generated correctly.
                            // If `IsReversed` on trajectory is true, that will be handled by `_cadService.ConvertArcToPoints` if it has that logic,
                            // or it might need to be handled by swapping start/end angles before calling.
                            // For now, assuming ConvertArcToPoints generates CCW points from startAngle to endAngle.
                            // The IsReversed logic is handled in ConvertArcTrajectoryToPoints in CadService if it needs to be.
                            // Let's assume the current CadService.ConvertArcToPoints can take this tempArc.
                            // However, ConvertArcTrajectoryToPoints takes a Trajectory object.
                            // We need to adapt _cadService.ConvertArcToPoints or use a similar discretization here.

                            // Re-using the logic from CadService.ConvertArcToPoints directly for now for simplicity:
                            List<Point> arcPoints = new List<Point>();
                            double currentAngleDeg = startAngle;
                            double effectiveEndAngleDeg = endAngle;

                            if (isClockwise) // Should have been handled by swapping start/end in CalculateArcParameters. Re-check.
                            {
                                // If CalculateArcParametersFromThreePoints always returns startAngle < endAngle for CCW DxfArc,
                                // then isClockwise flag here might be redundant or indicate original user intent.
                                // For now, assume parameters are for CCW traversal.
                                // If trajectory.IsReversed is true, then we need to reverse the point generation order or angles.
                                if (trajectory.IsReversed) {
                                    currentAngleDeg = endAngle;
                                    effectiveEndAngleDeg = startAngle;
                                    if (effectiveEndAngleDeg > currentAngleDeg) currentAngleDeg += 360; // Sweep backwards
                                } else {
                                     if (effectiveEndAngleDeg < currentAngleDeg) effectiveEndAngleDeg += 360;
                                }
                            } else { // CCW from P1->P2->P3
                                if (trajectory.IsReversed) {
                                    currentAngleDeg = endAngle;
                                    effectiveEndAngleDeg = startAngle;
                                    if (effectiveEndAngleDeg > currentAngleDeg) currentAngleDeg += 360; // Sweep backwards
                                } else {
                                     if (effectiveEndAngleDeg < currentAngleDeg) effectiveEndAngleDeg += 360;
                                }
                            }

                            bool sweepBackwards = trajectory.IsReversed; // Simplified logic from CadService.ConvertArcTrajectoryToPoints
                            if (sweepBackwards) {
                                currentAngleDeg = endAngle; // Start from original end
                                effectiveEndAngleDeg = startAngle; // Go to original start
                                 // Ensure we sweep correctly backwards
                                double sweep = effectiveEndAngleDeg - currentAngleDeg;
                                if (sweep > 0) sweep -= 360; // Ensure negative or zero sweep for backward
                                effectiveEndAngleDeg = currentAngleDeg + sweep; // Target for loop
                            } else {
                                currentAngleDeg = startAngle;
                                effectiveEndAngleDeg = endAngle;
                                double sweep = effectiveEndAngleDeg - currentAngleDeg;
                                if (sweep < 0) sweep += 360; // Ensure positive or zero sweep for forward
                                effectiveEndAngleDeg = currentAngleDeg + sweep;
                            }


                            double step = sweepBackwards ? -TrajectoryPointResolutionAngle : TrajectoryPointResolutionAngle;
                            if (Math.Abs(step) < 1e-6) step = sweepBackwards ? -1.0 : 1.0; // Avoid zero step

                            if (sweepBackwards) {
                                while (currentAngleDeg >= effectiveEndAngleDeg - Math.Abs(step)/2.0) { // Loop condition for backwards
                                    double radAngle = currentAngleDeg * Math.PI / 180.0;
                                    arcPoints.Add(new Point(center.X + radius * Math.Cos(radAngle), center.Y + radius * Math.Sin(radAngle)));
                                    if (currentAngleDeg <= effectiveEndAngleDeg + 1e-5 && currentAngleDeg >= effectiveEndAngleDeg - 1e-5) break; // Reached end
                                    currentAngleDeg += step;
                                     if (currentAngleDeg < effectiveEndAngleDeg && currentAngleDeg > effectiveEndAngleDeg + step -1e-5 ) currentAngleDeg = effectiveEndAngleDeg; // Ensure last point
                                }
                            } else {
                                while (currentAngleDeg <= effectiveEndAngleDeg + Math.Abs(step)/2.0) { // Loop condition for forwards
                                    double radAngle = currentAngleDeg * Math.PI / 180.0;
                                    arcPoints.Add(new Point(center.X + radius * Math.Cos(radAngle), center.Y + radius * Math.Sin(radAngle)));
                                     if (currentAngleDeg <= effectiveEndAngleDeg + 1e-5 && currentAngleDeg >= effectiveEndAngleDeg - 1e-5) break; // Reached end
                                    currentAngleDeg += step;
                                    if (currentAngleDeg > effectiveEndAngleDeg && currentAngleDeg < effectiveEndAngleDeg + step - 1e-5) currentAngleDeg = effectiveEndAngleDeg; // Ensure last point
                                }
                            }
                            trajectory.Points.AddRange(arcPoints);
                        }
                        else
                        {
                            Debug.WriteLine($"[WARNING] PopulateTrajectoryPoints: Could not calculate arc parameters for trajectory. PrimitiveType: {trajectory.PrimitiveType}. Defaulting to line segments if P1,P2,P3 exist.");
                            // Fallback: add P1, P2, P3 as line segments if arc calculation fails
                            if (trajectory.ArcPoint1 != null) trajectory.Points.Add(new Point(trajectory.ArcPoint1.Coordinates.X, trajectory.ArcPoint1.Coordinates.Y));
                            if (trajectory.ArcPoint2 != null) trajectory.Points.Add(new Point(trajectory.ArcPoint2.Coordinates.X, trajectory.ArcPoint2.Coordinates.Y));
                            if (trajectory.ArcPoint3 != null) trajectory.Points.Add(new Point(trajectory.ArcPoint3.Coordinates.X, trajectory.ArcPoint3.Coordinates.Y));
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"[WARNING] PopulateTrajectoryPoints: Arc trajectory missing ArcPoint1/2/3 data.");
                    }
                    break;
                case "Circle":
                    trajectory.Points.AddRange(_cadService.ConvertCircleTrajectoryToPoints(trajectory, TrajectoryPointResolutionAngle));
                    break;
                default:
                    // For other types or if PrimitiveType is not set, Points will remain empty or could be populated from OriginalDxfEntity if needed
                    // For now, we rely on the specific Convert<Primitive>TrajectoryToPoints methods.
                    // If OriginalDxfEntity exists and is of a known DxfEntityType, could fall back to old methods:
                    if (trajectory.OriginalDxfEntity != null) {
                        switch (trajectory.OriginalDxfEntity) {
                            case DxfLine line: trajectory.Points.AddRange(_cadService.ConvertLineToPoints(line)); break;
                            case DxfArc arc: trajectory.Points.AddRange(_cadService.ConvertArcToPoints(arc, TrajectoryPointResolutionAngle)); break;
                            case DxfCircle circle: trajectory.Points.AddRange(_cadService.ConvertCircleToPoints(circle, TrajectoryPointResolutionAngle)); break;
                        }
                    }
                    break;
            }
        }

        private void TrajectoryUpperNozzleEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // This checkbox is now hidden (Visibility="Collapsed" in XAML).
            // Its logic for enabling/disabling GasOn/LiquidOn checkboxes is superseded by the new interdependency logic.
            // We still need to update the model if this event were somehow triggered,
            // though it shouldn't be if the CheckBox is collapsed.
            // For safety, or if it's decided to use the model property differently:
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                // selectedTrajectory.UpperNozzleEnabled = TrajectoryUpperNozzleEnabledCheckBox.IsChecked ?? false;
                // The model's UpperNozzleEnabled might be re-purposed or removed later.
                // For now, we won't automatically uncheck Gas/Liquid here based on this hidden checkbox.
                // isConfigurationDirty = true; // Only if model property is still relevant and changed.
            }
        }

        private void TrajectoryUpperNozzleGasOnCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                selectedTrajectory.UpperNozzleGasOn = TrajectoryUpperNozzleGasOnCheckBox.IsChecked ?? false;
                isConfigurationDirty = true;
            }
        }

        private void TrajectoryUpperNozzleLiquidOnCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                bool isLiquidOn = TrajectoryUpperNozzleLiquidOnCheckBox.IsChecked ?? false;
                selectedTrajectory.UpperNozzleLiquidOn = isLiquidOn;

                if (isLiquidOn)
                {
                    TrajectoryUpperNozzleGasOnCheckBox.IsChecked = true; // Force Gas On
                    selectedTrajectory.UpperNozzleGasOn = true; // Update model
                    TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = false; // Disable Gas checkbox
                }
                else
                {
                    TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = true; // Enable Gas checkbox
                    // Gas state (selectedTrajectory.UpperNozzleGasOn) remains as it was or as user sets it via its own checkbox
                }
                isConfigurationDirty = true;
            }
        }

        private void TrajectoryLowerNozzleEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // This checkbox is now hidden (Visibility="Collapsed" in XAML).
            // Logic is superseded by new interdependency.
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                // selectedTrajectory.LowerNozzleEnabled = TrajectoryLowerNozzleEnabledCheckBox.IsChecked ?? false;
                // The model's LowerNozzleEnabled might be re-purposed or removed later.
                // isConfigurationDirty = true; // Only if model property is still relevant and changed.
            }
        }

        private void TrajectoryLowerNozzleGasOnCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                selectedTrajectory.LowerNozzleGasOn = TrajectoryLowerNozzleGasOnCheckBox.IsChecked ?? false;
                isConfigurationDirty = true;
            }
        }

        private void TrajectoryLowerNozzleLiquidOnCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                bool isLiquidOn = TrajectoryLowerNozzleLiquidOnCheckBox.IsChecked ?? false;
                selectedTrajectory.LowerNozzleLiquidOn = isLiquidOn;

                if (isLiquidOn)
                {
                    TrajectoryLowerNozzleGasOnCheckBox.IsChecked = true; // Force Gas On
                    selectedTrajectory.LowerNozzleGasOn = true; // Update model
                    TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = false; // Disable Gas checkbox
                }
                else
                {
                    TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = true; // Enable Gas checkbox
                    // Gas state (selectedTrajectory.LowerNozzleGasOn) remains as it was
                }
                isConfigurationDirty = true;
            }
        }

        private void ProductNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Check if the control is initialized and loaded to avoid setting dirty flag during setup
            if (this.IsLoaded)
            {
                isConfigurationDirty = true;
            }
        }

    private void UpdateLineStartZFromTextBox()
    {
        if (_trajectoryInDetailView != null && _trajectoryInDetailView.PrimitiveType == "Line")
        {
            if (double.TryParse(LineStartZTextBox.Text, out double newZ))
            {
                if (_trajectoryInDetailView.LineStartPoint.Z != newZ)
                {
                    _trajectoryInDetailView.LineStartPoint = new DxfPoint(
                        _trajectoryInDetailView.LineStartPoint.X,
                        _trajectoryInDetailView.LineStartPoint.Y,
                        newZ);
                    isConfigurationDirty = true;
                    CurrentPassTrajectoriesListBox.Items.Refresh(); // Refresh if Z might be part of ToString()
                }
            }
            else
            {
                MessageBox.Show("Invalid Start Z value. Please enter a valid number.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                LineStartZTextBox.Text = _trajectoryInDetailView.LineStartPoint.Z.ToString("F3");
            }
        }
    }

    private void LineStartZTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLineStartZFromTextBox();
    }

    // Removed LineStartZTextBox_LostFocus

    private (DxfPoint Center, double Radius, double StartAngle, double EndAngle, DxfVector Normal, bool IsClockwise)? CalculateArcParametersFromThreePoints(DxfPoint p1, DxfPoint p2, DxfPoint p3, double tolerance = 1e-6)
    {
        // Implementation of 3-point to arc parameters calculation.
        // This is a standard but somewhat complex geometric problem.
        // Source for algorithm idea: https://www.ambrsoft.com/TrigoCalc/Circle3D.htm and various geometry resources.

        // Check for collinearity or coincident points
        // Vector P1P2
        double v12x = p2.X - p1.X;
        double v12y = p2.Y - p1.Y;
        double v12z = p2.Z - p1.Z;

        // Vector P1P3
        double v13x = p3.X - p1.X;
        double v13y = p3.Y - p1.Y;
        double v13z = p3.Z - p1.Z;

        // Cross product (P1P2) x (P1P3)
        double crossX = v12y * v13z - v12z * v13y;
        double crossY = v12z * v13x - v12x * v13z;
        double crossZ = v12x * v13y - v12y * v13x;

        double crossLengthSq = crossX * crossX + crossY * crossY + crossZ * crossZ;
        if (crossLengthSq < tolerance * tolerance) // Points are collinear or too close
        {
            Debug.WriteLine("[WARNING] CalculateArcParametersFromThreePoints: Points are collinear or coincident.");
            return null;
        }

        DxfVector normal = new DxfVector(crossX, crossY, crossZ).Normalize();

        // For simplicity in this 2D focused app, project to XY plane if Z components are small,
        // or enforce that the arc must lie in a plane parallel to XY.
        // For now, let's assume points define an arc in a plane that could be tilted.
        // The IxMilia.Dxf.DxfArc typically uses OCS (Object Coordinate System).
        // For a simple 2D arc on XY plane, normal is (0,0,1) or (0,0,-1).

        // Using a formula for circumcenter of a triangle in 2D (projected, assuming normal is mainly Z)
        // This simplification might be an issue if the arc is not planar and parallel to XY.
        // For a true 3D arc from 3 points, the plane of the arc must be found first.
        // Given our app context, let's assume Z is constant or use the Z of P1.
        // A robust 3D solution is more complex. For now, using a 2D projection + Z.

        // Simplified 2D calculation (assuming Z is roughly constant, using p1.Z for the plane)
        // This will not work correctly for arcs significantly tilted out of the XY plane.
        // However, DxfArc itself is often defined in OCS that aligns with XY.
        Point pt1 = new Point(p1.X, p1.Y);
        Point pt2 = new Point(p2.X, p2.Y);
        Point pt3 = new Point(p3.X, p3.Y);

        double D = 2 * (pt1.X * (pt2.Y - pt3.Y) + pt2.X * (pt3.Y - pt1.Y) + pt3.X * (pt1.Y - pt2.Y));
        if (Math.Abs(D) < tolerance) // Collinear in 2D projection
        {
            Debug.WriteLine("[WARNING] CalculateArcParametersFromThreePoints: Points are collinear in 2D projection.");
            return null;
        }

        double pt1Sq = pt1.X * pt1.X + pt1.Y * pt1.Y;
        double pt2Sq = pt2.X * pt2.X + pt2.Y * pt2.Y;
        double pt3Sq = pt3.X * pt3.X + pt3.Y * pt3.Y;

        double centerX = (pt1Sq * (pt2.Y - pt3.Y) + pt2Sq * (pt3.Y - pt1.Y) + pt3Sq * (pt1.Y - pt2.Y)) / D;
        double centerY = (pt1Sq * (pt3.X - pt2.X) + pt2Sq * (pt1.X - pt3.X) + pt3Sq * (pt2.X - pt1.X)) / D;

        // Assuming Z is constant, take from P1 for the center's Z.
        // This is a simplification for primarily 2D DXF usage. True 3D arcs are more complex.
        DxfPoint center = new DxfPoint(centerX, centerY, p1.Z);

        double radius = Math.Sqrt(Math.Pow(pt1.X - centerX, 2) + Math.Pow(pt1.Y - centerY, 2));

        double startAngle = Math.Atan2(p1.Y - center.Y, p1.X - center.X) * (180.0 / Math.PI);
        double midAngle = Math.Atan2(p2.Y - center.Y, p2.X - center.X) * (180.0 / Math.PI);
        double endAngle = Math.Atan2(p3.Y - center.Y, p3.X - center.X) * (180.0 / Math.PI);

        // Normalize angles to 0-360
        startAngle = (startAngle % 360 + 360) % 360;
        midAngle = (midAngle % 360 + 360) % 360;
        endAngle = (endAngle % 360 + 360) % 360;

        // Determine sweep direction (IsClockwise)
        // If (start -> mid -> end) is CCW, then sweep angle (end - start) should contain mid.
        // If (start -> mid -> end) is CW, then sweep angle (start - end) should contain mid.
        bool isClockwise;
        double sweepAngle;

        // Check CCW sweep from start to end
        double sweepCCW = (endAngle - startAngle + 360) % 360;
        double midRelativeToStartCCW = (midAngle - startAngle + 360) % 360;

        if (midRelativeToStartCCW < sweepCCW) // Mid is within CCW sweep from start to end
        {
            isClockwise = false;
            sweepAngle = sweepCCW; // DxfArc uses positive sweep angle typically
        }
        else // Mid must be within CW sweep from start to end
        {
            isClockwise = true;
            // For DxfArc, angles are usually defined for CCW travel.
            // If our 3 points define a CW arc, we swap start/end and use CCW sweep.
            // Or, DxfArc handles CW if startAngle > endAngle and normal implies it.
            // For now, let's keep it simple: if it's CW, swap P1 and P3 for DxfArc definition.
            // This means DxfArc will always be defined CCW.
            double tempAngle = startAngle;
            startAngle = endAngle;
            endAngle = tempAngle;
            sweepAngle = (endAngle - startAngle + 360) % 360;
            // The "isClockwise" flag can be used by the caller if needed, but DxfArc usually assumes CCW.
        }
        if (Math.Abs(sweepAngle) < tolerance || Math.Abs(sweepAngle - 360) < tolerance) {
             // This might happen if points are very close or collinear after all.
             Debug.WriteLine("[WARNING] CalculateArcParametersFromThreePoints: Calculated sweep angle is near 0 or 360, might indicate collinearity or issue.");
             // Fallback to a line between P1 and P3 perhaps? For now, return null.
             return null;
        }


        // For DxfArc, the normal vector is important for OCS.
        // We calculated a geometric normal. For typical 2D DXF on XY plane, it's (0,0,1).
        // If p1,p2,p3 are Z-coplanar, the normal will be along Z.
        // If normal.Z < 0 and we want CCW interpretation for DxfArc, angles might need adjustment or normal flipped.
        // For now, using calculated geometric normal. If it's mostly along -Z, flip it and angles.
        if (normal.Z < 0) {
            normal = new DxfVector(-normal.X, -normal.Y, -normal.Z);
            double temp = startAngle;
            startAngle = endAngle;
            endAngle = temp;
            isClockwise = !isClockwise; // Flip direction if normal was flipped
        }


        return (center, radius, startAngle, endAngle, normal, isClockwise);
    }

    // Removed LineStartZTextBox_LostFocus

    private void UpdateLineEndZFromTextBox()
    {
        if (_trajectoryInDetailView != null && _trajectoryInDetailView.PrimitiveType == "Line")
        {
            if (double.TryParse(LineEndZTextBox.Text, out double newZ))
            {
                if (_trajectoryInDetailView.LineEndPoint.Z != newZ)
                {
                    _trajectoryInDetailView.LineEndPoint = new DxfPoint(
                        _trajectoryInDetailView.LineEndPoint.X,
                        _trajectoryInDetailView.LineEndPoint.Y,
                        newZ);
                    isConfigurationDirty = true;
                    CurrentPassTrajectoriesListBox.Items.Refresh(); // Refresh if Z might be part of ToString()
                }
            }
            else
            {
                MessageBox.Show("Invalid End Z value. Please enter a valid number.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                LineEndZTextBox.Text = _trajectoryInDetailView.LineEndPoint.Z.ToString("F3");
            }
        }
    }

    private void LineEndZTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLineEndZFromTextBox();
    }

    // Removed LineEndZTextBox_LostFocus

    private void UpdateArcCenterZFromTextBox()
    {
        if (_trajectoryInDetailView != null && _trajectoryInDetailView.PrimitiveType == "Arc")
        {
            if (double.TryParse(ArcCenterZTextBox.Text, out double newZ))
            {
                // For a 3-point arc, updating a single "Center Z" is complex.
                // Assuming this Z applies to all three points P1, P2, P3 for simplicity,
                // which means the arc is being moved along the Z-axis without tilting.
                bool changed = false;
                if (_trajectoryInDetailView.ArcPoint1 != null && _trajectoryInDetailView.ArcPoint1.Coordinates.Z != newZ)
                {
                    _trajectoryInDetailView.ArcPoint1.Coordinates = new DxfPoint(
                        _trajectoryInDetailView.ArcPoint1.Coordinates.X,
                        _trajectoryInDetailView.ArcPoint1.Coordinates.Y,
                        newZ);
                    changed = true;
                }
                if (_trajectoryInDetailView.ArcPoint2 != null && _trajectoryInDetailView.ArcPoint2.Coordinates.Z != newZ)
                {
                    _trajectoryInDetailView.ArcPoint2.Coordinates = new DxfPoint(
                        _trajectoryInDetailView.ArcPoint2.Coordinates.X,
                        _trajectoryInDetailView.ArcPoint2.Coordinates.Y,
                        newZ);
                    changed = true;
                }
                if (_trajectoryInDetailView.ArcPoint3 != null && _trajectoryInDetailView.ArcPoint3.Coordinates.Z != newZ)
                {
                    _trajectoryInDetailView.ArcPoint3.Coordinates = new DxfPoint(
                        _trajectoryInDetailView.ArcPoint3.Coordinates.X,
                        _trajectoryInDetailView.ArcPoint3.Coordinates.Y,
                        newZ);
                    changed = true;
                }

                if (changed)
                {
                    isConfigurationDirty = true;
                    PopulateTrajectoryPoints(_trajectoryInDetailView); // Regenerate points with new Z
                    CurrentPassTrajectoriesListBox.Items.Refresh();
                }
            }
            else
            {
                MessageBox.Show("Invalid Arc Z value. Please enter a valid number.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                // Restore to P1's Z or leave as is
                if(_trajectoryInDetailView.ArcPoint1 != null)
                    ArcCenterZTextBox.Text = _trajectoryInDetailView.ArcPoint1.Coordinates.Z.ToString("F3");
                else
                    ArcCenterZTextBox.Text = "0.000"; // Default if P1 is null
            }
        }
    }

    private void ArcCenterZTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateArcCenterZFromTextBox();
    }

    // Removed ArcCenterZTextBox_LostFocus

    private void UpdateCircleCenterZFromTextBox()
    {
        if (_trajectoryInDetailView != null && _trajectoryInDetailView.PrimitiveType == "Circle")
        {
            if (double.TryParse(CircleCenterZTextBox.Text, out double newZ))
            {
                if (_trajectoryInDetailView.CircleCenter.Z != newZ)
                {
                    _trajectoryInDetailView.CircleCenter = new DxfPoint(
                        _trajectoryInDetailView.CircleCenter.X,
                        _trajectoryInDetailView.CircleCenter.Y,
                        newZ);
                    isConfigurationDirty = true;
                    CurrentPassTrajectoriesListBox.Items.Refresh(); // Refresh if Z might be part of ToString()
                }
            }
            else
            {
                MessageBox.Show("Invalid Circle Center Z value. Please enter a valid number.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                CircleCenterZTextBox.Text = _trajectoryInDetailView.CircleCenter.Z.ToString("F3");
            }
        }
    }

    private void CircleCenterZTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCircleCenterZFromTextBox();
    }

    // Removed CircleCenterZTextBox_LostFocus

        /// <summary>
        /// Handles the Closing event of the window. Ensures Modbus connection is disconnected.
        /// </summary>
        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            _modbusService.Disconnect(); // Clean up Modbus connection.
        }

        /// <summary>
        /// Handles the SizeChanged event of the CadCanvas.
        /// Calls PerformFitToView to rescale and center the DXF content when the canvas size changes.
        /// </summary>
        private void CadCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Debug.WriteLine($"[DEBUG] CadCanvas_SizeChanged: NewSize=({e.NewSize.Width}, {e.NewSize.Height}), DXF Loaded={_currentDxfDocument != null}");
            // Check if the canvas has a valid size and if a DXF document is loaded
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0 && _currentDxfDocument != null)
            {
                PerformFitToView();
            }
        }

        /// <summary>
        /// Handles the Click event of the "Load DXF" button.
        /// Prompts the user to select a DXF file, loads it using <see cref="CadService"/>,
        /// processes its entities for display, and fits the view to the loaded drawing.
        /// </summary>
        private void LoadDxfButton_Click(object sender, RoutedEventArgs e)
        {
            bool canProceed = PromptAndTrySaveChanges();
            if (!canProceed)
            {
                StatusTextBlock.Text = "Load DXF cancelled due to unsaved changes."; // Optional: provide user feedback
                return;
            }

            OpenFileDialog openFileDialog = new OpenFileDialog {
                Filter = "DXF files (*.dxf)|*.dxf|All files (*.*)|*.*", Title = "Load DXF File" };
            string initialDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".."));
            if (!Directory.Exists(initialDir)) initialDir = "/app/RobTeachProject/RobTeach/";
            openFileDialog.InitialDirectory = initialDir;

            try {
                if (openFileDialog.ShowDialog() == true) {
                    _currentDxfFilePath = openFileDialog.FileName;
                    StatusTextBlock.Text = $"Loading DXF: {Path.GetFileName(_currentDxfFilePath)}...";

                    // 1. Clear canvas and DXF-specific visual maps
                    CadCanvas.Children.Clear();
                    _wpfShapeToDxfEntityMap.Clear();
                    _trajectoryPreviewPolylines.Clear();
                    _selectedDxfEntities.Clear();
                    _dxfEntityHandleMap.Clear();

                    // 2. Reset Product Name and Configuration Object
                    ProductNameTextBox.Text = $"Product_{DateTime.Now:yyyyMMddHHmmss}";
                    _currentConfiguration = new Models.Configuration { ProductName = ProductNameTextBox.Text };
                    _currentLoadedConfigPath = null;

                    // 3. Reset UI related to Configuration (Passes, Trajectories, Details)
                    if (_currentConfiguration.SprayPasses == null || !_currentConfiguration.SprayPasses.Any())
                    {
                        _currentConfiguration.SprayPasses = new List<SprayPass> { new SprayPass { PassName = "Default Pass 1" } };
                        _currentConfiguration.CurrentPassIndex = 0;
                    }
                    else if (_currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count)
                    {
                         _currentConfiguration.CurrentPassIndex = _currentConfiguration.SprayPasses.Any() ? 0 : -1;
                    }

                    SprayPassesListBox.ItemsSource = null;
                    SprayPassesListBox.ItemsSource = _currentConfiguration.SprayPasses;

                    if (_currentConfiguration.CurrentPassIndex >= 0 && _currentConfiguration.CurrentPassIndex < SprayPassesListBox.Items.Count)
                    {
                        SprayPassesListBox.SelectedIndex = _currentConfiguration.CurrentPassIndex;
                    }
                    else if (SprayPassesListBox.Items.Count > 0)
                    {
                        SprayPassesListBox.SelectedIndex = 0; // Fallback
                        _currentConfiguration.CurrentPassIndex = 0;
                    }
                    else
                    {
                         _currentConfiguration.CurrentPassIndex = -1; // No passes, no selection
                    }

                    RefreshCurrentPassTrajectoriesListBox();
                    UpdateSelectedTrajectoryDetailUI();
                    RefreshCadCanvasHighlights();
                    UpdateTrajectoryPreview();
                    UpdateDirectionIndicator();
                    UpdateOrderNumberLabels();

                    // 4. Reset DXF document state
                    _currentDxfDocument = null;
                    _dxfBoundingBox = Rect.Empty;

                    // isConfigurationDirty = false; // This will be set AFTER the new DXF is loaded and processed successfully.

                    _currentDxfDocument = _cadService.LoadDxf(_currentDxfFilePath);

                    if (_currentDxfDocument == null) {
                        StatusTextBlock.Text = "Failed to load DXF document (null document returned).";
                        MessageBox.Show("The DXF document could not be loaded. The file might be empty or an unknown error occurred.", "Error Loading DXF", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    // Note: IxMilia.Dxf doesn't expose Handle property directly
                    // We'll skip handle mapping for now

                    List<System.Windows.Shapes.Shape> wpfShapes = _cadService.GetWpfShapesFromDxf(_currentDxfDocument);
                    int shapeIndex = 0;
                    
                    foreach(var entity in _currentDxfDocument.Entities)
                    {
                        if (shapeIndex < wpfShapes.Count && wpfShapes[shapeIndex] != null) {
                            var wpfShape = wpfShapes[shapeIndex];
                            wpfShape.Stroke = DefaultStrokeBrush; 
                            wpfShape.StrokeThickness = DefaultStrokeThickness;
                            wpfShape.MouseLeftButtonDown += OnCadEntityClicked;
                            _wpfShapeToDxfEntityMap[wpfShape] = entity;
                            CadCanvas.Children.Add(wpfShape);
                            shapeIndex++; 
                        }
                    }

                    _dxfBoundingBox = GetDxfBoundingBox(_currentDxfDocument);
                    // Call PerformFitToView after layout has had a chance to update
                    Debug.WriteLine($"[DEBUG] LoadDxfButton_Click: Scheduling PerformFitToView via Dispatcher. CanvasSize=({CadCanvas.ActualWidth}, {CadCanvas.ActualHeight})");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Debug.WriteLine($"[DEBUG] LoadDxfButton_Click (Dispatcher): Calling PerformFitToView. CanvasSize=({CadCanvas.ActualWidth}, {CadCanvas.ActualHeight})");
                        PerformFitToView();
                        StatusTextBlock.Text = $"Loaded: {Path.GetFileName(_currentDxfFilePath)}. Click shapes to select.";
                        isConfigurationDirty = false; // Set dirty flag only after successful load and fit
                        Debug.WriteLine("[DEBUG] LoadDxfButton_Click (Dispatcher): PerformFitToView completed.");
                    }), DispatcherPriority.Background);
                    // Note: isConfigurationDirty is set to false inside the dispatcher action.
                    // If it needed to be set immediately, it would be here, but it's better tied to the completion of fitting.
                    UpdateDirectionIndicator(); // Update after loading and potential default selections
                    UpdateOrderNumberLabels();
                } else { StatusTextBlock.Text = "DXF loading cancelled."; }
            }
            catch (FileNotFoundException fnfEx) {
                StatusTextBlock.Text = "Error: DXF file not found.";
                MessageBox.Show($"DXF file not found:\n{fnfEx.Message}", "Error Loading DXF", MessageBoxButton.OK, MessageBoxImage.Error);
                _currentDxfDocument = null;
                isConfigurationDirty = false;
                UpdateOrderNumberLabels();
            }
            // Removed specific catch for netDxf.DxfVersionNotSupportedException. General Exception will handle DXF-specific errors.
            catch (Exception ex) {
                StatusTextBlock.Text = "Error loading or processing DXF file.";
                MessageBox.Show($"An error occurred while loading or processing the DXF file:\n{ex.Message}\n\nEnsure the file is a valid DXF format.", "Error Loading DXF", MessageBoxButton.OK, MessageBoxImage.Error);
                _currentDxfDocument = null;
                CadCanvas.Children.Clear();
                _selectedDxfEntities.Clear(); _wpfShapeToDxfEntityMap.Clear(); _dxfEntityHandleMap.Clear();
                _trajectoryPreviewPolylines?.Clear();
                _currentConfiguration = new Models.Configuration { ProductName = ProductNameTextBox.Text };
                isConfigurationDirty = false;
                UpdateTrajectoryPreview();
                UpdateDirectionIndicator(); // Clear indicator on error too
                UpdateOrderNumberLabels();
            }
        }

        /// <summary>
        /// Handles the click event on a CAD entity shape, toggling its selection state.
        /// </summary>
        private void OnCadEntityClicked(object sender, MouseButtonEventArgs e)
        {
            Trace.WriteLine("++++ OnCadEntityClicked Fired ++++");
            Trace.Flush();
            Debug.WriteLine($"[DEBUG] OnCadEntityClicked: Sender is {sender?.GetType().Name}");
            Trajectory trajectoryToSelect = null; // Declare at wider scope

            // Detailed check for the main condition
            if (sender is System.Windows.Shapes.Shape clickedShape && _wpfShapeToDxfEntityMap.TryGetValue(clickedShape, out DxfEntity? dxfEntity))
            {
                // keyExists is implicitly true if TryGetValue succeeds.
                // dxfEntity will be non-null if TryGetValue returns true.
                Trace.WriteLine($"  -- Checking sender type: {sender?.GetType().Name ?? "null"}, IsShape: true, Map contains key: true");
                Trace.Flush();
                Trace.WriteLine("  -- Condition (sender is Shape AND _wpfShapeToDxfEntityMap contains key) MET");
                Trace.Flush();

                // var dxfEntity = _wpfShapeToDxfEntityMap[clickedShape]; // No longer needed due to TryGetValue
                Trace.WriteLine($"  -- Retrieved dxfEntity: {dxfEntity?.GetType().Name ?? "null"}");
                Debug.WriteLine($"[DEBUG] OnCadEntityClicked: Retrieved DxfEntity: {dxfEntity?.GetType().Name}");
                Trace.Flush();

                Point clickPosCanvas = e.GetPosition(CadCanvas);
                Debug.WriteLine($"[DEBUG] OnCadEntityClicked: Click position on Canvas = {clickPosCanvas}");
                Point clickPosDxf = _transformGroup.Inverse.Transform(clickPosCanvas);
                Debug.WriteLine($"[DEBUG] OnCadEntityClicked: Click position transformed to DXF Coords = {clickPosDxf}");

                Rect entityDxfBounds = GetDxfEntityRect(dxfEntity); // Helper method to be added
                Debug.WriteLine($"[DEBUG] OnCadEntityClicked: DXF Entity Bounds = {entityDxfBounds}");
                if (entityDxfBounds != Rect.Empty)
                {
                    Debug.WriteLine($"[DEBUG] OnCadEntityClicked: Does transformed click fall within entity bounds? {entityDxfBounds.Contains(clickPosDxf)}");
                }
                
                // Logic for adding/removing from current spray pass's trajectories
                Trace.WriteLine($"  -- Checking current pass index: {_currentConfiguration.CurrentPassIndex}, SprayPasses count: {_currentConfiguration.SprayPasses?.Count ?? 0}");
                Trace.Flush();
                if (_currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count)
                {
                    Trace.WriteLine("  -- Current pass index invalid, returning.");
                    Trace.Flush();
                    MessageBox.Show("Please select or create a spray pass first.", "No Active Pass", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];

                var existingTrajectory = currentPass.Trajectories.FirstOrDefault(t => t.OriginalDxfEntity == dxfEntity);

                if (existingTrajectory != null)
                {
                    // Entity is already part of the current pass. Mark it for selection.
                    Trace.WriteLine("  -- Existing trajectory found. Selecting it.");
                    Trace.Flush();
                    trajectoryToSelect = existingTrajectory;
                    // Do NOT remove it from currentPass.Trajectories.
                    // Do NOT set isConfigurationDirty = true just for re-selecting.
                }
                else
                {
                    // Select - Add to current pass
                    Trace.WriteLine("  -- No existing trajectory. Creating and adding new one.");
                    Trace.Flush();
                    var newTrajectory = new Trajectory
                    {
                        OriginalDxfEntity = dxfEntity,
                        EntityType = dxfEntity.GetType().Name, // General type, can be overridden by PrimitiveType
                        IsReversed = false // Default, can be changed by specific logic below or UI
                    };

                    switch (dxfEntity)
                    {
                        case DxfLine line:
                            newTrajectory.PrimitiveType = "Line";
                            double p1DistSq = line.P1.X * line.P1.X + line.P1.Y * line.P1.Y + line.P1.Z * line.P1.Z;
                            double p2DistSq = line.P2.X * line.P2.X + line.P2.Y * line.P2.Y + line.P2.Z * line.P2.Z;
                            if (p1DistSq <= p2DistSq)
                            {
                                newTrajectory.LineStartPoint = line.P1;
                                newTrajectory.LineEndPoint = line.P2;
                            }
                            else
                            {
                                newTrajectory.LineStartPoint = line.P2;
                                newTrajectory.LineEndPoint = line.P1;
                            }
                            break;
                        case DxfArc arc:
                            newTrajectory.PrimitiveType = "Arc";
                            // Calculate P1, P2 (mid), P3 for the arc using DxfArc properties
                            double startRad = arc.StartAngle * Math.PI / 180.0;
                            double endRad = arc.EndAngle * Math.PI / 180.0;

                            // P1 (Start Point)
                            newTrajectory.ArcPoint1.Coordinates = new DxfPoint(
                                arc.Center.X + arc.Radius * Math.Cos(startRad),
                                arc.Center.Y + arc.Radius * Math.Sin(startRad),
                                arc.Center.Z // Assuming arc is planar, Z is from center
                            );

                            // P3 (End Point)
                            newTrajectory.ArcPoint3.Coordinates = new DxfPoint(
                                arc.Center.X + arc.Radius * Math.Cos(endRad),
                                arc.Center.Y + arc.Radius * Math.Sin(endRad),
                                arc.Center.Z
                            );

                            // P2 (Mid Point Angle)
                            // Ensure angles are handled correctly for sweep (e.g. Start=350, End=10)
                            if (endRad < startRad) // Adjust if end angle is "smaller" due to wrap around
                            {
                                endRad += 2 * Math.PI;
                            }
                            double midRad = (startRad + endRad) / 2.0;
                            newTrajectory.ArcPoint2.Coordinates = new DxfPoint(
                                arc.Center.X + arc.Radius * Math.Cos(midRad),
                                arc.Center.Y + arc.Radius * Math.Sin(midRad),
                                arc.Center.Z
                            );
                            // Rx, Ry, Rz for ArcPoint1, ArcPoint2, ArcPoint3 will default to 0.0
                            break;
                        case DxfCircle circle:
                            newTrajectory.PrimitiveType = "Circle";
                            newTrajectory.CircleCenter = circle.Center;
                            newTrajectory.CircleRadius = circle.Radius;
                            newTrajectory.CircleNormal = circle.Normal;
                            break;
                        default:
                            newTrajectory.PrimitiveType = dxfEntity.GetType().Name;
                            break;
                    }
                    PopulateTrajectoryPoints(newTrajectory);
                    currentPass.Trajectories.Add(newTrajectory);
                    isConfigurationDirty = true;
                    trajectoryToSelect = newTrajectory; // Mark this new trajectory for selection
                }

                RefreshCurrentPassTrajectoriesListBox();

                if (trajectoryToSelect != null)
                {
                    CurrentPassTrajectoriesListBox.SelectedItem = trajectoryToSelect;
                    Trace.WriteLine($"  -- Explicitly set CurrentPassTrajectoriesListBox.SelectedItem to: {trajectoryToSelect.ToString()}");
                }
                else if (existingTrajectory != null) // It was a deselection, and existingTrajectory was the one removed
                {
                    Trace.WriteLine($"  -- Trajectory deselected. CurrentPassTrajectoriesListBox.SelectedItem is now: {CurrentPassTrajectoriesListBox.SelectedItem?.ToString() ?? "null"}");
                }
                Trace.Flush();

                RefreshCadCanvasHighlights();
                StatusTextBlock.Text = $"Selected {currentPass.Trajectories.Count} trajectories in {currentPass.PassName}.";

                Trace.WriteLine("  -- About to call UpdateDirectionIndicator from OnCadEntityClicked");
                Trace.Flush();
                UpdateDirectionIndicator(); // Selection changed by clicking CAD entity
                UpdateOrderNumberLabels();
            }
            else
            {
                Trace.WriteLine("  -- Condition (sender is Shape AND _wpfShapeToDxfEntityMap contains key) FAILED");
                Trace.Flush();
                // It's possible that the click was on the canvas background or another UI element not part of the DXF drawing.
                // No return needed here unless this path should explicitly not do anything further.
            }
        }

        /// <summary>
        /// Updates the trajectory preview by drawing polylines for selected entities.
        /// </summary>
        private void UpdateTrajectoryPreview()
        {
            // Clear existing trajectory previews
            foreach (var polyline in _trajectoryPreviewPolylines)
            {
                CadCanvas.Children.Remove(polyline);
            }
            _trajectoryPreviewPolylines.Clear();

            // Generate preview for selected entities
            foreach (var entity in _selectedDxfEntities)
            {
                List<System.Windows.Point> points = new List<System.Windows.Point>();
                
                switch (entity)
                {
                    case DxfLine line:
                        points = _cadService.ConvertLineToPoints(line);
                        break;
                    case DxfArc arc:
                        points = _cadService.ConvertArcToPoints(arc, TrajectoryPointResolutionAngle);
                        break;
                    case DxfCircle circle:
                        points = _cadService.ConvertCircleToPoints(circle, TrajectoryPointResolutionAngle);
                        break;
                }

                if (points.Count > 1)
                {
                    var polyline = new System.Windows.Shapes.Polyline
                    {
                        Points = new System.Windows.Media.PointCollection(points),
                        Stroke = Brushes.Red,
                        StrokeThickness = SelectedStrokeThickness,
                        StrokeDashArray = new System.Windows.Media.DoubleCollection { 5, 3 },
                        Tag = TrajectoryPreviewTag
                    };
                    
                    _trajectoryPreviewPolylines.Add(polyline);
                    CadCanvas.Children.Add(polyline);
                }
            }
        }

        /// <summary>
        /// Creates a configuration object from the current application state.
        /// </summary>
        private Models.Configuration CreateConfigurationFromCurrentState(bool forSaving = false)
        {
            // This method now primarily ensures the ProductName is up-to-date in _currentConfiguration.
            // The actual SprayPasses and Trajectories are modified directly by UI interactions.
            _currentConfiguration.ProductName = ProductNameTextBox.Text;

            // The old logic of creating a single trajectory from _selectedDxfEntities is removed.
            // That list might be empty or used differently now.
            // If there's a need to explicitly "commit" selections from a temporary list to the current pass,
            // that logic would go here or be part of the selection process itself.

            // For now, we assume _currentConfiguration is the source of truth and is being updated live.
            return _currentConfiguration;
        }
        private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
        {
            bool success = PerformSaveOperation(); // PerformSaveOperation will handle dialogs and actual saving
            if (success)
            {
                isConfigurationDirty = false;
                // StatusTextBlock.Text is likely updated within PerformSaveOperation or can be set here
                // For example: StatusTextBlock.Text = $"Configuration saved to {Path.GetFileName(_currentLoadedConfigPath)}";
                // This depends on how much PerformSaveOperation handles.
                // For now, just focus on calling it and setting the dirty flag.
            }
            else
            {
                // Optional: Update status if save failed or was cancelled within PerformSaveOperation
                // StatusTextBlock.Text = "Save configuration cancelled or failed.";
            }
        }
        private void LoadConfigButton_Click(object sender, RoutedEventArgs e)
        {
            bool canProceed = PromptAndTrySaveChanges();
            if (!canProceed)
            {
                StatusTextBlock.Text = "Load configuration cancelled due to unsaved changes."; // Optional feedback
                return;
            }

            OpenFileDialog openFileDialog = new OpenFileDialog {
                Filter = "Config files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Load Configuration File"
            };
            // Set initial directory (similar to LoadDxfButton_Click for consistency)
            string initialDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "RobTeachProject", "RobTeach", "Configurations"));
            if (!Directory.Exists(initialDir)) initialDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Configurations"); // Fallback
            openFileDialog.InitialDirectory = initialDir;

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    _currentConfiguration = _configService.LoadConfiguration(openFileDialog.FileName);
                    if (_currentConfiguration == null)
                    {
                        MessageBox.Show("Failed to load configuration file. The file might be corrupt or not a valid configuration.", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        StatusTextBlock.Text = "Error: Failed to deserialize configuration.";
                        // Initialize a default empty configuration to prevent null reference issues later
                        _currentConfiguration = new Models.Configuration { ProductName = $"Product_{DateTime.Now:yyyyMMddHHmmss}" };
                        // No further processing if config is null
                    }

                    ProductNameTextBox.Text = _currentConfiguration.ProductName;

                    // Restore Modbus Settings
                    ModbusIpAddressTextBox.Text = _currentConfiguration.ModbusIpAddress;
                    ModbusPortTextBox.Text = _currentConfiguration.ModbusPort.ToString();

                    // Restore Canvas View State
                    if (_currentConfiguration.CanvasState != null)
                    {
                        _scaleTransform.ScaleX = _currentConfiguration.CanvasState.ScaleX;
                        _scaleTransform.ScaleY = _currentConfiguration.CanvasState.ScaleY;
                        _translateTransform.X = _currentConfiguration.CanvasState.TranslateX;
                        _translateTransform.Y = _currentConfiguration.CanvasState.TranslateY;
                    }

                    // Load Embedded DXF Content
                    if (!string.IsNullOrEmpty(_currentConfiguration.DxfFileContent))
                    {
                        // Reset current DXF state
                        CadCanvas.Children.Clear();
                        _wpfShapeToDxfEntityMap.Clear();
                        _trajectoryPreviewPolylines.Clear();
                        _selectedDxfEntities.Clear();
                        _dxfEntityHandleMap.Clear();
                        _currentDxfDocument = null;
                        _dxfBoundingBox = Rect.Empty;
                        _currentDxfFilePath = "(Embedded DXF from project file)";

                        try
                        {
                            using (var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(_currentConfiguration.DxfFileContent)))
                            {
                                _currentDxfDocument = DxfFile.Load(memoryStream);
                            }

                            if (_currentDxfDocument != null)
                            {
                                List<System.Windows.Shapes.Shape> wpfShapes = _cadService.GetWpfShapesFromDxf(_currentDxfDocument);
                                int shapeIndex = 0;
                                foreach(var entity in _currentDxfDocument.Entities)
                                {
                                    if (shapeIndex < wpfShapes.Count && wpfShapes[shapeIndex] != null)
                                    {
                                        var wpfShape = wpfShapes[shapeIndex];
                                        wpfShape.Stroke = DefaultStrokeBrush;
                                        wpfShape.StrokeThickness = DefaultStrokeThickness;
                                        wpfShape.MouseLeftButtonDown += OnCadEntityClicked;
                                        _wpfShapeToDxfEntityMap[wpfShape] = entity;
                                        CadCanvas.Children.Add(wpfShape);
                                        shapeIndex++;
                                    }
                                }
                                _dxfBoundingBox = GetDxfBoundingBox(_currentDxfDocument);
                                PerformFitToView();
                                StatusTextBlock.Text = "Loaded embedded DXF and configuration from project file.";
                            }
                            else // Should ideally be caught by DxfFile.Load exception
                            {
                                StatusTextBlock.Text = "Project file loaded, but embedded DXF content was invalid or empty.";
                                _currentDxfDocument = null; // Ensure it's null
                            }
                        }
                        catch (Exception dxfEx)
                        {
                            StatusTextBlock.Text = "Project file loaded, but failed to load embedded DXF content.";
                            MessageBox.Show($"Failed to load embedded DXF content from the project file. It might be corrupt.\nError: {dxfEx.Message}", "DXF Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            _currentDxfDocument = null; // Ensure it's null
                             // Clear canvas again in case partial loading happened before error, or if an error occurred in GetWpfShapesFromDxf
                            CadCanvas.Children.Clear();
                            _wpfShapeToDxfEntityMap.Clear();
                        }
                    }
                    else
                    {
                        // No embedded DXF content, clear existing DXF from canvas
                        CadCanvas.Children.Clear();
                        _wpfShapeToDxfEntityMap.Clear();
                        _trajectoryPreviewPolylines.Clear();
                        _selectedDxfEntities.Clear();
                        _dxfEntityHandleMap.Clear();
                        _currentDxfDocument = null;
                        _currentDxfFilePath = null; // No active DXF file path
                        _dxfBoundingBox = Rect.Empty;
                        PerformFitToView(); // Reset view if no DXF
                        StatusTextBlock.Text = "Configuration loaded (no embedded DXF).";
                    }

                    // Initialize Spray Passes from loaded configuration
                    if (_currentConfiguration.SprayPasses == null || !_currentConfiguration.SprayPasses.Any())
                    {
                        // If loaded config has no passes, create a default one.
                        _currentConfiguration.SprayPasses = new List<SprayPass> { new SprayPass { PassName = "Default Pass 1" } };
                        _currentConfiguration.CurrentPassIndex = 0;
                    }
                    else if (_currentConfiguration.CurrentPassIndex < 0 || _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count)
                    {
                        // If index is invalid, default to first pass.
                        _currentConfiguration.CurrentPassIndex = _currentConfiguration.SprayPasses.Any() ? 0 : -1;
                    }

                    SprayPassesListBox.ItemsSource = null; // Refresh
                    SprayPassesListBox.ItemsSource = _currentConfiguration.SprayPasses;
                    if (_currentConfiguration.CurrentPassIndex >= 0 && _currentConfiguration.CurrentPassIndex < SprayPassesListBox.Items.Count)
                    {
                         SprayPassesListBox.SelectedIndex = _currentConfiguration.CurrentPassIndex;
                    }
                    else if (SprayPassesListBox.Items.Count > 0)
                    {
                        SprayPassesListBox.SelectedIndex = 0; // Fallback to selecting the first if index is out of sync
                        _currentConfiguration.CurrentPassIndex = 0;
                    }


                    // The old global nozzle checkbox updates are removed.
                    // UpperNozzleOnCheckBox_Changed(null, null); // Removed
                    // LowerNozzleOnCheckBox_Changed(null, null); // Removed

                    RefreshCurrentPassTrajectoriesListBox(); // Update trajectory list for the (newly) current pass

                    // Restore selected trajectory index for the current pass
                    if (_currentConfiguration.SelectedTrajectoryIndexInCurrentPass >= 0 &&
                        _currentConfiguration.SelectedTrajectoryIndexInCurrentPass < CurrentPassTrajectoriesListBox.Items.Count)
                    {
                        CurrentPassTrajectoriesListBox.SelectedIndex = _currentConfiguration.SelectedTrajectoryIndexInCurrentPass;
                    }
                    // else, no valid selection or list is empty, ListBox default behavior (no selection or first item)

                    // Reconcile DxfEntity instances if DXF was loaded from embedded content
                    if (!string.IsNullOrEmpty(_currentConfiguration.DxfFileContent) && _currentDxfDocument != null)
                    {
                        ReconcileTrajectoryEntities(_currentConfiguration, _currentDxfDocument);
                    }

                    UpdateSelectedTrajectoryDetailUI(); // Renamed: Update nozzle UI for potentially selected trajectory
                    RefreshCadCanvasHighlights(); // Update canvas highlights for the loaded pass
                    UpdateDirectionIndicator(); // Config loaded, selection might have changed
                    UpdateOrderNumberLabels();

                    // Assuming _cadService.GetWpfShapesFromDxf and entity selection logic
                    // might need to be re-run or updated if the config implies specific CAD entities.
                    // For now, just loading configuration values. Future work might involve
                    // re-selecting entities based on handles stored in config if _currentDxfDocument is still relevant.
                    isConfigurationDirty = false;
                    StatusTextBlock.Text = $"Configuration loaded from {Path.GetFileName(openFileDialog.FileName)}";
                    _currentLoadedConfigPath = openFileDialog.FileName;
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text = "Error loading configuration.";
                    MessageBox.Show($"Failed to load configuration: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    // Reset to a default state if loading fails
                    _currentConfiguration = new Models.Configuration { ProductName = $"Product_{DateTime.Now:yyyyMMddHHmmss}" };
                    ProductNameTextBox.Text = _currentConfiguration.ProductName;
                    // Reset new nozzle CheckBoxes in UI (they are per-trajectory, so this just clears the UI if no trajectory selected)
                    UpdateSelectedTrajectoryDetailUI(); // Renamed: This will clear them if nothing is selected
                    // The old global nozzle checkbox resets are removed.
                    // UpperNozzleOnCheckBox_Changed(null, null); // Removed
                    // LowerNozzleOnCheckBox_Changed(null, null); // Removed
                    isConfigurationDirty = false;
                    UpdateDirectionIndicator(); // Clear indicator if error during load
                    UpdateOrderNumberLabels();
                }
            }
            else
            {
                StatusTextBlock.Text = "Load configuration cancelled.";
            }
        }
        private void ModbusConnectButton_Click(object sender, RoutedEventArgs e)
        {
            string ipAddress = ModbusIpAddressTextBox.Text;
            string portString = ModbusPortTextBox.Text;

            if (string.IsNullOrEmpty(ipAddress))
            {
                MessageBox.Show("IP address cannot be empty.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!int.TryParse(portString, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Invalid port number. Please enter a number between 1 and 65535.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ModbusResponse response = _modbusService.Connect(ipAddress, port);
            ModbusStatusTextBlock.Text = response.Message;

            if (response.Success)
            {
                ModbusStatusIndicatorEllipse.Fill = Brushes.Green;
                ModbusConnectButton.IsEnabled = false;
                ModbusDisconnectButton.IsEnabled = true;
                SendToRobotButton.IsEnabled = true;
                StatusTextBlock.Text = "Successfully connected to Modbus server.";
            }
            else
            {
                ModbusStatusIndicatorEllipse.Fill = Brushes.Red;
                StatusTextBlock.Text = "Failed to connect to Modbus server.";
            }
        }

        private void ModbusDisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _modbusService.Disconnect();
            ModbusStatusTextBlock.Text = "Disconnected";
            ModbusStatusIndicatorEllipse.Fill = Brushes.Red;
            ModbusConnectButton.IsEnabled = true;
            ModbusDisconnectButton.IsEnabled = false;
            SendToRobotButton.IsEnabled = false;
            StatusTextBlock.Text = "Disconnected from Modbus server.";
        }

        private void SendToRobotButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_modbusService.IsConnected)
            {
                MessageBox.Show("Not connected to Modbus server. Please connect first.", "Modbus Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Ensure points are populated for the trajectories in the current pass
            if (_currentConfiguration.CurrentPassIndex >= 0 &&
                _currentConfiguration.CurrentPassIndex < _currentConfiguration.SprayPasses.Count)
            {
                SprayPass currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
                if (currentPass.Trajectories != null)
                {
                    foreach (var trajectory in currentPass.Trajectories)
                    {
                        PopulateTrajectoryPoints(trajectory); // Assumes PopulateTrajectoryPoints(trajectory) exists and is accessible
                    }
                }
            }
            else if (_currentConfiguration.SprayPasses == null || _currentConfiguration.SprayPasses.Count == 0)
            {
                // No spray passes, so nothing to populate. SendConfiguration will handle this.
            }
            else
            {
                // Invalid CurrentPassIndex, but SprayPasses exist.
                // SendConfiguration will return an error for invalid index.
                // No points to populate here for sending.
                // Optionally, show a MessageBox here too, but ModbusService will also report it.
                MessageBox.Show($"Cannot send: Invalid current spray pass index ({_currentConfiguration.CurrentPassIndex}).", "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return; // Prevent sending if pass index is clearly wrong but passes exist
            }

            _currentConfiguration.ProductName = ProductNameTextBox.Text; // Ensure latest product name
            ModbusResponse response = _modbusService.SendConfiguration(_currentConfiguration);

            // Using StatusTextBlock for general feedback seems more consistent with other operations
            if (response.Success)
            {
                StatusTextBlock.Text = "Configuration successfully sent to robot.";
                ModbusStatusTextBlock.Text = response.Message; // Keep specific Modbus status updated too
            }
            else
            {
                StatusTextBlock.Text = $"Failed to send configuration: {response.Message}";
                ModbusStatusTextBlock.Text = response.Message; // Update Modbus status as well
            }
        }

        /// <summary>
        /// Calculates the overall bounding box of the DXF document, considering header extents and all entity extents.
        /// </summary>
        /// <param name="dxfDoc">The DXF document.</param>
        /// <returns>A Rect representing the bounding box, or Rect.Empty if no valid bounds can be determined.</returns>
        private Rect GetDxfBoundingBox(DxfFile dxfDoc)
        {
            if (dxfDoc == null)
            {
                return Rect.Empty;
            }

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            bool hasValidBounds = false;

            // Calculate bounds directly from entities
            if (dxfDoc.Entities != null && dxfDoc.Entities.Any())
            {
                foreach (var entity in dxfDoc.Entities)
                {
                    if (entity == null) continue;

                    try
                    {
                        // Calculate entity bounds directly
                        var bounds = CalculateEntityBoundsSimple(entity);
                        if (bounds.HasValue)
                        {
                            var (eMinX, eMinY, eMaxX, eMaxY) = bounds.Value;
                            minX = Math.Min(minX, eMinX);
                            minY = Math.Min(minY, eMinY);
                            maxX = Math.Max(maxX, eMaxX);
                            maxY = Math.Max(maxY, eMaxY);
                            hasValidBounds = true;
                        }
                    }
                    catch
                    {
                        // Skip entities that can't be processed
                        continue;
                    }
                }
            }

            if (!hasValidBounds)
            {
                return Rect.Empty;
            }

            return new System.Windows.Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private void FitToViewButton_Click(object sender, RoutedEventArgs e) { Debug.WriteLine("[DEBUG] FitToViewButton_Click called."); PerformFitToView(); }
        private void PerformFitToView()
        {
            Debug.WriteLine("[DEBUG] PerformFitToView: Entered.");
            Debug.WriteLine($"[DEBUG] PerformFitToView: CadCanvas.ActualWidth={CadCanvas.ActualWidth}, CadCanvas.ActualHeight={CadCanvas.ActualHeight}");
            Debug.WriteLine($"[DEBUG] PerformFitToView: _dxfBoundingBox={_dxfBoundingBox.ToString()}");

            if (_dxfBoundingBox.IsEmpty || CadCanvas.ActualWidth == 0 || CadCanvas.ActualHeight == 0)
            {
                Debug.WriteLine("[DEBUG] PerformFitToView: BoundingBox is empty or Canvas size is zero. Resetting transforms.");
                // Reset to default if no content or canvas not ready
                _scaleTransform.ScaleX = 1;
                _scaleTransform.ScaleY = 1;
                _translateTransform.X = 0;
                _translateTransform.Y = 0;
                Debug.WriteLine("[DEBUG] PerformFitToView: Exiting due to empty bounds or zero canvas size.");
                return;
            }

            double canvasWidth = CadCanvas.ActualWidth;
            double canvasHeight = CadCanvas.ActualHeight;

            double contentWidth = _dxfBoundingBox.Width;
            double contentHeight = _dxfBoundingBox.Height;

            if (contentWidth == 0 || contentHeight == 0)
            {
                Debug.WriteLine("[DEBUG] PerformFitToView: ContentWidth or ContentHeight is zero. Exiting.");
                return; // Avoid division by zero
            }

            // Calculate scale to fit content within canvas, maintaining aspect ratio
            double scaleX = canvasWidth / contentWidth;
            double scaleY = canvasHeight / contentHeight;
            double scale = Math.Min(scaleX, scaleY);

            // Apply a margin, e.g., 5% of the canvas dimension
            double marginFactor = 0.95;
            scale *= marginFactor;
            Debug.WriteLine($"[DEBUG] PerformFitToView: Calculated scaleX={scaleX}, scaleY={scaleY}, final scale (with margin)={scale}");

            _scaleTransform.ScaleX = scale;
            _scaleTransform.ScaleY = scale; // Maintain aspect ratio

            // Align content to the top-left with a small padding
            double targetTranslateX = -(_dxfBoundingBox.X * scale); // Align left edge of content with left edge of canvas
            const double topPadding = 5.0; // Define a small padding from the top of the canvas
            double targetTranslateY = topPadding -(_dxfBoundingBox.Y * scale);
            // This still aligns the bounding box top, but ensures it's at worst at Y=topPadding if _dxfBoundingBox.Y is 0.
            // If the diagnostic Y=0 worked, and original -(_dxfBoundingBox.Y * scale) didn't,
            // it implies -(_dxfBoundingBox.Y * scale) was too negative.
            // Let's try a simpler version that worked for the diagnostic: Y=0 of DXF to Y=padding of canvas.
            // This means the Y-origin of the DXF drawing is placed at Y=topPadding on the canvas.
            targetTranslateY = topPadding; // All DXFs will start rendering their Y=0 coordinate at Y=topPadding of canvas.

            _translateTransform.X = targetTranslateX;
            _translateTransform.Y = targetTranslateY;
            Debug.WriteLine($"[DEBUG] PerformFitToView: TargetTranslateX (Left Aligned)={targetTranslateX}, TargetTranslateY (Top Padded to {topPadding})={targetTranslateY}");

            StatusTextBlock.Text = "View fitted to content.";
            Debug.WriteLine("[DEBUG] PerformFitToView: Completed.");
        }
        private void CadCanvas_MouseWheel(object sender, MouseWheelEventArgs e) { /* ... (No change) ... */ }
        private void CadCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Pressed ||
                (e.LeftButton == MouseButtonState.Pressed && Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) ) // More robust Ctrl check
            {
                _isPanning = true;
                _panStartPoint = e.GetPosition(this); // Pan relative to MainWindow for this example, or CadCanvas.Parent
                CadCanvas.CaptureMouse();
                StatusTextBlock.Text = "Panning...";
                e.Handled = true;
            }
            // Check e.Source to ensure the click originated on the Canvas itself, not on a child like a DxfEntity shape
            else if (e.LeftButton == MouseButtonState.Pressed && e.Source == CadCanvas)
            {
                isSelectingWithRect = true;
                selectionStartPoint = e.GetPosition(CadCanvas);

                if (selectionRectangleUI != null && CadCanvas.Children.Contains(selectionRectangleUI))
                {
                    CadCanvas.Children.Remove(selectionRectangleUI);
                }
                selectionRectangleUI = null; // Ensure it's null before creating a new one

                selectionRectangleUI = new System.Windows.Shapes.Rectangle
                {
                    Stroke = Brushes.DodgerBlue, // Using SelectedStrokeBrush color
                    StrokeThickness = 1,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 3, 2 },
                    Fill = new SolidColorBrush(Color.FromArgb(40, 0, 120, 255)) // Light semi-transparent blue
                };

                Canvas.SetLeft(selectionRectangleUI, selectionStartPoint.X);
                Canvas.SetTop(selectionRectangleUI, selectionStartPoint.Y);
                selectionRectangleUI.Width = 0;
                selectionRectangleUI.Height = 0;

                // Add to canvas before trying to set ZIndex, though ZIndex might not be strictly needed here
                // as it's temporary.
                CadCanvas.Children.Add(selectionRectangleUI);
                // Panel.SetZIndex(selectionRectangleUI, int.MaxValue); // Optional: ensure it's on top

                CadCanvas.CaptureMouse();
                StatusTextBlock.Text = "Defining selection area...";
                e.Handled = true;
            }
            // If not handled, and it was a left click, it might be on a DxfEntity shape,
            // which would be handled by OnCadEntityClicked due to event bubbling if that shape has a handler.
        }
        private void CadCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                System.Windows.Point currentPanPoint = e.GetPosition(this); // Or CadCanvas.Parent as IInputElement
                Vector panDelta = currentPanPoint - _panStartPoint;
                _translateTransform.X += panDelta.X;
                _translateTransform.Y += panDelta.Y;
                _panStartPoint = currentPanPoint;
                e.Handled = true;
            }
            else if (isSelectingWithRect && selectionRectangleUI != null)
            {
                System.Windows.Point currentMousePos = e.GetPosition(CadCanvas);

                double x = Math.Min(selectionStartPoint.X, currentMousePos.X);
                double y = Math.Min(selectionStartPoint.Y, currentMousePos.Y);
                double width = Math.Abs(selectionStartPoint.X - currentMousePos.X);
                double height = Math.Abs(selectionStartPoint.Y - currentMousePos.Y);

                Canvas.SetLeft(selectionRectangleUI, x);
                Canvas.SetTop(selectionRectangleUI, y);
                selectionRectangleUI.Width = width;
                selectionRectangleUI.Height = height;

                e.Handled = true;
            }
        }
        private void CadCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                CadCanvas.ReleaseMouseCapture();
                StatusTextBlock.Text = "Pan complete.";
                e.Handled = true;
            }
            else if (isSelectingWithRect)
            {
                isSelectingWithRect = false;
                CadCanvas.ReleaseMouseCapture();

                if (selectionRectangleUI == null) // Should not happen if isSelectingWithRect was true
                {
                    e.Handled = true;
                    return;
                }

                Rect finalSelectionRect = new Rect(
                    Canvas.GetLeft(selectionRectangleUI),
                    Canvas.GetTop(selectionRectangleUI),
                    selectionRectangleUI.Width,
                    selectionRectangleUI.Height);

                // Remove the visual rectangle from canvas
                CadCanvas.Children.Remove(selectionRectangleUI);
                selectionRectangleUI = null;

                StatusTextBlock.Text = "Selection processed."; // Or clear
                Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Marquee): finalSelectionRect (Canvas UI Coords) = {finalSelectionRect}");
                Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Marquee): _scaleTransform=({_scaleTransform.ScaleX},{_scaleTransform.ScaleY}), _translateTransform=({_translateTransform.X},{_translateTransform.Y})");

                // Small drag check (if it was more of a click)
                const double clickThreshold = 5.0;
                if (finalSelectionRect.Width < clickThreshold && finalSelectionRect.Height < clickThreshold)
                {
                    e.Handled = true; // Event handled, but no marquee selection performed
                    return;
                }

                // Check for a valid current pass
                if (_currentConfiguration == null || _currentConfiguration.CurrentPassIndex < 0 ||
                    _currentConfiguration.CurrentPassIndex >= _currentConfiguration.SprayPasses.Count ||
                    _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex].Trajectories == null)
                {
                    MessageBox.Show("Please select or create a spray pass first to add entities.", "No Active Pass", MessageBoxButton.OK, MessageBoxImage.Information);
                    e.Handled = true;
                    return;
                }
                var currentPass = _currentConfiguration.SprayPasses[_currentConfiguration.CurrentPassIndex];
                bool selectionStateChanged = false;
                List<DxfEntity> marqueeHitEntities = new List<DxfEntity>();

                RectangleGeometry selectionGeometry = new RectangleGeometry(finalSelectionRect);
                GeometryHitTestParameters parameters = new GeometryHitTestParameters(selectionGeometry);

                HitTestResultCallback hitTestCallback = (HitTestResult result) =>
                {
                    if (result is GeometryHitTestResult geometryResult)
                    {
                        if (geometryResult.VisualHit is System.Windows.Shapes.Shape hitShape)
                        {
                            if (_wpfShapeToDxfEntityMap.TryGetValue(hitShape, out DxfEntity? hitEntity) && hitEntity != null)
                            {
                                if (!marqueeHitEntities.Contains(hitEntity))
                                {
                                    marqueeHitEntities.Add(hitEntity);
                                    Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Marquee HitTest): Hit DxfEntity {hitEntity.GetType().Name}");
                                }
                            }
                        }
                    }
                    return HitTestResultBehavior.Continue;
                };

                // Optional: HitTestFilterCallback to quickly prune visuals not in _wpfShapeToDxfEntityMap.Keys
                // For now, let the result callback handle it.
                VisualTreeHelper.HitTest(CadCanvas, null, hitTestCallback, parameters);

                Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Marquee HitTest): Found {marqueeHitEntities.Count} entities in marquee.");

                // Removed outer declaration and population of newPassTrajectories.
                // This logic is now handled within the 'else' block for Replace Mode.

                bool isShiftPressed = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp: ShiftPressed={isShiftPressed}, Marquee Hits={marqueeHitEntities.Count}");

                if (isShiftPressed)
                {
                    // Mode: Deselect within marquee (Shift + Marquee)
                    // Remove any currently selected items that are within the marquee.
                    int itemsDeselectedCount = 0;
                    for (int i = currentPass.Trajectories.Count - 1; i >= 0; i--)
                    {
                        Trajectory trajectory = currentPass.Trajectories[i];
                        if (marqueeHitEntities.Contains(trajectory.OriginalDxfEntity))
                        {
                            Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Shift+Marquee): Deselecting {trajectory.OriginalDxfEntity.GetType().Name}");
                            currentPass.Trajectories.RemoveAt(i);
                            itemsDeselectedCount++;
                        }
                    }
                    if (itemsDeselectedCount > 0)
                    {
                        selectionStateChanged = true;
                    }
                }
                else
                {
                    // Mode: Additive selection (Normal Marquee)
                    // Add any items from the marquee that are not already selected.
                    int itemsAddedCount = 0;
                    foreach (DxfEntity? hitDxfEntity in marqueeHitEntities) // hitDxfEntity can be null if TryGetValue failed but was added
                    {
                        if (hitDxfEntity == null) continue; // Skip null entities

                        // Use geometric comparison to check if already selected, due to potential instance differences
                        bool alreadySelected = currentPass.Trajectories.Any(t => t.OriginalDxfEntity != null && AreEntitiesGeometricallyEquivalent(t.OriginalDxfEntity, hitDxfEntity));
                        if (!alreadySelected)
                        {
                            Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Normal Marquee - Additive): Adding {hitDxfEntity.GetType().Name} as it's not geometrically equivalent to any existing selected trajectory's entity.");
                            var newTrajectory = new Trajectory
                            {
                                OriginalDxfEntity = hitDxfEntity,
                                EntityType = hitDxfEntity.GetType().Name, // Safe due to null check above
                                IsReversed = false // Default
                            };
                            // Populate geometric properties for the new trajectory
                            switch (hitDxfEntity) // Safe due to null check above
                            {
                                case DxfLine line:
                                    newTrajectory.PrimitiveType = "Line";
                                    newTrajectory.LineStartPoint = line.P1;
                                    newTrajectory.LineEndPoint = line.P2;
                                    break;
                                case DxfArc arc:
                                    newTrajectory.PrimitiveType = "Arc";
                                    // Populate ArcPoint1, ArcPoint2, ArcPoint3 from DxfArc
                                    double startRadMarquee = arc.StartAngle * Math.PI / 180.0;
                                    double endRadMarquee = arc.EndAngle * Math.PI / 180.0;
                                    newTrajectory.ArcPoint1.Coordinates = new DxfPoint(
                                        arc.Center.X + arc.Radius * Math.Cos(startRadMarquee),
                                        arc.Center.Y + arc.Radius * Math.Sin(startRadMarquee),
                                        arc.Center.Z);
                                    newTrajectory.ArcPoint3.Coordinates = new DxfPoint(
                                        arc.Center.X + arc.Radius * Math.Cos(endRadMarquee),
                                        arc.Center.Y + arc.Radius * Math.Sin(endRadMarquee),
                                        arc.Center.Z);
                                    if (endRadMarquee < startRadMarquee) endRadMarquee += 2 * Math.PI;
                                    double midRadMarquee = (startRadMarquee + endRadMarquee) / 2.0;
                                    newTrajectory.ArcPoint2.Coordinates = new DxfPoint(
                                        arc.Center.X + arc.Radius * Math.Cos(midRadMarquee),
                                        arc.Center.Y + arc.Radius * Math.Sin(midRadMarquee),
                                        arc.Center.Z);
                                    break;
                                case DxfCircle circle:
                                    newTrajectory.PrimitiveType = "Circle";
                                    newTrajectory.CircleCenter = circle.Center;
                                    newTrajectory.CircleRadius = circle.Radius;
                                    newTrajectory.CircleNormal = circle.Normal;
                                    break;
                                default:
                                    newTrajectory.PrimitiveType = hitDxfEntity.GetType().Name; // Fallback
                                    break;
                            }
                            PopulateTrajectoryPoints(newTrajectory);
                            currentPass.Trajectories.Add(newTrajectory);
                            itemsAddedCount++;
                        }
                    }
                    if (itemsAddedCount > 0)
                    {
                        selectionStateChanged = true;
                    }
                }

                if (selectionStateChanged)
                {
                    Debug.WriteLine($"[DEBUG] CadCanvas_MouseUp (Marquee): Selection state changed. Refreshing UI.");
                    isConfigurationDirty = true;
                    RefreshCurrentPassTrajectoriesListBox();
                    RefreshCadCanvasHighlights();
                    UpdateDirectionIndicator();
                    UpdateOrderNumberLabels();
                    StatusTextBlock.Text = $"Selection updated in {currentPass.PassName}. Total: {currentPass.Trajectories.Count}.";
                }
                e.Handled = true;
            }
        }
        /// <summary>
        /// Calculates the bounding rectangle for a given DXF entity.
        /// </summary>
        private bool PerformSaveOperation()
        {
            // Call update methods at the beginning of save operation
            // These methods now use _trajectoryInDetailView, which should reflect the
            // trajectory whose details are currently shown (and potentially edited) in the UI.
            UpdateLineStartZFromTextBox();
            UpdateLineEndZFromTextBox();
            UpdateArcCenterZFromTextBox();
            UpdateCircleCenterZFromTextBox();

            // This logic is largely moved from the original SaveConfigButton_Click
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Filter = "Config files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Save Configuration File",
                FileName = $"{ProductNameTextBox.Text}.json" // Suggest filename
            };

            string initialDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "RobTeachProject", "RobTeach", "Configurations"));
            if (!Directory.Exists(initialDir))
            {
                initialDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Configurations"); // Fallback
            }
            if (!Directory.Exists(initialDir))
            {
                try
                {
                    Directory.CreateDirectory(initialDir);
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text = $"Error creating configurations directory: {ex.Message}";
                    // Optionally, default to a known accessible path or handle error differently
                    initialDir = AppDomain.CurrentDomain.BaseDirectory;
                }
            }
            saveFileDialog.InitialDirectory = initialDir;

            if (saveFileDialog.ShowDialog() == true)
            {
                // Ensure the _currentConfiguration object has the latest product name from the UI
                _currentConfiguration.ProductName = ProductNameTextBox.Text;

                // Populate DxfFileContent
                if (!string.IsNullOrEmpty(_currentDxfFilePath) && File.Exists(_currentDxfFilePath))
                {
                    try
                    {
                        _currentConfiguration.DxfFileContent = File.ReadAllText(_currentDxfFilePath);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Warning: Could not embed DXF file content in the project file: {ex.Message}");
                        MessageBox.Show($"Warning: Could not embed DXF file content in the project file: {ex.Message}", "File Read Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                        _currentConfiguration.DxfFileContent = string.Empty;
                    }
                }
                else
                {
                    _currentConfiguration.DxfFileContent = string.Empty;
                }

                // Populate Modbus Settings
                _currentConfiguration.ModbusIpAddress = ModbusIpAddressTextBox.Text;
                if (int.TryParse(ModbusPortTextBox.Text, out int parsedPort) && parsedPort >= 1 && parsedPort <= 65535)
                {
                    _currentConfiguration.ModbusPort = parsedPort;
                }
                else
                {
                    _currentConfiguration.ModbusPort = 502; // Default port
                }

                // Populate CanvasState
                _currentConfiguration.CanvasState ??= new CanvasViewSettings(); // Ensure not null
                _currentConfiguration.CanvasState.ScaleX = _scaleTransform.ScaleX;
                _currentConfiguration.CanvasState.ScaleY = _scaleTransform.ScaleY;
                _currentConfiguration.CanvasState.TranslateX = _translateTransform.X;
                _currentConfiguration.CanvasState.TranslateY = _translateTransform.Y;

                _currentConfiguration.SelectedTrajectoryIndexInCurrentPass = CurrentPassTrajectoriesListBox.SelectedIndex;
                // This is done before deciding what to filter into configToSave.

                Configuration configToSave = new Configuration
                {
                    ProductName = _currentConfiguration.ProductName,
                    CurrentPassIndex = _currentConfiguration.CurrentPassIndex, // Preserve the selected pass index
                    // NEW: Copy new fields
                    DxfFileContent = _currentConfiguration.DxfFileContent,
                    ModbusIpAddress = _currentConfiguration.ModbusIpAddress,
                    ModbusPort = _currentConfiguration.ModbusPort,
                    CanvasState = _currentConfiguration.CanvasState,
                    SelectedTrajectoryIndexInCurrentPass = _currentConfiguration.SelectedTrajectoryIndexInCurrentPass, // <-- ADD THIS LINE
                    SprayPasses = new List<SprayPass>()
                };

                if (_currentConfiguration.SprayPasses != null) // Ensure SprayPasses list itself is not null
                {
                    foreach (var originalPass in _currentConfiguration.SprayPasses)
                    {
                        // Only include passes that actually contain trajectories
                        if (originalPass.Trajectories != null && originalPass.Trajectories.Any())
                        {
                            SprayPass passToSave = new SprayPass
                            {
                                PassName = originalPass.PassName
                                // If SprayPass has other serializable properties, copy them here
                            };
                            // Copy the list of trajectories for this pass.
                            // These are the trajectories that are actively configured.
                            passToSave.Trajectories = new List<Trajectory>(originalPass.Trajectories);

                            configToSave.SprayPasses.Add(passToSave);
                        }
                    }
                }

                // Now, save the filtered configuration object
                try
                {
                    _configService.SaveConfiguration(configToSave, saveFileDialog.FileName);
                    StatusTextBlock.Text = $"Configuration saved to {Path.GetFileName(saveFileDialog.FileName)}";
                    _currentLoadedConfigPath = saveFileDialog.FileName; // Update current loaded path
                    return true; // Save successful
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text = "Error saving configuration.";
                    MessageBox.Show($"Failed to save configuration: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false; // Save failed
                }
            }
            else
            {
                StatusTextBlock.Text = "Save configuration cancelled.";
                return false; // User cancelled SaveFileDialog
            }
        }
        private (double minX, double minY, double maxX, double maxY)? CalculateEntityBoundsSimple(DxfEntity entity)
        {
            try
            {
                switch (entity)
                {
                    case DxfLine line:
                        var minX = Math.Min(line.P1.X, line.P2.X);
                        var maxX = Math.Max(line.P1.X, line.P2.X);
                        var minY = Math.Min(line.P1.Y, line.P2.Y);
                        var maxY = Math.Max(line.P1.Y, line.P2.Y);
                        return (minX, minY, maxX, maxY);

                    case DxfArc arc:
                        var centerX = arc.Center.X;
                        var centerY = arc.Center.Y;
                        var radius = arc.Radius;
                        return (centerX - radius, centerY - radius, centerX + radius, centerY + radius);

                    case DxfCircle circle:
                        var cX = circle.Center.X;
                        var cY = circle.Center.Y;
                        var r = circle.Radius;
                        return (cX - r, cY - r, cX + r, cY + r);

                    case DxfLwPolyline lwPolyline:
                        if (lwPolyline.Vertices == null || !lwPolyline.Vertices.Any())
                            return null;

                        var polyMinX = double.MaxValue;
                        var polyMinY = double.MaxValue;
                        var polyMaxX = double.MinValue;
                        var polyMaxY = double.MinValue;

                        foreach (var vertex in lwPolyline.Vertices)
                        {
                            polyMinX = Math.Min(polyMinX, vertex.X);
                            polyMinY = Math.Min(polyMinY, vertex.Y);
                            polyMaxX = Math.Max(polyMaxX, vertex.X);
                            polyMaxY = Math.Max(polyMaxY, vertex.Y);
                        }

                        // Iterate through segments for bulge handling
                        if (lwPolyline.Vertices.Count > 1)
                        {
                            for (int i = 0; i < lwPolyline.Vertices.Count; i++)
                            {
                                var p1Vertex = lwPolyline.Vertices[i];
                                var p2Vertex = lwPolyline.IsClosed ? lwPolyline.Vertices[(i + 1) % lwPolyline.Vertices.Count] :
                                                                    (i < lwPolyline.Vertices.Count - 1 ? lwPolyline.Vertices[i + 1] : null);

                                if (p2Vertex == null && i == lwPolyline.Vertices.Count - 1 && !lwPolyline.IsClosed)
                                {
                                    // Last vertex of an open polyline, already processed by initial vertex iteration.
                                    break;
                                }
                                if (p2Vertex == null) continue; // Should ideally not be reached if logic is sound

                                if (Math.Abs(p1Vertex.Bulge) > 1e-6) // Bulge is significant
                                {
                                    var arcSegBounds = GetArcSegmentBoundsFromBulge(
                                        new Point(p1Vertex.X, p1Vertex.Y),
                                        new Point(p2Vertex.X, p2Vertex.Y),
                                        p1Vertex.Bulge);

                                    if (arcSegBounds != Rect.Empty)
                                    {
                                        polyMinX = Math.Min(polyMinX, arcSegBounds.Left);
                                        polyMinY = Math.Min(polyMinY, arcSegBounds.Top);
                                        polyMaxX = Math.Max(polyMaxX, arcSegBounds.Right);
                                        polyMaxY = Math.Max(polyMaxY, arcSegBounds.Bottom);
                                    }
                                }
                            }
                        }
                        return (polyMinX, polyMinY, polyMaxX, polyMaxY);

                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private bool PromptAndTrySaveChanges()
        {
            if (!isConfigurationDirty)
            {
                return true; // No unsaved changes, proceed.
            }

            MessageBoxResult result = MessageBox.Show(
                "You have unsaved changes. Would you like to save the current configuration?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            switch (result)
            {
                case MessageBoxResult.Yes:
                    // PerformSaveOperation() will be implemented in a later step.
                    // Assume it returns true if save was successful, false if user cancelled or error.
                    bool saveSuccess = PerformSaveOperation();
                    if (saveSuccess)
                    {
                        isConfigurationDirty = false; // Reset dirty flag only if save was successful
                        return true; // Proceed with the original action
                    }
                    else
                    {
                        return false; // Save failed or was cancelled, so cancel the original action
                    }
                case MessageBoxResult.No:
                    return true; // User chose not to save, proceed with the original action
                case MessageBoxResult.Cancel:
                    return false; // User cancelled the original action
                default:
                    return false; // Should not happen
            }
        }


        private void HandleError(Exception ex, string action) { /* ... (No change) ... */ }


        private bool PointEquals(DxfPoint p1, DxfPoint p2, double tolerance = 0.001)
        {
            return Math.Abs(p1.X - p2.X) < tolerance &&
                   Math.Abs(p1.Y - p2.Y) < tolerance &&
                   Math.Abs(p1.Z - p2.Z) < tolerance;
        }

        private bool AreEntitiesGeometricallyEquivalent(DxfEntity entity1, DxfEntity entity2, double tolerance = 0.001)
        {
            if (entity1 == null || entity2 == null)
            {
                Debug.WriteLineIf(entity1 == null || entity2 == null, $"[DEBUG] AreEntitiesGeometricallyEquivalent: One or both entities are null. Entity1: {(entity1 == null ? "null" : entity1.GetType().Name)}, Entity2: {(entity2 == null ? "null" : entity2.GetType().Name)}");
                return false;
            }
            if (entity1.GetType() != entity2.GetType())
            {
                Debug.WriteLine($"[DEBUG] AreEntitiesGeometricallyEquivalent: Entity types differ: {entity1.GetType().Name} vs {entity2.GetType().Name}");
                return false;
            }

            Debug.WriteLine($"[DEBUG] AreEntitiesGeometricallyEquivalent: Comparing two {entity1.GetType().Name}");

            switch (entity1)
            {
                case DxfLine line1 when entity2 is DxfLine line2:
                    bool p1p1 = PointEquals(line1.P1, line2.P1, tolerance);
                    bool p2p2 = PointEquals(line1.P2, line2.P2, tolerance);
                    bool p1p2 = PointEquals(line1.P1, line2.P2, tolerance);
                    bool p2p1 = PointEquals(line1.P2, line2.P1, tolerance);
                    Debug.WriteLine($"[DEBUG] LineCompare: L1P1={line1.P1}, L1P2={line1.P2} | L2P1={line2.P1}, L2P2={line2.P2}");
                    Debug.WriteLine($"[DEBUG] LineCompare: (P1s match: {p1p1}, P2s match: {p2p2}) OR (P1-L2P2 match: {p1p2}, P2-L2P1 match: {p2p1})");
                    return (p1p1 && p2p2) || (p1p2 && p2p1);

                case DxfCircle circle1 when entity2 is DxfCircle circle2:
                    bool centerMatch = PointEquals(circle1.Center, circle2.Center, tolerance);
                    bool radiusMatch = Math.Abs(circle1.Radius - circle2.Radius) < tolerance;
                    Debug.WriteLine($"[DEBUG] CircleCompare: C1=({circle1.Center}, R={circle1.Radius}) | C2=({circle2.Center}, R={circle2.Radius})");
                    Debug.WriteLine($"[DEBUG] CircleCompare: CenterMatch={centerMatch}, RadiusMatch={radiusMatch}");
                    return centerMatch && radiusMatch;

                case DxfArc arc1 when entity2 is DxfArc arc2:
                    // TODO: Robust angle comparison (normalize to 0-360, handle wrap-around)
                    // For now, using modulo which is not perfectly robust for all cases like 0 vs 360.
                    // A better way: convert angles to vectors or check if one angle is equivalent to other + k*360.
                    double normalizedStartAngle1 = (arc1.StartAngle % 360 + 360) % 360;
                    double normalizedEndAngle1 = (arc1.EndAngle % 360 + 360) % 360;
                    double normalizedStartAngle2 = (arc2.StartAngle % 360 + 360) % 360;
                    double normalizedEndAngle2 = (arc2.EndAngle % 360 + 360) % 360;

                    bool arcCenterMatch = PointEquals(arc1.Center, arc2.Center, tolerance);
                    bool arcRadiusMatch = Math.Abs(arc1.Radius - arc2.Radius) < tolerance;
                    bool arcStartAngleMatch = Math.Abs(normalizedStartAngle1 - normalizedStartAngle2) < tolerance || Math.Abs(normalizedStartAngle1 - normalizedStartAngle2 - 360) < tolerance || Math.Abs(normalizedStartAngle1 - normalizedStartAngle2 + 360) < tolerance;
                    bool arcEndAngleMatch = Math.Abs(normalizedEndAngle1 - normalizedEndAngle2) < tolerance || Math.Abs(normalizedEndAngle1 - normalizedEndAngle2 - 360) < tolerance || Math.Abs(normalizedEndAngle1 - normalizedEndAngle2 + 360) < tolerance;

                    Debug.WriteLine($"[DEBUG] ArcCompare: A1=C({arc1.Center}),R({arc1.Radius}),SA({arc1.StartAngle}),EA({arc1.EndAngle})");
                    Debug.WriteLine($"[DEBUG] ArcCompare: A2=C({arc2.Center}),R({arc2.Radius}),SA({arc2.StartAngle}),EA({arc2.EndAngle})");
                    Debug.WriteLine($"[DEBUG] ArcCompare: NormA1=SA({normalizedStartAngle1}),EA({normalizedEndAngle1}) | NormA2=SA({normalizedStartAngle2}),EA({normalizedEndAngle2})");
                    Debug.WriteLine($"[DEBUG] ArcCompare: CenterMatch={arcCenterMatch}, RadiusMatch={arcRadiusMatch}, StartAngleMatch={arcStartAngleMatch}, EndAngleMatch={arcEndAngleMatch}");
                    return arcCenterMatch && arcRadiusMatch && arcStartAngleMatch && arcEndAngleMatch;

                case DxfLwPolyline poly1 when entity2 is DxfLwPolyline poly2:
                    Debug.WriteLine($"[DEBUG] LWPolylineCompare: VCount1={poly1.Vertices.Count}, VCount2={poly2.Vertices.Count}, Closed1={poly1.IsClosed}, Closed2={poly2.IsClosed}");
                    if (poly1.Vertices.Count != poly2.Vertices.Count || poly1.IsClosed != poly2.IsClosed) return false;
                    for(int i=0; i < poly1.Vertices.Count; i++)
                    {
                        var v1 = poly1.Vertices[i];
                        var v2 = poly2.Vertices[i];
                        // LwPolyline vertices are DxfLwPolylineVertex, which have X, Y, Bulge. Z is from polyline's Elevation.
                        // Using PointEquals for X,Y comparison by creating temporary DxfPoints.
                        bool xyMatch = PointEquals(new DxfPoint(v1.X, v1.Y, 0), new DxfPoint(v2.X, v2.Y, 0), tolerance);
                        bool bulgeMatch = Math.Abs(v1.Bulge - v2.Bulge) < tolerance;
                        Debug.WriteLine($"[DEBUG] LWPolylineCompare: V{i} P1=({v1.X},{v1.Y},B={v1.Bulge}) | P2=({v2.X},{v2.Y},B={v2.Bulge}) | XYMatch={xyMatch}, BulgeMatch={bulgeMatch}");
                        if (!xyMatch || !bulgeMatch)
                        {
                            return false;
                        }
                    }
                    return true;
                // TODO: Add other entity types as needed
                default:
                    Debug.WriteLine($"[WARNING] AreEntitiesGeometricallyEquivalent: Unhandled entity type {entity1.GetType().Name} for comparison.");
                    return false;
            }
        }

        private void ReconcileTrajectoryEntities(Models.Configuration config, DxfFile? currentDoc)
        {
            if (config == null || currentDoc == null || config.SprayPasses == null || !currentDoc.Entities.Any())
            {
                Debug.WriteLine("[DEBUG] ReconcileTrajectoryEntities: Skipping reconciliation due to null config, doc, passes, or empty document entities.");
                return;
            }

            Debug.WriteLine($"[DEBUG] ReconcileTrajectoryEntities: Starting. Document has {currentDoc.Entities.Count()} entities.");

            // Create a list of available entities from the document to "consume" as they are matched
            // This helps handle cases where multiple identical geometric entities might exist in the DXF,
            // ensuring each trajectory maps to a unique live entity if possible.
            List<DxfEntity> availableDocEntities = new List<DxfEntity>(currentDoc.Entities);

            foreach (var pass in config.SprayPasses)
            {
                if (pass.Trajectories == null) continue;
                for (int i = 0; i < pass.Trajectories.Count; i++)
                {
                    var trajectory = pass.Trajectories[i];
                    if (trajectory.OriginalDxfEntity == null)
                    {
                        Debug.WriteLine($"[DEBUG] ReconcileTrajectoryEntities: Trajectory {i} in pass '{pass.PassName}' has null OriginalDxfEntity.");
                        continue;
                    }

                    DxfEntity? matchedEntity = null;
                    int matchedEntityIndexInAvailableList = -1;

                    for (int j = 0; j < availableDocEntities.Count; j++)
                    {
                        if (AreEntitiesGeometricallyEquivalent(trajectory.OriginalDxfEntity, availableDocEntities[j]))
                        {
                            matchedEntity = availableDocEntities[j];
                            matchedEntityIndexInAvailableList = j;
                            break;
                        }
                    }

                    if (matchedEntity != null)
                    {
                        trajectory.OriginalDxfEntity = matchedEntity; // Update reference to the live entity from the document
                        availableDocEntities.RemoveAt(matchedEntityIndexInAvailableList); // Remove from available to prevent re-matching
                        Debug.WriteLine($"[DEBUG] ReconcileTrajectoryEntities: Reconciled trajectory entity: {matchedEntity.GetType().Name}");
                    }
                    else
                    {
                        // If no match, the trajectory.OriginalDxfEntity remains the deserialized instance.
                        // Highlighting will likely fail for this specific entity.
                        Debug.WriteLine($"[WARNING] ReconcileTrajectoryEntities: Could not find a matching live entity for deserialized {trajectory.OriginalDxfEntity.GetType().Name}.");
                    }
                }
            }
            Debug.WriteLine("[DEBUG] ReconcileTrajectoryEntities: Finished.");
        }


        /// <summary>
        /// Calculates the bounding box of an arc segment defined by two points and a bulge value.
        /// </summary>
        /// <param name="p1">Start point of the arc segment.</param>
        /// <param name="p2">End point of the arc segment.</param>
        /// <param name="bulge">Bulge value. Positive for CCW arc, negative for CW.</param>
        /// <returns>A Rect representing the bounding box of the arc segment, or Rect.Empty if calculation fails.</returns>
        private Rect GetArcSegmentBoundsFromBulge(Point p1, Point p2, double bulge)
        {
            if (Math.Abs(bulge) < 1e-9) // Treat as a straight line segment
            {
                return new Rect(p1, p2);
            }

            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            double chordLengthSquared = dx * dx + dy * dy;
            double chordLength = Math.Sqrt(chordLengthSquared);

            if (chordLength < 1e-9) // Points are coincident
            {
                return new Rect(p1, p2);
            }

            // Angle of the chord vector
            double chordAngle = Math.Atan2(dy, dx);

            // Included angle of the arc segment (theta)
            // bulge = tan(theta / 4) ==> theta = 4 * atan(bulge)
            double includedAngle = 4.0 * Math.Atan(bulge);

            // Radius of the arc
            // R = C / (2 * sin(theta / 2))
            double radius = chordLength / (2.0 * Math.Sin(includedAngle / 2.0));
            if (double.IsInfinity(radius) || double.IsNaN(radius)) return new Rect(p1, p2); // Should be caught by bulge or chord checks

            // Arc center calculation
            // Midpoint of the chord
            Point midPointChord = new Point(p1.X + dx / 2.0, p1.Y + dy / 2.0);

            // Distance from chord midpoint to arc center (h)
            // h = R * cos(theta/2) or sqrt(R^2 - (C/2)^2)
            double h = radius * Math.Cos(includedAngle / 2.0);
            // If bulge > 0 (CCW), center is to the "left" of chord vector P1->P2
            // If bulge < 0 (CW), center is to the "right"
            // The sign of bulge also affects the sign of 'h' in some formulations, here h is distance.
            // We need to adjust the perpendicular direction based on bulge.

            double perpDx = -dy / chordLength; // Normalized perpendicular vector component
            double perpDy = dx / chordLength;  // Normalized perpendicular vector component

            // For CCW arc (bulge > 0), center is (midPoint.X - h * perpDx_normalized, midPoint.Y - h * perpDy_normalized)
            // where perp vector is (-dy, dx).
            // If bulge is negative, the center is on the other side.
            // Simplified: sagitta vector direction depends on bulge.
            // Using a common formula: Pc = Pm - (R*cos(theta/2)) * PerpendicularNormalized(V_chord) * sign(bulge)
            // Let's use a more direct center finding:
            // Angle from midpoint of chord to center is chordAngle - PI/2 (for CCW) or + PI/2 (for CW)
            // This 'h' can be thought of as signed based on bulge for offset direction
            double offsetFactor = h * Math.Sign(bulge); // This might be incorrect, h is distance, sign of bulge defines concavity

            Point center = new Point(
                midPointChord.X - offsetFactor * (dy / chordLength), // perp component X scaled by h
                midPointChord.Y + offsetFactor * (dx / chordLength)  // perp component Y scaled by h
            );

            // Start and end angles relative to the arc center
            double startAngle = Math.Atan2(p1.Y - center.Y, p1.X - center.X);
            double endAngle = Math.Atan2(p2.Y - center.Y, p2.Y - center.Y);

            // Normalize angles to be [0, 2*PI)
            // startAngle = (startAngle + 2 * Math.PI) % (2 * Math.PI);
            // endAngle = (endAngle + 2 * Math.PI) % (2 * Math.PI);

            // Determine sweep direction and adjust endAngle for full sweep
            // If bulge < 0, arc is CW. If bulge > 0, arc is CCW.
            // We want to sweep from startAngle to endAngle in the direction of the bulge.

            if (bulge < 0) // Clockwise
            {
                if (endAngle > startAngle)
                    startAngle += 2 * Math.PI; // Ensure sweep is CW
            }
            else // Counter-clockwise
            {
                if (endAngle < startAngle)
                    endAngle += 2 * Math.PI; // Ensure sweep is CCW
            }

            // Initialize bounding box with start and end points
            double minX = Math.Min(p1.X, p2.X);
            double minY = Math.Min(p1.Y, p2.Y);
            double maxX = Math.Max(p1.X, p2.X);
            double maxY = Math.Max(p1.Y, p2.Y);

            // Check cardinal points (0, 90, 180, 270 degrees in circle's coordinate system)
            // if they fall within the arc segment.
            Action<double> checkAngle = (angle) =>
            {
                bool angleInSweep;
                if (bulge > 0) // CCW
                {
                    // Normalize angle to be relative to startAngle for CCW sweep
                    double normalizedAngle = angle;
                    while (normalizedAngle < startAngle) normalizedAngle += 2 * Math.PI;
                    angleInSweep = normalizedAngle >= startAngle && normalizedAngle <= endAngle;
                }
                else // CW
                {
                    // Normalize angle to be relative to startAngle for CW sweep
                    double normalizedAngle = angle;
                    while (normalizedAngle > startAngle) normalizedAngle -= 2 * Math.PI;
                    angleInSweep = normalizedAngle <= startAngle && normalizedAngle >= endAngle;
                }

                if (angleInSweep)
                {
                    double x = center.X + radius * Math.Cos(angle);
                    double y = center.Y + radius * Math.Sin(angle);
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            };

            checkAngle(0);                         // Point at positive X-axis from center
            checkAngle(Math.PI / 2.0);             // Point at positive Y-axis from center
            checkAngle(Math.PI);                   // Point at negative X-axis from center
            checkAngle(3.0 * Math.PI / 2.0);       // Point at negative Y-axis from center

            if (minX > maxX || minY > maxY) return Rect.Empty; // Should not happen with valid inputs

            return new Rect(new Point(minX, minY), new Point(maxX, maxY));
        }

        /// <summary>
        /// Helper method to get the bounding box of a DxfEntity as a Rect.
        /// </summary>
        /// <param name="entity">The DXF entity.</param>
        /// <returns>A Rect representing the entity's bounding box in DXF coordinates, or Rect.Empty if bounds cannot be determined.</returns>
        private Rect GetDxfEntityRect(DxfEntity entity)
        {
            if (entity == null)
                return Rect.Empty;

            var boundsTuple = CalculateEntityBoundsSimple(entity);
            if (boundsTuple.HasValue)
            {
                var (minX, minY, maxX, maxY) = boundsTuple.Value;
                if (maxX < minX || maxY < minY) return Rect.Empty; // Invalid bounds
                return new Rect(minX, minY, maxX - minX, maxY - minY);
            }
            return Rect.Empty;
        }
    }
}
