namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Maps the progress of one step, from 0 to 100, onto its share of a longer task.
/// </summary>
public sealed class ProgressRange : IProgress<double>
{
    private readonly IProgress<double> _parent;
    private readonly double _from;
    private readonly double _to;
    private double _last;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressRange"/> class.
    /// </summary>
    /// <param name="parent">Progress of the longer task.</param>
    /// <param name="from">Where the step starts.</param>
    /// <param name="to">Where the step ends.</param>
    public ProgressRange(IProgress<double> parent, double from, double to)
    {
        _parent = parent;
        _from = from;
        _to = to;
    }

    /// <inheritdoc />
    public void Report(double value)
    {
        // a scan already under way can end and start over, the bar must not go back
        _last = Math.Max(_last, Math.Clamp(value, 0, 100));
        _parent.Report(_from + ((_to - _from) * _last / 100));
    }

    /// <summary>
    /// Reports a count out of a total.
    /// </summary>
    /// <param name="done">Items done.</param>
    /// <param name="total">Items in all.</param>
    public void Report(int done, int total) => Report(total == 0 ? 100 : done * 100d / total);
}
