using System;
using ValeLoot;

int assertions = 0;

void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

IntPtr firstTab = new(1);
IntPtr secondTab = new(2);
var state = new DeferredRepaintState();

state.Queue(firstTab);
Assert(state.Pending, "a targeted redraw must queue a repaint");
Assert(!state.TryTake(out _), "a targeted redraw must not repaint in the same tick");
Assert(state.TryTake(out IntPtr ready) && ready == firstTab,
    "the repaint must become ready after one complete tick");
Assert(!state.Pending, "taking a repaint must clear the queue");

state.Queue(firstTab);
state.Queue(secondTab);
Assert(!state.TryTake(out _), "a replacement redraw must restart the stable-tick delay");
Assert(state.TryTake(out ready) && ready == secondTab,
    "multiple redraws must coalesce onto the latest tab");

state.Queue(firstTab);
Assert(state.Cancel(firstTab), "a completed RenderPage must cancel its queued repaint");
Assert(!state.TryTake(out _), "a cancelled repaint must never run");

Console.WriteLine($"Deferred repaint regression: {assertions} assertions passed.");
