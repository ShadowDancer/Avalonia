using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Avalonia.Controls.Presenters
{
    /// <summary>
    /// Presents a scrolling view of content inside a <see cref="ScrollViewer"/>.
    /// </summary>
    public class ScrollContentPresenter : ContentPresenter, IPresenter, IScrollable, IScrollAnchorProvider
    {
        /// <summary>
        /// Defines the <see cref="CanHorizontallyScroll"/> property.
        /// </summary>
        public static readonly DirectProperty<ScrollContentPresenter, bool> CanHorizontallyScrollProperty =
            AvaloniaProperty.RegisterDirect<ScrollContentPresenter, bool>(
                nameof(CanHorizontallyScroll),
                o => o.CanHorizontallyScroll,
                (o, v) => o.CanHorizontallyScroll = v);

        /// <summary>
        /// Defines the <see cref="CanVerticallyScroll"/> property.
        /// </summary>
        public static readonly DirectProperty<ScrollContentPresenter, bool> CanVerticallyScrollProperty =
            AvaloniaProperty.RegisterDirect<ScrollContentPresenter, bool>(
                nameof(CanVerticallyScroll),
                o => o.CanVerticallyScroll,
                (o, v) => o.CanVerticallyScroll = v);

        /// <summary>
        /// Defines the <see cref="Extent"/> property.
        /// </summary>
        public static readonly DirectProperty<ScrollContentPresenter, Size> ExtentProperty =
            ScrollViewer.ExtentProperty.AddOwner<ScrollContentPresenter>(
                o => o.Extent,
                (o, v) => o.Extent = v);

        /// <summary>
        /// Defines the <see cref="Offset"/> property.
        /// </summary>
        public static readonly DirectProperty<ScrollContentPresenter, Vector> OffsetProperty =
            ScrollViewer.OffsetProperty.AddOwner<ScrollContentPresenter>(
                o => o.Offset,
                (o, v) => o.Offset = v);

        /// <summary>
        /// Defines the <see cref="Viewport"/> property.
        /// </summary>
        public static readonly DirectProperty<ScrollContentPresenter, Size> ViewportProperty =
            ScrollViewer.ViewportProperty.AddOwner<ScrollContentPresenter>(
                o => o.Viewport,
                (o, v) => o.Viewport = v);

        // Arbitrary chosen value, probably need to ask ILogicalScrollable
        private const int LogicalScrollItemSize = 50;

        private bool _canHorizontallyScroll;
        private bool _canVerticallyScroll;
        private bool _arranging;
        private Size _extent;
        private Vector _offset;
        private IDisposable _logicalScrollSubscription;
        private Size _viewport;
        private Dictionary<int, Vector> _activeLogicalGestureScrolls;
        private List<IControl> _anchorCandidates;
        private (IControl control, Rect bounds) _anchor;

        /// <summary>
        /// Initializes static members of the <see cref="ScrollContentPresenter"/> class.
        /// </summary>
        static ScrollContentPresenter()
        {
            ClipToBoundsProperty.OverrideDefaultValue(typeof(ScrollContentPresenter), true);
            ChildProperty.Changed.AddClassHandler<ScrollContentPresenter>((x, e) => x.ChildChanged(e));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ScrollContentPresenter"/> class.
        /// </summary>
        public ScrollContentPresenter()
        {
            AddHandler(Gestures.ScrollGestureEvent, OnScrollGesture);

            this.GetObservable(ChildProperty).Subscribe(UpdateScrollableSubscription);
        }

        internal event EventHandler<EventArgs> PreArrange;

        /// <summary>
        /// Gets or sets a value indicating whether the content can be scrolled horizontally.
        /// </summary>
        public bool CanHorizontallyScroll
        {
            get { return _canHorizontallyScroll; }
            set { SetAndRaise(CanHorizontallyScrollProperty, ref _canHorizontallyScroll, value); }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the content can be scrolled horizontally.
        /// </summary>
        public bool CanVerticallyScroll
        {
            get { return _canVerticallyScroll; }
            set { SetAndRaise(CanVerticallyScrollProperty, ref _canVerticallyScroll, value); }
        }

        /// <summary>
        /// Gets the extent of the scrollable content.
        /// </summary>
        public Size Extent
        {
            get { return _extent; }
            private set { SetAndRaise(ExtentProperty, ref _extent, value); }
        }

        /// <summary>
        /// Gets or sets the current scroll offset.
        /// </summary>
        public Vector Offset
        {
            get { return _offset; }
            set { SetAndRaise(OffsetProperty, ref _offset, value); }
        }

        /// <summary>
        /// Gets the size of the viewport on the scrollable content.
        /// </summary>
        public Size Viewport
        {
            get { return _viewport; }
            private set { SetAndRaise(ViewportProperty, ref _viewport, value); }
        }

        /// <inheritdoc/>
        IControl IScrollAnchorProvider.CurrentAnchor => _anchor.control;

        /// <inheritdoc/>
        void IScrollAnchorProvider.RegisterAnchorCandidate(IControl element)
        {
            _anchorCandidates ??= new List<IControl>();
            _anchorCandidates.Add(element);
        }

        /// <inheritdoc/>
        void IScrollAnchorProvider.UnregisterAnchorCandidate(IControl element)
        {
            _anchorCandidates?.Remove(element);

            if (_anchor.control == element)
            {
                _anchor = default;
            }
        }

        /// <inheritdoc/>
        protected override Size MeasureOverride(Size availableSize)
        {
            if (_logicalScrollSubscription != null || Child == null)
            {
                return base.MeasureOverride(availableSize);
            }

            var constraint = new Size(
                CanHorizontallyScroll ? double.PositiveInfinity : availableSize.Width,
                CanVerticallyScroll ? double.PositiveInfinity : availableSize.Height);

            Child.Measure(constraint);
            return Child.DesiredSize.Constrain(availableSize);
        }

        /// <inheritdoc/>
        protected override Size ArrangeOverride(Size finalSize)
        {
            Size result;

            PreArrange?.Invoke(this, EventArgs.Empty);

            if (_logicalScrollSubscription != null || Child == null)
            {
                result = base.ArrangeOverride(finalSize);
            }
            else
            {
                try
                {
                    _arranging = true;
                    result = ArrangeWithAnchoring(finalSize);
                }
                finally
                {
                    _arranging = false;
                }
            }

            return result;
        }

        private Size ArrangeWithAnchoring(Size finalSize)
        {
            var size = new Size(
                CanHorizontallyScroll ? Math.Max(Child.DesiredSize.Width, finalSize.Width) : finalSize.Width,
                CanVerticallyScroll ? Math.Max(Child.DesiredSize.Height, finalSize.Height) : finalSize.Height);

            Vector TrackAnchor()
            {
                // If we have an anchor and its position relative to Child has changed during the
                // arrange then that change wasn't just due to scrolling (as scrolling doesn't adjust
                // relative positions within Child).
                if (_anchor.control != null &&
                    TranslateBounds(_anchor.control, Child, out var updatedBounds) &&
                    updatedBounds.Position != _anchor.bounds.Position)
                {
                    var offset = updatedBounds.Position - _anchor.bounds.Position;
                    return offset;
                }

                return default;
            }

            // Calculate the new anchor element.
            _anchor = CalculateCurrentAnchor();

            // Do the arrange.
            ArrangeOverrideImpl(size, -Offset);

            // If the anchor moved during the arrange, we need to adjust the offset and do another arrange.
            var anchorShift = TrackAnchor();

            if (anchorShift != default)
            {
                Offset += anchorShift;
                ArrangeOverrideImpl(size, -Offset);
            }

            Viewport = finalSize;
            Extent = Child.Bounds.Size.Inflate(Child.Margin);

            return finalSize;
        }

        private void OnScrollGesture(object sender, ScrollGestureEventArgs e)
        {
            if (Extent.Height > Viewport.Height || Extent.Width > Viewport.Width)
            {
                var scrollable = Child as ILogicalScrollable;
                bool isLogical = scrollable?.IsLogicalScrollEnabled == true;

                double x = Offset.X;
                double y = Offset.Y;

                Vector delta = default;
                if (isLogical)
                    _activeLogicalGestureScrolls?.TryGetValue(e.Id, out delta);
                delta += e.Delta;

                if (Extent.Height > Viewport.Height)
                {
                    double dy;
                    if (isLogical)
                    {
                        var logicalUnits = delta.Y / LogicalScrollItemSize;
                        delta = delta.WithY(delta.Y - logicalUnits * LogicalScrollItemSize);
                        dy = logicalUnits * scrollable.ScrollSize.Height;
                    }
                    else
                        dy = delta.Y;


                    y += dy;
                    y = Math.Max(y, 0);
                    y = Math.Min(y, Extent.Height - Viewport.Height);
                }

                if (Extent.Width > Viewport.Width)
                {
                    double dx;
                    if (isLogical)
                    {
                        var logicalUnits = delta.X / LogicalScrollItemSize;
                        delta = delta.WithX(delta.X - logicalUnits * LogicalScrollItemSize);
                        dx = logicalUnits * scrollable.ScrollSize.Width;
                    }
                    else
                        dx = delta.X;
                    x += dx;
                    x = Math.Max(x, 0);
                    x = Math.Min(x, Extent.Width - Viewport.Width);
                }

                if (isLogical)
                {
                    if (_activeLogicalGestureScrolls == null)
                        _activeLogicalGestureScrolls = new Dictionary<int, Vector>();
                    _activeLogicalGestureScrolls[e.Id] = delta;
                }

                Offset = new Vector(x, y);
                e.Handled = true;
            }
        }

        private void OnScrollGestureEnded(object sender, ScrollGestureEndedEventArgs e)
            => _activeLogicalGestureScrolls?.Remove(e.Id);

        /// <inheritdoc/>
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            if (Extent.Height > Viewport.Height || Extent.Width > Viewport.Width)
            {
                var scrollable = Child as ILogicalScrollable;
                bool isLogical = scrollable?.IsLogicalScrollEnabled == true;

                double x = Offset.X;
                double y = Offset.Y;

                if (Extent.Height > Viewport.Height)
                {
                    double height = isLogical ? scrollable.ScrollSize.Height : 50;
                    y += -e.Delta.Y * height;
                    y = Math.Max(y, 0);
                    y = Math.Min(y, Extent.Height - Viewport.Height);
                }

                if (Extent.Width > Viewport.Width)
                {
                    double width = isLogical ? scrollable.ScrollSize.Width : 50;
                    x += -e.Delta.X * width;
                    x = Math.Max(x, 0);
                    x = Math.Min(x, Extent.Width - Viewport.Width);
                }

                Offset = new Vector(x, y);
                e.Handled = true;
            }
        }

        protected override void OnPropertyChanged<T>(AvaloniaPropertyChangedEventArgs<T> change)
        {
            if (change.Property == OffsetProperty && !_arranging)
            {
                InvalidateArrange();
            }

            base.OnPropertyChanged(change);
        }

        private void ChildChanged(AvaloniaPropertyChangedEventArgs e)
        {
            UpdateScrollableSubscription((IControl)e.NewValue);

            if (e.OldValue != null)
            {
                Offset = default(Vector);
            }
        }

        private void UpdateScrollableSubscription(IControl child)
        {
            var scrollable = child as ILogicalScrollable;

            _logicalScrollSubscription?.Dispose();
            _logicalScrollSubscription = null;

            if (scrollable != null)
            {
                scrollable.ScrollInvalidated += ScrollInvalidated;

                if (scrollable.IsLogicalScrollEnabled)
                {
                    _logicalScrollSubscription = new CompositeDisposable(
                        this.GetObservable(CanHorizontallyScrollProperty)
                            .Subscribe(x => scrollable.CanHorizontallyScroll = x),
                        this.GetObservable(CanVerticallyScrollProperty)
                            .Subscribe(x => scrollable.CanVerticallyScroll = x),
                        this.GetObservable(OffsetProperty)
                            .Skip(1).Subscribe(x => scrollable.Offset = x),
                        Disposable.Create(() => scrollable.ScrollInvalidated -= ScrollInvalidated));
                    UpdateFromScrollable(scrollable);
                }
            }
        }

        private void ScrollInvalidated(object sender, EventArgs e)
        {
            UpdateFromScrollable((ILogicalScrollable)sender);
        }

        private void UpdateFromScrollable(ILogicalScrollable scrollable)
        {
            var logicalScroll = _logicalScrollSubscription != null;

            if (logicalScroll != scrollable.IsLogicalScrollEnabled)
            {
                UpdateScrollableSubscription(Child);
                Offset = default(Vector);
                InvalidateMeasure();
            }
            else if (scrollable.IsLogicalScrollEnabled)
            {
                Viewport = scrollable.Viewport;
                Extent = scrollable.Extent;
                Offset = scrollable.Offset;
            }
        }

        (IControl, Rect) CalculateCurrentAnchor()
        {
            if (_anchorCandidates == null)
            {
                return default;
            }

            var thisBounds = new Rect(Bounds.Size);
            var bestCandidate = default(IControl);
            var bestCandidateDistance = double.MaxValue;

            // Find the anchor candidate that is scrolled closest to the top-left of this
            // ScrollContentPresenter.
            foreach (var element in _anchorCandidates)
            {
                if (element.IsVisible && GetViewportBounds(element, out var bounds))
                {
                    var distance = (Vector)bounds.Position;
                    var candidateDistance = Math.Abs(distance.Length);

                    if (candidateDistance < bestCandidateDistance)
                    {
                        bestCandidate = element;
                        bestCandidateDistance = candidateDistance;
                    }
                }
            }

            if (bestCandidate != null)
            {
                // We have a candidate, calculate its bounds relative to Child. Because these
                // bounds aren't relative to the ScrollContentPresenter itself, if they change
                // then we know it wasn't just due to scrolling.
                var unscrolledBounds = TranslateBounds(bestCandidate, Child);
                return (bestCandidate, unscrolledBounds);
            }

            return default;
        }

        private bool GetViewportBounds(IControl element, out Rect bounds)
        {
            // We want the bounds relative to the new Offset, regardless of whether the child
            // control has actually been arranged to this offset yet, so translate first to the
            // child control and then apply Offset rather than translating directly to this
            // control.
            var thisBounds = new Rect(Bounds.Size);
            var childBounds = TranslateBounds(element, Child);
            bounds = new Rect(childBounds.Position - Offset, childBounds.Size);
            return bounds.Intersects(thisBounds);
        }

        private Rect TranslateBounds(IControl control, IControl to)
        {
            if (TranslateBounds(control, to, out var bounds))
            {
                return bounds;
            }

            throw new InvalidOperationException("The control's bounds could not be translated to the requested control.");
        }

        private bool TranslateBounds(IControl control, IControl to, out Rect bounds)
        {
            if (!control.IsVisible)
            {
                bounds = default;
                return false;
            }

            var p = control.TranslatePoint(default, to);
            bounds = p.HasValue ? new Rect(p.Value, control.Bounds.Size) : default;
            return p.HasValue;
        }
    }
}
