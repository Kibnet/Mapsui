using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Mapsui.Fetcher;
using Mapsui.Geometries;
using Mapsui.Layers;
using Mapsui.Providers;
using Mapsui.Rendering;
using Mapsui.Rendering.Xaml;
using Mapsui.Utilities;
using SkiaSharp;
using SkiaSharp.Views;
using Point = System.Windows.Point;
using XamlVector = System.Windows.Vector;

namespace Mapsui.UI.Xaml
{
    public enum RenderMode
    {
        Wpf,
        Skia
    }

    public class MapControl : Grid
    {
        // ReSharper disable once UnusedMember.Local // This registration triggers the call to OnResolutionChanged
        private static readonly DependencyProperty ResolutionProperty =
            DependencyProperty.Register(
                "Resolution", typeof(double), typeof(MapControl),
                new PropertyMetadata(OnResolutionChanged));

        private readonly Rectangle _bboxRect = CreateSelectRectangle();
        private readonly DoubleAnimation _zoomAnimation = new DoubleAnimation();
        private readonly Storyboard _zoomStoryBoard = new Storyboard();
        private Point _currentMousePosition;
        private Point _downMousePosition;
        private bool _invalid;
        private Map _map;
        private bool _mouseDown;
        private MouseInfoEventArgs _previousHoverInfoEventArgs;
        private Point _previousMousePosition;
        private RenderMode _renderMode;
        private Geometries.Point _skiaScale;
        private double _toResolution = double.NaN;
        private bool _viewportInitialized;
        private readonly AttributionPanel _attributionPanel = CreateAttributionPanel();

        public MapControl()
        {
            Children.Add(RenderCanvas);
            Children.Add(RenderElement);
            Children.Add(_attributionPanel);
            Children.Add(_bboxRect);

            RenderElement.PaintSurface += SKElementOnPaintSurface;
            CompositionTarget.Rendering += CompositionTargetRendering;

            Map = new Map();
            Loaded += MapControlLoaded;
            MouseLeftButtonDown += MapControlMouseLeftButtonDown;
            MouseLeftButtonUp += MapControlMouseLeftButtonUp;

            MouseMove += MapControlMouseMove;
            MouseLeave += MapControlMouseLeave;
            MouseWheel += MapControlMouseWheel;

            SizeChanged += MapControlSizeChanged;

            ManipulationDelta += OnManipulationDelta;
            ManipulationCompleted += OnManipulationCompleted;
            ManipulationInertiaStarting += OnManipulationInertiaStarting;
            Dispatcher.ShutdownStarted += DispatcherShutdownStarted;
            IsManipulationEnabled = true;
        }

        private static Rectangle CreateSelectRectangle()
        {
            return new Rectangle
            {
                Fill = new SolidColorBrush(Colors.Red),
                Stroke = new SolidColorBrush(Colors.Black),
                StrokeThickness = 3,
                RadiusX = 0.5,
                RadiusY = 0.5,
                StrokeDashArray = new DoubleCollection {3.0},
                Opacity = 0.3,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Visible
            };
        }

        public IRenderer Renderer { get; set; } = new MapRenderer();

        private bool IsInBoxZoomMode { get; set; }

        [Obsolete("Use Map.HoverInfoLayers", true)]
        // ReSharper disable once UnassignedGetOnlyAutoProperty // This is here just to help upgraders
        public IList<ILayer> MouseInfoOverLayers { get; }

        [Obsolete("Use Map.InfoLayers", true)]
        // ReSharper disable once UnassignedGetOnlyAutoProperty // This is here just to help upgraders
        public IList<ILayer> MouseInfoUpLayers { get; }

        public bool ZoomToBoxMode { get; set; }

        [Obsolete("Map.Viewport instead", true)]
        public IViewport Viewport => Map.Viewport;

