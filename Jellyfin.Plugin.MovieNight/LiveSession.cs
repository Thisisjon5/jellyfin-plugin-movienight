using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MovieNight.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// The live broadcast, end to end: movie feeder and slate (switcher v3, unchanged, owned by
/// <see cref="BroadcastManager"/>) feeding one ladder encoder (<see cref="LadderEncoder"/>) whose
/// HLS output the custom tuner host hands straight to clients. This is ROADMAP-2026-09-03.md
/// step 1: M2 + M3 + M4 of the ladder design, collapsed.
/// <para>
/// Pause and resume are the v3 feed swaps. The encoder is the feed's one permanent consumer, so
/// the feeder runs whether or not anyone is tuned - live-channel semantics, and it retires the
/// zero-viewer respawn dance because the consumer never disconnects.
/// </para>
/// </summary>
public sealed class LiveSession : IDisposable
{
    private const int MaxRestarts = 5;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly BroadcastManager _broadcastManager;
    private readonly LadderEncoder _encoder;
    private readonly MezzaninePrep _prep;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<LiveSession> _logger;

    private string _state = "Idle";
    private bool _stopping;
    private Guid _itemId;
    private string? _itemName;
    private long? _runTimeTicks;
    private string? _sourcePath;
    private bool _usingMezzanine;
    private DateTime? _startedUtc;
    private string? _lastFailure;
    private int _restarts;
    private string? _channelName;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveSession"/> class.
    /// </summary>
    /// <param name="broadcastManager">Owns the feeder/slate half (switcher v3).</param>
    /// <param name="encoder">Owns the ladder encoder process.</param>
    /// <param name="prep">Knows whether a mezzanine exists for an item.</param>
    /// <param name="libraryManager">Resolves item ids.</param>
    /// <param name="appHost">The server's own HTTP port, for the loopback feed URL.</param>
    /// <param name="configurationManager">The server's hardware-acceleration setting.</param>
    /// <param name="logger">Logger.</param>
    public LiveSession(
        BroadcastManager broadcastManager,
        LadderEncoder encoder,
        MezzaninePrep prep,
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        IServerConfigurationManager configurationManager,
        ILogger<LiveSession> logger)
    {
        _broadcastManager = broadcastManager;
        _encoder = encoder;
        _prep = prep;
        _libraryManager = libraryManager;
        _appHost = appHost;
        _configurationManager = configurationManager;
        _logger = logger;
        _encoder.ExitedCallback = OnEncoderExited;
    }

    /// <summary>Gets a value indicating whether the channel is live (tunable).</summary>
    public bool IsLive
    {
        get
        {
            lock (_lock)
            {
                return _state == "Live";
            }
        }
    }

    /// <summary>Gets the channel name in effect for this broadcast.</summary>
    public string ChannelName
    {
        get
        {
            lock (_lock)
            {
                return _channelName ?? Plugin.Instance?.Configuration.ChannelName ?? "Movie Night";
            }
        }
    }

    /// <summary>Gets the movie's name, while live.</summary>
    public string? NowPlaying
    {
        get
        {
            lock (_lock)
            {
                return _itemName;
            }
        }
    }

    /// <summary>Gets when the broadcast went live.</summary>
    public DateTime? StartedUtc
    {
        get
        {
            lock (_lock)
            {
                return _startedUtc;
            }
        }
    }

    /// <summary>Gets the movie's runtime in ticks, if known.</summary>
    public long? RunTimeTicks
    {
        get
        {
            lock (_lock)
            {
                return _runTimeTicks;
            }
        }
    }

    /// <summary>
    /// Goes live with an item: configures the feeder, starts the ladder encoder with the rung
    /// count from settings, waits for the first playable output, then announces the channel.
    /// </summary>
    /// <param name="itemId">Library item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success and a message (the encoder's stderr tail on failure).</returns>
    public async Task<(bool Success, string Message)> GoLiveAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
#pragma warning disable CA3003 // itemId is an opaque library key resolved by Jellyfin, not a caller-supplied path
        if (item is null || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            return (false, $"Item {itemId} not found or has no file on disk");
        }
#pragma warning restore CA3003

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var mezzanine = _prep.GetMezzaninePath(itemId);
        var source = mezzanine ?? item.Path;

        lock (_lock)
        {
            if (_state is "Starting" or "Live")
            {
                return (false, "Already live - stop first");
            }

            _state = "Starting";
            _stopping = false;
            _itemId = itemId;
            _itemName = item.Name;
            _runTimeTicks = item.RunTimeTicks;
            _sourcePath = source;
            _usingMezzanine = mezzanine is not null;
            _startedUtc = null;
            _lastFailure = null;
            _restarts = 0;
            _channelName = string.IsNullOrWhiteSpace(config.ChannelName) ? "Movie Night" : config.ChannelName;
        }

        if (mezzanine is null)
        {
            _logger.LogWarning("Movie Night: no mezzanine prepared for {Name} - feeding the original file; the live encoder will decode it at full size and there are no captions", item.Name);
        }

        var encodingOptions = _configurationManager.GetEncodingOptions();
        var accel = HardwareAccelMapper.Map(encodingOptions.HardwareAccelerationType);
        var rungs = LadderCommandBuilder.PlanRungs(config.LadderRungs, config.TopRungBitrateKbps);
        var feedUrl = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{_appHost.HttpPort}/MovieNight/stream/feed.ts");
        var args = LadderCommandBuilder.Build(feedUrl, _encoder.LadderDirectory, rungs, accel, encodingOptions.VaapiDevice);

