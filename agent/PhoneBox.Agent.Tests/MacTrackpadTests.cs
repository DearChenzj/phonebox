using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class MacTrackpadTests
{
    [Fact]
    public void MovementDuringAHoldPostsADragUntilTheButtonIsReleased()
    {
        var cursor = new MacTrackpad.Point(100, 200);
        var events = new List<(MacTrackpad.MouseEvent, MacTrackpad.Point)>();
        var mouse = new MacTrackpad(() => cursor, (type, point) =>
        {
            events.Add((type, point));
            cursor = point;
        }, (_, _) => { });

        mouse.Move(12, -6);
        mouse.Down();
        mouse.Move(7, 8);
        mouse.Up();
        mouse.Move(-5, 3);

        Assert.Equal(
            new (MacTrackpad.MouseEvent, MacTrackpad.Point)[]
            {
                (MacTrackpad.MouseEvent.Move, new(112, 194)),
                (MacTrackpad.MouseEvent.LeftDown, new(112, 194)),
                (MacTrackpad.MouseEvent.LeftDrag, new(119, 202)),
                (MacTrackpad.MouseEvent.LeftUp, new(119, 202)),
                (MacTrackpad.MouseEvent.Move, new(114, 205)),
            },
            events);
    }
}
