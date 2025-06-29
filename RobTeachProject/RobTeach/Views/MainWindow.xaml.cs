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
// using System.Windows.Threading; // Was for optional Dispatcher.Invoke, not currently used.
// using System.Text.RegularExpressions; // Was for optional IP validation, not currently used.

namespace RobTeach.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml. This is the main window of the RobTeach application,
    /// handling UI events, displaying CAD data, managing configurations, and initiating Modbus communication.
    /// </summary>
    public partial class MainWindow : Window
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
        private RobTeach.Models.Trajectory _trajectoryInDetailView;

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
        private System.Windows.Shapes.Rectangle selectionRectangleUI = null; // The visual rectangle for selection
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

                // Enable all nozzle checkboxes
                TrajectoryUpperNozzleEnabledCheckBox.IsEnabled = true;
                TrajectoryLowerNozzleEnabledCheckBox.IsEnabled = true;
                // Gas/Liquid enabled state will be set by their respective 'Enabled' checkbox change handlers

                // Set IsChecked status for nozzle settings from selected trajectory
                TrajectoryUpperNozzleEnabledCheckBox.IsChecked = selectedTrajectory.UpperNozzleEnabled;
                TrajectoryUpperNozzleGasOnCheckBox.IsChecked = selectedTrajectory.UpperNozzleGasOn;
                TrajectoryUpperNozzleLiquidOnCheckBox.IsChecked = selectedTrajectory.UpperNozzleLiquidOn;
                TrajectoryLowerNozzleEnabledCheckBox.IsChecked = selectedTrajectory.LowerNozzleEnabled;
                TrajectoryLowerNozzleGasOnCheckBox.IsChecked = selectedTrajectory.LowerNozzleGasOn;
                TrajectoryLowerNozzleLiquidOnCheckBox.IsChecked = selectedTrajectory.LowerNozzleLiquidOn;

                // Call handlers to correctly set IsEnabled for Gas/Liquid checkboxes
                TrajectoryUpperNozzleEnabledCheckBox_Changed(null, null);
                TrajectoryLowerNozzleEnabledCheckBox_Changed(null, null);

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
                    ArcCenterZTextBox.Text = selectedTrajectory.ArcCenter.Z.ToString("F3");
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
                    trajectory.Points.AddRange(_cadService.ConvertArcTrajectoryToPoints(trajectory, TrajectoryPointResolutionAngle));
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
            bool isEnabled = TrajectoryUpperNozzleEnabledCheckBox.IsChecked ?? false;
            TrajectoryUpperNozzleGasOnCheckBox.IsEnabled = isEnabled;
            TrajectoryUpperNozzleLiquidOnCheckBox.IsEnabled = isEnabled;

            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                selectedTrajectory.UpperNozzleEnabled = isEnabled;
                if (!isEnabled)
                {
                    TrajectoryUpperNozzleGasOnCheckBox.IsChecked = false; // Also uncheck
                    TrajectoryUpperNozzleLiquidOnCheckBox.IsChecked = false; // Also uncheck
                    selectedTrajectory.UpperNozzleGasOn = false;
                    selectedTrajectory.UpperNozzleLiquidOn = false;
                }
                isConfigurationDirty = true;
            }
            else if (!isEnabled) // No trajectory selected, ensure UI is consistent
            {
                TrajectoryUpperNozzleGasOnCheckBox.IsChecked = false;
                TrajectoryUpperNozzleLiquidOnCheckBox.IsChecked = false;
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
                selectedTrajectory.UpperNozzleLiquidOn = TrajectoryUpperNozzleLiquidOnCheckBox.IsChecked ?? false;
                isConfigurationDirty = true;
            }
        }

        private void TrajectoryLowerNozzleEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool isEnabled = TrajectoryLowerNozzleEnabledCheckBox.IsChecked ?? false;
            TrajectoryLowerNozzleGasOnCheckBox.IsEnabled = isEnabled;
            TrajectoryLowerNozzleLiquidOnCheckBox.IsEnabled = isEnabled;

            if (CurrentPassTrajectoriesListBox.SelectedItem is Trajectory selectedTrajectory)
            {
                selectedTrajectory.LowerNozzleEnabled = isEnabled;
                if (!isEnabled)
                {
                    TrajectoryLowerNozzleGasOnCheckBox.IsChecked = false; // Also uncheck
                    TrajectoryLowerNozzleLiquidOnCheckBox.IsChecked = false; // Also uncheck
                    selectedTrajectory.LowerNozzleGasOn = false;
                    selectedTrajectory.LowerNozzleLiquidOn = false;
                }
                isConfigurationDirty = true;
            }
            else if (!isEnabled) // No trajectory selected, ensure UI is consistent
            {
                 TrajectoryLowerNozzleGasOnCheckBox.IsChecked = false;
                 TrajectoryLowerNozzleLiquidOnCheckBox.IsChecked = false;
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
                selectedTrajectory.LowerNozzleLiquidOn = TrajectoryLowerNozzleLiquidOnCheckBox.IsChecked ?? false;
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
                if (_trajectoryInDetailView.ArcCenter.Z != newZ)
                {
                    _trajectoryInDetailView.ArcCenter = new DxfPoint(
                        _trajectoryInDetailView.ArcCenter.X,
                        _trajectoryInDetailView.ArcCenter.Y,
                        newZ);
                    isConfigurationDirty = true;
                    CurrentPassTrajectoriesListBox.Items.Refresh(); // Refresh if Z might be part of ToString()
                }
            }
            else
            {
                MessageBox.Show("Invalid Arc Center Z value. Please enter a valid number.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ArcCenterZTextBox.Text = _trajectoryInDetailView.ArcCenter.Z.ToString("F3");
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
                    PerformFitToView();
                    StatusTextBlock.Text = $"Loaded: {Path.GetFileName(_currentDxfFilePath)}. Click shapes to select.";
                    isConfigurationDirty = false;
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
            Trajectory trajectoryToSelect = null; // Declare at wider scope

            // Detailed check for the main condition
            bool isShape = sender is System.Windows.Shapes.Shape;
            bool keyExists = false;
            if (isShape)
            {
                keyExists = _wpfShapeToDxfEntityMap.ContainsKey((System.Windows.Shapes.Shape)sender);
            }
            Trace.WriteLine($"  -- Checking sender type: {sender?.GetType().Name ?? "null"}, IsShape: {isShape}, Map contains key: {keyExists}");
            Trace.Flush();

            if (isShape && keyExists)
            {
                System.Windows.Shapes.Shape clickedShape = (System.Windows.Shapes.Shape)sender;
                Trace.WriteLine("  -- Condition (sender is Shape AND _wpfShapeToDxfEntityMap contains key) MET");
                Trace.Flush();

                var dxfEntity = _wpfShapeToDxfEntityMap[clickedShape];
                Trace.WriteLine($"  -- Retrieved dxfEntity: {dxfEntity?.GetType().Name ?? "null"}");
                Trace.Flush();
                
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
                            newTrajectory.ArcCenter = arc.Center;
                            newTrajectory.ArcRadius = arc.Radius;
                            newTrajectory.ArcStartAngle = arc.StartAngle;
                            newTrajectory.ArcEndAngle = arc.EndAngle;
                            newTrajectory.ArcNormal = arc.Normal;
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

        private void FitToViewButton_Click(object sender, RoutedEventArgs e) { /* ... (No change) ... */ }
        private void PerformFitToView() { /* ... (No change) ... */ }
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

                foreach (var wpfShape in _wpfShapeToDxfEntityMap.Keys)
                {
                    if (wpfShape.Visibility != Visibility.Visible || wpfShape.RenderedGeometry == null || wpfShape.RenderedGeometry.IsEmpty())
                    {
                        continue;
                    }

                    GeneralTransform canvasToWorldTransform = _transformGroup.Inverse;
                    if (canvasToWorldTransform == null) continue;

                    Rect selectionRectInWorldCoords = canvasToWorldTransform.TransformBounds(finalSelectionRect);
                    Rect shapeGeometryBounds = wpfShape.RenderedGeometry.Bounds;

                    if (selectionRectInWorldCoords.IntersectsWith(shapeGeometryBounds))
                    {
                        DxfEntity dxfEntity = _wpfShapeToDxfEntityMap[wpfShape];
                        var existingTrajectory = currentPass.Trajectories.FirstOrDefault(t => t.OriginalDxfEntity == dxfEntity);

                        if (existingTrajectory == null) // Entity is not selected, so select it
                        {
                            var newTrajectory = new Trajectory
                            {
                                OriginalDxfEntity = dxfEntity,
                                EntityType = dxfEntity.GetType().Name,
                                IsReversed = false
                            };
                            switch (dxfEntity)
                            {
                                case DxfLine line:
                                    newTrajectory.PrimitiveType = "Line";
                                    newTrajectory.LineStartPoint = line.P1;
                                    newTrajectory.LineEndPoint = line.P2;
                                    break;
                                case DxfArc arc:
                                    newTrajectory.PrimitiveType = "Arc";
                                    newTrajectory.ArcCenter = arc.Center;
                                    newTrajectory.ArcRadius = arc.Radius;
                                    newTrajectory.ArcStartAngle = arc.StartAngle;
                                    newTrajectory.ArcEndAngle = arc.EndAngle;
                                    newTrajectory.ArcNormal = arc.Normal;
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
                            selectionStateChanged = true;
                        }
                        else // Entity is already selected, so deselect it
                        {
                            currentPass.Trajectories.Remove(existingTrajectory);
                            selectionStateChanged = true;
                        }
                    }
                }

                if (selectionStateChanged)
                {
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
    }
}