        // Feeder side first (it starts lazily when the encoder connects to feed.ts), then the
        // encoder itself.
        _broadcastManager.ConfigureSwitcherV2(source, _appHost.HttpPort);

        if (!_encoder.Start(args, rungs, accel, cleanDirectory: true))
        {
            await FailAsync("ladder encoder process failed to start (see server log)").ConfigureAwait(false);
            return (false, "ladder encoder process failed to start");
        }

        var ready = await _encoder.WaitForOutputAsync(StartupTimeout, cancellationToken).ConfigureAwait(false);
        if (!ready)
        {
            var stderr = _encoder.StderrTail;
            var message = FormattableString.Invariant($"no playable output within {StartupTimeout.TotalSeconds:F0}s. ffmpeg stderr: {stderr}");
            await FailAsync(message).ConfigureAwait(false);
            return (false, message);
        }

        lock (_lock)
        {
            _state = "Live";
            _startedUtc = DateTime.UtcNow;
        }

        _logger.LogInformation("Movie Night: LIVE - {Name} ({Rungs} rung(s), {Accel}, source {Source})", item.Name, rungs.Count, accel, _usingMezzanine ? "mezzanine" : "original");
        _broadcastManager.RefreshChannelsGuide();
        return (true, FormattableString.Invariant($"live: {item.Name}"));
    }

    /// <summary>Stops the broadcast: encoder killed, feeders cleared, channel withdrawn.</summary>
    /// <returns>A task.</returns>
    public async Task StopAsync()
    {
        lock (_lock)
        {
            _stopping = true;
        }

        await _encoder.StopAsync().ConfigureAwait(false);
        _broadcastManager.ClearSwitcherV2();

        lock (_lock)
        {
            _state = "Idle";
            _itemName = null;
            _sourcePath = null;
            _startedUtc = null;
            _runTimeTicks = null;
        }

        _logger.LogInformation("Movie Night: stopped");
        _broadcastManager.RefreshChannelsGuide();
    }

    /// <summary>Pauses for everyone (v3 feed swap to the slate).</summary>
    /// <returns>Success and the pause position.</returns>
    public Task<(bool Success, string Output)> PauseAsync()
        => IsLive ? _broadcastManager.PauseSwitcherV2Async() : Task.FromResult((false, "Not live"));

    /// <summary>Resumes for everyone (v3 feed swap back to the movie, a little before the pause point).</summary>
    /// <returns>Success and the resume position.</returns>
    public Task<(bool Success, string Output)> ResumeAsync()
        => IsLive ? _broadcastManager.ResumeSwitcherV2Async() : Task.FromResult((false, "Not live"));

    /// <summary>Snapshot for the host page and the API.</summary>
    /// <returns>Session state plus the feed and encoder sub-statuses.</returns>
    public object GetStatus()
    {
        lock (_lock)
        {
            return new
            {
                State = _state,
                ItemId = _state == "Idle" ? null : (Guid?)_itemId,
                NowPlaying = _itemName,
                ChannelName = ChannelName,
                StartedUtc = _startedUtc,
                RunTimeTicks = _runTimeTicks,
                SourcePath = _sourcePath,
                UsingMezzanine = _usingMezzanine,
                EncoderRestarts = _restarts,
                LastFailure = _lastFailure,
                Feed = _broadcastManager.GetSwitcherV2Status(),
                Encoder = _encoder.GetStatus(),
            };
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _encoder.ExitedCallback = null;
    }

    private async Task FailAsync(string reason)
    {
        _logger.LogError("Movie Night: Go Live failed - {Reason}", reason);
        lock (_lock)
        {
            _stopping = true;
        }

        await _encoder.StopAsync().ConfigureAwait(false);
        _broadcastManager.ClearSwitcherV2();
        lock (_lock)
        {
            _state = "Failed";
            _lastFailure = reason;
        }
    }

    private void OnEncoderExited(int exitCode)
    {
        int attempt;
        lock (_lock)
        {
            if (_stopping || _state != "Live")
            {
                return;
            }

            _restarts++;
            attempt = _restarts;
        }

        if (attempt > MaxRestarts)
        {
            _logger.LogError("Movie Night: ladder encoder died {Count} times; giving up and stopping the broadcast", attempt - 1);
            _ = Task.Run(async () =>
            {
                await StopAsync().ConfigureAwait(false);
                lock (_lock)
                {
                    _state = "Failed";
                    _lastFailure = FormattableString.Invariant($"ladder encoder crashed repeatedly (last exit {exitCode})");
                }
            });
            return;
        }

        _logger.LogWarning("Movie Night: ladder encoder exited ({Code}) while live - restart {Attempt}/{Max} in {Delay}s. Clients will see the playlist sequence reset.", exitCode, attempt, MaxRestarts, RestartDelay.TotalSeconds);
        _ = Task.Run(async () =>
        {
            await Task.Delay(RestartDelay).ConfigureAwait(false);
            lock (_lock)
            {
                if (_stopping || _state != "Live")
                {
                    return;
                }
            }

            if (!_encoder.Restart())
            {
                _logger.LogError("Movie Night: ladder encoder restart failed to spawn");
            }
        });
    }
}
