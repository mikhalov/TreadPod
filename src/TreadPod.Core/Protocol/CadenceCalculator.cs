namespace TreadPod.Core.Protocol;

/// <summary>
/// Computes running cadence (steps per minute) from the R2 cumulative step counter over a rolling window.
/// </summary>
public sealed class CadenceCalculator
{
    private readonly double _windowSeconds;
    private readonly LinkedList<(double TimeSeconds, int Steps)> _points = new();

    public CadenceCalculator(double windowSeconds = 10.0)
    {
        if (windowSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        }

        _windowSeconds = windowSeconds;
    }

    public int Update(double timeSeconds, int steps)
    {
        if (_points.Last is { } last && steps < last.Value.Steps)
        {
            // Counter reset on a new R2 session.
            _points.Clear();
        }

        _points.AddLast((timeSeconds, steps));

        while (_points.Count > 2 &&
               _points.First?.Next is { } second &&
               timeSeconds - second.Value.TimeSeconds >= _windowSeconds)
        {
            _points.RemoveFirst();
        }

        var first = _points.First!.Value;
        if (timeSeconds <= first.TimeSeconds || steps < first.Steps)
        {
            return 0;
        }

        return (int)Math.Round(
            (steps - first.Steps) * 60.0 / (timeSeconds - first.TimeSeconds),
            MidpointRounding.ToEven);
    }

    public void Reset() => _points.Clear();
}
