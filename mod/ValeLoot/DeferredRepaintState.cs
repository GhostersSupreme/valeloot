using System;

namespace ValeLoot;

/// <summary>
/// Coalesces targeted redraws and exposes them only after one complete main-thread tick.
/// </summary>
internal struct DeferredRepaintState
{
    private IntPtr _target;
    private int _ticksRemaining;

    public readonly IntPtr Target => _target;
    public readonly bool Pending => _target != IntPtr.Zero;

    public void Queue(IntPtr target)
    {
        if (target == IntPtr.Zero) return;
        _target = target;
        _ticksRemaining = 1;
    }

    public bool Cancel(IntPtr target)
    {
        if (_target != target) return false;
        Clear();
        return true;
    }

    public bool TryTake(out IntPtr target)
    {
        target = IntPtr.Zero;
        if (_target == IntPtr.Zero || _ticksRemaining-- > 0) return false;

        target = _target;
        Clear();
        return true;
    }

    public void Clear()
    {
        _target = IntPtr.Zero;
        _ticksRemaining = 0;
    }
}
