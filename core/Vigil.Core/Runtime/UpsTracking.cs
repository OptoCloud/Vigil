using Microsoft.Extensions.Logging;
using Vigil.Core.Alerting;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using Vigil.Core.Protocol;

namespace Vigil.Core.Runtime;

/// <summary>
/// The runtime's UPS state: the latest reading or error, how long it has been unreadable or
/// unguarded, and the debouncer for the UPS conditions. Split out of the runtime as
/// <see cref="PoolWatch"/> is (docs/STYLE.md §2.1); the runtime still does the persisting and
/// the broadcasting, so this is state and judgement only. One owner, no locks.
/// </summary>
internal sealed class UpsTracking
{
    /// <summary>
    /// The UPS reports its own state, so one poll is enough: an OB flag is a real power event,
    /// not a flaky read.
    /// </summary>
    private const uint Threshold = 1;

    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConditionDebouncer _debouncer;
    private long? _unreadableSince;
    private long? _unguardedSince;

    public UpsTracking(VigilOptions options, ILogger logger, TimeProvider time)
    {
        _logger = logger;
        _time = time;
        _debouncer = new ConditionDebouncer(Threshold, time);
        State = new UpsState
        {
            Enabled = options.NutHost is { Length: > 0 },
            Name = options.NutUps,
            Reading = null,
            Error = null,
            Limits = new UpsLimits
            {
                HostShutdownSeconds = options.HostShutdownSeconds,
                BatteryTemperatureWarn = options.UpsBatteryTemperatureWarn,
            },
        };
        _unreadableSince = time.GetTimestamp();
    }

    public UpsState State { get; private set; }

    public IEnumerable<Alert> Raised => _debouncer.Raised;

    /// <summary>Whether a tick has anything to judge: the unreadable and unguarded conditions depend on time.</summary>
    public bool IsWaiting => State.Enabled && (_unreadableSince is not null || _unguardedSince is not null);

    /// <summary>Takes one poll. Returns the reading worth keeping in history, if it was one.</summary>
    public UpsReading? Observe(UpsPoll poll)
    {
        switch (poll)
        {
            case UpsPoll.Read { Reading: var reading }:
                if (State.Reading is null && _logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("reading the UPS: {Status}", string.Join(' ', reading.Status));
                }

                // A reading that names no power source is kept for the dashboard, but for alerting
                // it is no reading at all, and the time without one keeps counting.
                _unreadableSince = UpsConditions.NamesPowerSource(reading) ? null : _unreadableSince ?? _time.GetTimestamp();
                _unguardedSince = reading.Monitors == 0 ? _unguardedSince ?? _time.GetTimestamp() : null;
                State = State with { Reading = reading, Error = null };
                return reading;

            case UpsPoll.Failed { Reason: var error }:
                if (State.Error != error)
                {
                    _logger.LogWarning("cannot read the UPS: {Error}", error);
                }

                _unreadableSince ??= _time.GetTimestamp();
                State = State with { Reading = null, Error = error };
                return null;

            default:
                return null;
        }
    }

    /// <summary>Judges the UPS conditions now; returns what was raised or cleared.</summary>
    public List<NewEvent> Evaluate()
    {
        var watch = new Alerting.UpsWatch(
            UnreadableFor: Elapsed(_unreadableSince),
            UnguardedFor: Elapsed(_unguardedSince),
            BatteryHotRaised: _debouncer.IsRaised(UpsConditions.BatteryHot));

        // Unreadable, the UPS's power state is unknown, not back to normal: the conditions raised
        // before stay raised until a reading says otherwise.
        return _debouncer.Update(UpsConditions.Of(State, watch), isComplete: _unreadableSince is null);
    }

    /// <summary>How long ago <paramref name="since"/> was; zero when it is not set.</summary>
    private TimeSpan Elapsed(long? since) => since is { } at ? _time.GetElapsedTime(at) : TimeSpan.Zero;
}