        public Map Map
        {
            get { return _map; }
            set
            {
                if (_map != null)
                {
                    var temp = _map;
                    _map = null;
                    temp.DataChanged -= MapDataChanged;
                    temp.PropertyChanged -= MapPropertyChanged;
                    temp.RefreshGraphics -= MapRefreshGraphics;
                    temp.Dispose();
                }

                _map = value;

                if (_map != null)
                {
                    _viewportInitialized = false;
                    _map.DataChanged += MapDataChanged;
                    _map.PropertyChanged += MapPropertyChanged;
                    _map.RefreshGraphics += MapRefreshGraphics;
                    _map.ViewChanged(true);
                    _attributionPanel.Populate(Map.Layers);
                }

                RefreshGraphics();
            }
        }

        public FpsCounter FpsCounter { get; } = new FpsCounter();

        public string ErrorMessage { get; private set; }

        public bool ZoomLocked { get; set; }

        public Canvas RenderCanvas { get; } = CreateWpfRenderCanvas();

        private SKElement RenderElement { get; } = CreateSkiaRenderElement();

        public RenderMode RenderMode
        {
            get { return _renderMode; }
            set
            {
                if (value == RenderMode.Skia)
                {
                    RenderCanvas.Visibility = Visibility.Collapsed;
                    RenderElement.Visibility = Visibility.Visible;
                    Renderer = new Rendering.Skia.MapRenderer();
                    Refresh();
                }
                else
                {
                    RenderElement.Visibility = Visibility.Collapsed;
                    RenderCanvas.Visibility = Visibility.Visible;
                    Renderer = new MapRenderer();
                    Refresh();
                }
                _renderMode = value;
            }
        }

        private static Canvas CreateWpfRenderCanvas()
        {
            return new Canvas
            {
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }

        private static SKElement CreateSkiaRenderElement()
        {
            return new SKElement
            {
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Visibility = Visibility.Collapsed
            };
        }

        public event EventHandler ErrorMessageChanged;
        public event EventHandler<ViewChangedEventArgs> ViewChanged;
        public event EventHandler<MouseInfoEventArgs> HoverInfo;
        public event EventHandler<MouseInfoEventArgs> Info;
        public event EventHandler<FeatureInfoEventArgs> FeatureInfo;
        public event EventHandler ViewportInitialized;

        private void MapRefreshGraphics(object sender, EventArgs eventArgs)
        {
            RefreshGraphics();
        }

        private void MapPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(new Action(() => MapPropertyChanged(sender, e)));
            else
            {
                if (e.PropertyName == "Enabled")
                {
                    RefreshGraphics();
                }
                else if (e.PropertyName == "Opacity")
                {
                    RefreshGraphics();
                }
                else if (e.PropertyName == nameof(Map.Envelope))
                {
                    InitializeViewport();
                    _map.ViewChanged(true);
                }
                else if (e.PropertyName == "Rotation")
                {
                    _map.ViewChanged(true);
                    OnViewChanged();
                }
                else if (e.PropertyName == nameof(Map.Layers))
                {
                    _attributionPanel.Populate(Map.Layers);
                }
            }
        }

        public void OnViewChanged(bool userAction = false)
        {
            if (_map == null) return;

            ViewChanged?.Invoke(this, new ViewChangedEventArgs {Viewport = Map.Viewport, UserAction = userAction});
        }

        public void Refresh()
        {
            _map.ViewChanged(true);
            RefreshGraphics();
        }

