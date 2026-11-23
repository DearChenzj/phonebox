using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>Translates relative phone gestures into Core Graphics events in the current Mac desktop session.</summary>
internal sealed class MacTrackpad(
    Func<MacTrackpad.Point> cursor,
    Action<MacTrackpad.MouseEvent, MacTrackpad.Point> mouse,
    Action<int, int> scroll)
{
    internal static MacTrackpad SystemPointer { get; } = new(CursorPosition, PostMouse, PostScroll);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Point(double X, double Y);

    internal enum MouseEvent : uint { LeftDown = 1, LeftUp, RightDown, RightUp, Move, LeftDrag }

    private const int DragIdleMs = 10_000;
    private readonly object _lock = new();
    private Timer? _releaseTimer;
    private long _dragDeadline;
    private bool _leftDown;

    internal void Move(int dx, int dy)
    {
        lock (_lock)
        {
            var point = cursor();
            mouse(_leftDown ? MouseEvent.LeftDrag : MouseEvent.Move, new(point.X + dx, point.Y + dy));
            if (_leftDown) RefreshDrag();
        }
    }

    internal void Click(bool right = false)
    {
        lock (_lock)
        {
            if (_leftDown) return;
            var point = cursor();
            mouse(right ? MouseEvent.RightDown : MouseEvent.LeftDown, point);
            mouse(right ? MouseEvent.RightUp : MouseEvent.LeftUp, point);
        }
    }

    internal void Down()
    {
        lock (_lock)
        {
            if (!_leftDown)
            {
                mouse(MouseEvent.LeftDown, cursor());
                _leftDown = true;
            }
            RefreshDrag();
        }
    }

    internal void Up()
    {
        lock (_lock)
        {
            if (!_leftDown) return;
            _leftDown = false;
            _releaseTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            mouse(MouseEvent.LeftUp, cursor());
        }
    }

    internal void Scroll(int dx, int dy)
    {
        lock (_lock)
        {
            // The widget uses wheel deltas. Scale them into pixels and reverse Quartz's horizontal axis.
            scroll(-ToPixels(dx), ToPixels(dy));
        }
    }

    private static int ToPixels(int delta) => delta == 0 ? 0 : Math.Sign(delta) * Math.Max(1, (int)(Math.Abs((long)delta) / 3));

    private void RefreshDrag()
    {
        _dragDeadline = Environment.TickCount64 + DragIdleMs;
        _releaseTimer ??= new Timer(_ => ReleaseInactiveDrag());
        _releaseTimer.Change(DragIdleMs, Timeout.Infinite);
    }

    private void ReleaseInactiveDrag()
    {
        lock (_lock)
        {
            // USB uses individual requests, so a disconnected phone may never send the final mouse-up.
            if (!_leftDown) return;
            var remaining = _dragDeadline - Environment.TickCount64;
            if (remaining > 0)
            {
                _releaseTimer?.Change(remaining, Timeout.Infinite);
                return;
            }
            try { Up(); } catch (InvalidOperationException) { /* Permission may have been revoked during the drag. */ }
        }
    }

    private static Point CursorPosition()
    {
        var nativeEvent = CGEventCreate(IntPtr.Zero);
        if (nativeEvent == IntPtr.Zero) throw new InvalidOperationException("Could not read the Mac mouse position.");
        try { return CGEventGetLocation(nativeEvent); }
        finally { CFRelease(nativeEvent); }
    }

    private static void PostMouse(MouseEvent type, Point point)
    {
        RequirePermission();
        uint button = type is MouseEvent.RightDown or MouseEvent.RightUp ? 1u : 0u;
        var nativeEvent = CGEventCreateMouseEvent(IntPtr.Zero, type, point, button);
        const uint clickState = 1;
        if (nativeEvent != IntPtr.Zero && type is MouseEvent.LeftDown or MouseEvent.LeftUp or MouseEvent.RightDown or MouseEvent.RightUp)
            CGEventSetIntegerValueField(nativeEvent, clickState, 1);
        Post(nativeEvent);
    }

    private static void PostScroll(int dx, int dy)
    {
        RequirePermission();
        const uint pixelUnits = 0;
        Post(CGEventCreateScrollWheelEvent2(IntPtr.Zero, pixelUnits, 2, dy, dx, 0));
    }

    private static void Post(IntPtr nativeEvent)
    {
        if (nativeEvent == IntPtr.Zero) throw new InvalidOperationException("Could not create a Mac mouse event.");
        const uint hidEventTap = 0;
        try { CGEventPost(hidEventTap, nativeEvent); }
        finally { CFRelease(nativeEvent); }
    }

    private static void RequirePermission()
    {
        if (!CGPreflightPostEventAccess()) throw new InvalidOperationException(KeySender.AccessibilityHint);
    }

    private const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(Graphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGPreflightPostEventAccess();

    [DllImport(Graphics)]
    private static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport(Graphics)]
    private static extern Point CGEventGetLocation(IntPtr nativeEvent);

    [DllImport(Graphics)]
    private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, MouseEvent type, Point point, uint button);

    [DllImport(Graphics)]
    private static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, uint units, uint count, int vertical, int horizontal, int third);

    [DllImport(Graphics)]
    private static extern void CGEventPost(uint tap, IntPtr nativeEvent);

    [DllImport(Graphics)]
    private static extern void CGEventSetIntegerValueField(IntPtr nativeEvent, uint field, long value);

    [DllImport(Foundation)]
    private static extern void CFRelease(IntPtr reference);
}