        private void RefreshGraphics()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                InvalidateVisual();
                _invalid = true;
            }));
        }

        public void Clear()
        {
            _map?.ClearCache();
            RefreshGraphics();
        }

        public void ZoomIn()
        {
            if (ZoomLocked)
                return;

            if (double.IsNaN(_toResolution))
                _toResolution = Map.Viewport.Resolution;

            _toResolution = ZoomHelper.ZoomIn(_map.Resolutions, _toResolution);
            ZoomMiddle();
        }

        public void ZoomOut()
        {
            if (double.IsNaN(_toResolution))
                _toResolution = Map.Viewport.Resolution;

            _toResolution = ZoomHelper.ZoomOut(_map.Resolutions, _toResolution);
            ZoomMiddle();
        }

        private void OnErrorMessageChanged(EventArgs e)
        {
            ErrorMessageChanged?.Invoke(this, e);
        }

        private static void OnResolutionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            var newResolution = (double) e.NewValue;
            ((MapControl) dependencyObject).ZoomToResolution(newResolution);
        }

        private void ZoomToResolution(double resolution)
        {
            var current = _currentMousePosition;

            Map.Viewport.Transform(current.X, current.Y, current.X, current.Y, Map.Viewport.Resolution/resolution);

            _map.ViewChanged(true);
            OnViewChanged();
            RefreshGraphics();
        }

        private void ZoomMiddle()
        {
            _currentMousePosition = new Point(ActualWidth/2, ActualHeight/2);
            StartZoomAnimation(Map.Viewport.Resolution, _toResolution);
        }

        private void MapControlLoaded(object sender, RoutedEventArgs e)
        {
            if (!_viewportInitialized) InitializeViewport();
            UpdateSize();
            InitAnimation();
            Focusable = true;
        }

        private void InitAnimation()
        {
            _zoomAnimation.Duration = new Duration(new TimeSpan(0, 0, 0, 0, 1000));
            _zoomAnimation.EasingFunction = new QuarticEase();
            Storyboard.SetTarget(_zoomAnimation, this);
            Storyboard.SetTargetProperty(_zoomAnimation, new PropertyPath("Resolution"));
            _zoomStoryBoard.Children.Add(_zoomAnimation);
        }

        private void MapControlMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_viewportInitialized) return;
            if (ZoomLocked) return;

            _currentMousePosition = e.GetPosition(this);
            //Needed for both MouseMove and MouseWheel event for mousewheel event

            if (double.IsNaN(_toResolution))
                _toResolution = Map.Viewport.Resolution;

            if (e.Delta > Constants.Epsilon)
                _toResolution = ZoomHelper.ZoomIn(_map.Resolutions, _toResolution);
            else if (e.Delta < Constants.Epsilon)
                _toResolution = ZoomHelper.ZoomOut(_map.Resolutions, _toResolution);

            e.Handled = true; //so that the scroll event is not sent to the html page.

            // Some cheating for personal gain. This workaround could be ommitted if the zoom animations was on CenterX, CenterY and Resolution, not Resolution alone.
            Map.Viewport.Center.X += 0.000000001;
            Map.Viewport.Center.Y += 0.000000001;

            StartZoomAnimation(Map.Viewport.Resolution, _toResolution);
        }

        private void StartZoomAnimation(double begin, double end)
        {
            _zoomStoryBoard.Pause(); //using Stop() here causes unexpected results while zooming very fast.
            _zoomAnimation.From = begin;
            _zoomAnimation.To = end;
            _zoomAnimation.Completed += ZoomAnimationCompleted;
            _zoomStoryBoard.Begin();
        }

        private void ZoomAnimationCompleted(object sender, EventArgs e)
        {
            _toResolution = double.NaN;
        }

        private void MapControlSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_viewportInitialized) InitializeViewport();
            Clip = new RectangleGeometry {Rect = new Rect(0, 0, ActualWidth, ActualHeight)};
            UpdateSize();
            _map.ViewChanged(true);
            OnViewChanged();
            Refresh();
        }

        private void UpdateSize()
        {
            if (Map.Viewport != null)
            {
                Map.Viewport.Width = ActualWidth;
                Map.Viewport.Height = ActualHeight;
            }
        }

        private void MapControlMouseLeave(object sender, MouseEventArgs e)
        {
            _previousMousePosition = new Point();
            ReleaseMouseCapture();
        }

        public void MapDataChanged(object sender, DataChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new DataChangedEventHandler(MapDataChanged), sender, e);
            }
            else
            {
                if (e == null)
                {
                    ErrorMessage = "Unexpected error: DataChangedEventArgs can not be null";
                    OnErrorMessageChanged(EventArgs.Empty);
                }
                else if (e.Cancelled)
                {
                    ErrorMessage = "Cancelled";
                    OnErrorMessageChanged(EventArgs.Empty);
                }
                else if (e.Error is WebException)
                {
                    ErrorMessage = "WebException: " + e.Error.Message;
                    OnErrorMessageChanged(EventArgs.Empty);
                }
                else if (e.Error != null)
                {
                    ErrorMessage = e.Error.GetType() + ": " + e.Error.Message;
                    OnErrorMessageChanged(EventArgs.Empty);
                }
                else // no problems
                {
                    RefreshGraphics();
                }
            }
        }

        private void MapControlMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.StylusDevice != null) return;

            _previousMousePosition = e.GetPosition(this);
            _downMousePosition = e.GetPosition(this);
            _mouseDown = true;
            CaptureMouse();
            IsInBoxZoomMode = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        }

        private void MapControlMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.StylusDevice != null) return;

            if (IsInBoxZoomMode || ZoomToBoxMode)
            {
                ZoomToBoxMode = false;
                var previous = Map.Viewport.ScreenToWorld(_previousMousePosition.X, _previousMousePosition.Y);
                var current = Map.Viewport.ScreenToWorld(e.GetPosition(this).X, e.GetPosition(this).Y);
                ZoomToBox(previous, current);
            }
            else
            {
                HandleFeatureInfo(e);
                var eventArgs = GetInfoEventArgs(e.GetPosition(this), Map.InfoLayers);
                OnMouseInfoUp(eventArgs ?? new MouseInfoEventArgs());
            }

            _map.ViewChanged(true);
            OnViewChanged(true);
            _mouseDown = false;

            _previousMousePosition = new Point();
            ReleaseMouseCapture();
        }

        private void HandleFeatureInfo(MouseButtonEventArgs e)
        {
            if (FeatureInfo == null) return; // don't fetch if you the call back is not set.

            if (_downMousePosition == e.GetPosition(this))
                foreach (var layer in Map.Layers)
                {
                    // ReSharper disable once SuspiciousTypeConversion.Global
                    (layer as IFeatureInfo)?.GetFeatureInfo(Map.Viewport, _downMousePosition.X, _downMousePosition.Y,
                        OnFeatureInfo);
                }
        }

        private void OnFeatureInfo(IDictionary<string, IEnumerable<IFeature>> features)
        {
            FeatureInfo?.Invoke(this, new FeatureInfoEventArgs {FeatureInfo = features});
        }

        private void MapControlMouseMove(object sender, MouseEventArgs e)
        {
            if (e.StylusDevice != null) return;

            if (IsInBoxZoomMode || ZoomToBoxMode)
            {
                DrawBbox(e.GetPosition(this));
                return;
            }

            if (!_mouseDown) RaiseHoverInfoEvents(e.GetPosition(this));

            if (_mouseDown)
            {
                if (_previousMousePosition == default(Point))
                    return; // It turns out that sometimes MouseMove+Pressed is called before MouseDown

                _currentMousePosition = e.GetPosition(this); //Needed for both MouseMove and MouseWheel event
                Map.Viewport.Transform(_currentMousePosition.X, _currentMousePosition.Y, _previousMousePosition.X,
                    _previousMousePosition.Y);
                _previousMousePosition = _currentMousePosition;
                _map.ViewChanged(false);
                OnViewChanged(true);
                RefreshGraphics();
            }
        }

        private void RaiseHoverInfoEvents(Point mousePosition)
        {
            var hoverInfoEventArgs = GetInfoEventArgs(mousePosition, Map.HoverInfoLayers);

            if (HasChanged(_previousHoverInfoEventArgs, hoverInfoEventArgs))
            {
                if (hoverInfoEventArgs != null) // Don't raise new event when nothing changed.
                    OnMouseHoverInfo(hoverInfoEventArgs);
                else if (_previousHoverInfoEventArgs != null)
                    OnMouseHoverInfoLeave();
            }

            _previousHoverInfoEventArgs = hoverInfoEventArgs;
        }

        private static bool HasChanged(MouseInfoEventArgs previousInfoEventArgs, MouseInfoEventArgs infoEventArgs)
        {
            if (previousInfoEventArgs == null) return true;
            return previousInfoEventArgs.Feature != infoEventArgs?.Feature;
        }

        private MouseInfoEventArgs GetInfoEventArgs(Point mousePosition, IEnumerable<ILayer> layers)
        {
            var point = Map.Viewport.ScreenToWorld(new Geometries.Point(mousePosition.X, mousePosition.Y));

            var feature = Map.GetFeatureInfo(layers, point);

            if (feature != null)
                return new MouseInfoEventArgs { LayerName = "", Feature = feature };

            return null;
        }
        
        private void OnMouseHoverInfoLeave()
        {
            HoverInfo?.Invoke(this, new MouseInfoEventArgs {Leaving = true});
        }

        private void OnMouseHoverInfo(MouseInfoEventArgs e)
        {
            HoverInfo?.Invoke(this, e);
        }

        private void OnMouseInfoUp(MouseInfoEventArgs e)
        {
            Info?.Invoke(this, e);
        }

        private void InitializeViewport()
        {
            if (ActualWidth.IsNanOrZero()) return;

            if (double.IsNaN(Map.Viewport.Resolution)) // only when not set yet
            {
                if (!_map.Envelope.IsInitialized()) return;
                if (_map.Envelope.GetCentroid() == null) return;

                if (Math.Abs(_map.Envelope.Width) > Constants.Epsilon)
                    Map.Viewport.Resolution = _map.Envelope.Width/ActualWidth;
                else
                    // An envelope width of zero can happen when there is no data in the Maps' layers (yet).
                    // It should be possible to start with an empty map.
                    Map.Viewport.Resolution = Constants.DefaultResolution;
            }
            if (double.IsNaN(Map.Viewport.Center.X) || double.IsNaN(Map.Viewport.Center.Y)) // only when not set yet
            {
                if (!_map.Envelope.IsInitialized()) return;
                if (_map.Envelope.GetCentroid() == null) return;

                Map.Viewport.Center = _map.Envelope.GetCentroid();
            }

            Map.Viewport.Width = ActualWidth;
            Map.Viewport.Height = ActualHeight;

            Map.Viewport.RenderResolutionMultiplier = 1.0;

            _viewportInitialized = true;

            OnViewportInitialize();

            Map.ViewChanged(true);
        }

        private void OnViewportInitialize()
        {
            ViewportInitialized?.Invoke(this, EventArgs.Empty);
        }

        private void CompositionTargetRendering(object sender, EventArgs e)
        {
            if (!_viewportInitialized) InitializeViewport();
            if (!_viewportInitialized) return; // Stop if the line above failed.
            // In developermode always render so that fps can be counted
            if (!_invalid && !DeveloperTools.DeveloperMode) return;

            if (RenderMode == RenderMode.Wpf) RenderWpf();
            else RenderElement.InvalidateVisual();
        }

        private void RenderWpf()
        {
            if ((Renderer != null) && (_map != null))
            {
                Renderer.Render(RenderCanvas, Map.Viewport, _map.Layers, _map.BackColor);
                if (DeveloperTools.DeveloperMode) FpsCounter.FramePlusOne();
                _invalid = false;
            }
        }

        private void DispatcherShutdownStarted(object sender, EventArgs e)
        {
            CompositionTarget.Rendering -= CompositionTargetRendering;
            _map?.Dispose();
        }

        public void ZoomToBox(Geometries.Point beginPoint, Geometries.Point endPoint)
        {
            double x, y, resolution;
            var width = Math.Abs(endPoint.X - beginPoint.X);
            var height = Math.Abs(endPoint.Y - beginPoint.Y);
            if (width <= 0) return;
            if (height <= 0) return;

            ZoomHelper.ZoomToBoudingbox(beginPoint.X, beginPoint.Y, endPoint.X, endPoint.Y,
                ActualWidth, ActualHeight, out x, out y, out resolution);
            resolution = ZoomHelper.ClipResolutionToExtremes(_map.Resolutions, resolution);

            Map.Viewport.Center = new Geometries.Point(x, y);
            Map.Viewport.Resolution = resolution;
            _toResolution = resolution;

            _map.ViewChanged(true);
            OnViewChanged(true);
            RefreshGraphics();
            ClearBBoxDrawing();
        }

        private void ClearBBoxDrawing()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _bboxRect.Margin = new Thickness(0, 0, 0, 0);
                _bboxRect.Width = 0;
                _bboxRect.Height = 0;
            }));
        }

        private void DrawBbox(Point newPos)
        {
            if (_mouseDown)
            {
                var from = _previousMousePosition;
                var to = newPos;

                if (from.X > to.X)
                {
                    var temp = from;
                    from.X = to.X;
                    to.X = temp.X;
                }

                if (from.Y > to.Y)
                {
                    var temp = from;
                    from.Y = to.Y;
                    to.Y = temp.Y;
                }

                _bboxRect.Width = to.X - from.X;
                _bboxRect.Height = to.Y - from.Y;
                _bboxRect.Margin = new Thickness(from.X, from.Y, 0, 0);
            }
        }

        public void ZoomToFullEnvelope()
        {
            if (Map.Envelope == null) return;
            if (ActualWidth.IsNanOrZero()) return;
            Map.Viewport.Resolution = Math.Max(Map.Envelope.Width/ActualWidth, Map.Envelope.Height/ActualHeight);
            Map.Viewport.Center = Map.Envelope.GetCentroid();
        }

        private static void OnManipulationInertiaStarting(object sender, ManipulationInertiaStartingEventArgs e)
        {
            e.TranslationBehavior.DesiredDeceleration = 25*96.0/(1000.0*1000.0);
        }

        private void OnManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            var previousX = e.ManipulationOrigin.X;
            var previousY = e.ManipulationOrigin.Y;
            var currentX = e.ManipulationOrigin.X + e.DeltaManipulation.Translation.X;
            var currentY = e.ManipulationOrigin.Y + e.DeltaManipulation.Translation.Y;
            var deltaScale = GetDeltaScale(e.DeltaManipulation.Scale);

            Map.Viewport.Transform(currentX, currentY, previousX, previousY, deltaScale);

            _invalid = true;
            OnViewChanged(true);
            e.Handled = true;
        }

        private double GetDeltaScale(XamlVector scale)
        {
            if (ZoomLocked) return 1;
            var deltaScale = (scale.X + scale.Y)/2;
            if (Math.Abs(deltaScale) < Constants.Epsilon)
                return 1; // If there is no scaling the deltaScale will be 0.0 in Windows Phone (while it is 1.0 in wpf)
            if (!(Math.Abs(deltaScale - 1d) > Constants.Epsilon)) return 1;
            return deltaScale;
        }

        private void OnManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
        {
            Refresh();
        }

        [SuppressMessage("ReSharper", "UnusedParameter.Local")]
        private void OnPaintSurface(SKCanvas canvas, int width, int height)
        {
            if (double.IsNaN(Map.Viewport.Resolution)) return;

            Map.Viewport.Width = ActualWidth;
            Map.Viewport.Height = ActualHeight;

            Renderer.Render(canvas, Map.Viewport, Map.Layers, Map.BackColor);
        }

        private Geometries.Point GetSkiaScale()
        {
            var presentationSource = PresentationSource.FromVisual(this);
            if (presentationSource == null) throw new Exception("PresentationSource is null");
            var compositionTarget = presentationSource.CompositionTarget;
            if (compositionTarget == null) throw new Exception("CompositionTarget is null");

            var m = compositionTarget.TransformToDevice;

            var dpiX = m.M11;
            var dpiY = m.M22;

            return new Geometries.Point(dpiX, dpiY);
        }

        private void SKElementOnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            if (!_viewportInitialized) InitializeViewport();
            if (!_viewportInitialized) return; // Stop if the line above failed. 
            if (!_invalid && !DeveloperTools.DeveloperMode)
                return; // In developermode always render so that fps can be counterd.

            if (_skiaScale == null) _skiaScale = GetSkiaScale();
            e.Surface.Canvas.Scale((float) _skiaScale.X, (float) _skiaScale.Y);
            OnPaintSurface(e.Surface.Canvas, e.Info.Width, e.Info.Height);
        }

        private static AttributionPanel CreateAttributionPanel()
        {
            return new AttributionPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Right
            };
        }
    }

    public class ViewChangedEventArgs : EventArgs
    {
        public IViewport Viewport { get; set; }
        public bool UserAction { get; set; }
    }

    public class MouseInfoEventArgs : EventArgs
    {
        public string LayerName { get; set; } = "";
        public IFeature Feature { get; set; }
        public bool Leaving { get; set; }
    }

    public class FeatureInfoEventArgs : EventArgs
    {
        public IDictionary<string, IEnumerable<IFeature>> FeatureInfo { get; set; }
    }
}