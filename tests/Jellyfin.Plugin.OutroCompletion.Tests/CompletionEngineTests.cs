using Jellyfin.Plugin.OutroCompletion.Core;

namespace Jellyfin.Plugin.OutroCompletion.Tests;

public sealed class CompletionEngineTests : IDisposable
{
    private static readonly Guid User = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherUser = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Series = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid A = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid Movie = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private readonly Clock _clock = new();
    private readonly Repository _repository = new();
    private readonly Settings _settings = new();
    private readonly CompletionEngine _engine;

    public CompletionEngineTests()
    {
        _repository.Episodes[A] = new EpisodePlayback(A, A, Series, Ticks(120));
        _repository.Episodes[B] = new EpisodePlayback(B, B, Series, Ticks(120));
        _repository.Episodes[C] = new EpisodePlayback(C, C, Series, Ticks(120));
        _repository.Next[A] = B;
        _repository.Next[B] = C;
        _engine = new CompletionEngine(_repository, _settings, _clock);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(90)]
    [InlineData(100)]
    public async Task StopsInsideOutroCompleteIncludingExactBoundaries(int seconds)
    {
        await _engine.OnStoppedAsync(Report(A, seconds));
        Assert.Equal((A, User), Assert.Single(_repository.Completed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(79)]
    [InlineData(101)]
    [InlineData(119)]
    [InlineData(-1)]
    public async Task StopsOutsideOutroDoNotCompleteEvenAfterCredits(int seconds)
    {
        await _engine.OnStoppedAsync(Report(A, seconds));
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task UnknownPositionDoesNotGuessFromOldProgress()
    {
        await _engine.OnStoppedAsync(Report(A, null));
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task PlaybackStartInsideCreditsDoesNotMarkWatched()
    {
        await _engine.OnStartedAsync(Report(A, 90));
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task MissingMarkersLeavePlaybackUnchanged()
    {
        _repository.Ranges = [];
        await _engine.OnStoppedAsync(Report(A, 90));
        Assert.Empty(_repository.Completed);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(100, 80)]
    [InlineData(90, 90)]
    [InlineData(80, 121)]
    public async Task InvalidMarkersAreIgnored(int start, int end)
    {
        _repository.Ranges = [new OutroRange(Ticks(start), Ticks(end))];
        await _engine.OnStoppedAsync(Report(A, 90));
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task AnyValidOutroSegmentCanMatch()
    {
        _repository.Ranges = [new OutroRange(0, 0), new OutroRange(Ticks(10), Ticks(20)), new OutroRange(Ticks(80), Ticks(100))];
        await _engine.OnStoppedAsync(Report(A, 90));
        Assert.Single(_repository.Completed);
    }

    [Fact]
    public async Task NonEpisodesAreNeverCompleted()
    {
        await _engine.OnStoppedAsync(Report(Movie, 90));
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task DisabledPluginDoesNotEvenQuerySegments()
    {
        _settings.Enabled = false;
        await _engine.OnStoppedAsync(Report(A, 90));
        Assert.Equal(0, _repository.SegmentReads);
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task RequireNextWaitsForTheImmediateNextEpisode()
    {
        _settings.RequireNext = true;
        await BeginAndStopA();
        Assert.Empty(_repository.Completed);
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0));
        Assert.Equal((A, User), Assert.Single(_repository.Completed));
    }

    [Fact]
    public async Task DoesNotAssumeConsecutiveEpisodeNumbers()
    {
        // Repository ordering can cross a season boundary or contain a special/multi-episode file.
        _settings.RequireNext = true;
        _repository.Next[A] = C;
        await BeginAndStopA();
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(C, 0));
        Assert.Equal((A, User), Assert.Single(_repository.Completed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public async Task NextStartCanArriveBeforeThePreviousStop(int stopDelaySeconds)
    {
        _settings.RequireNext = true;
        await _engine.OnStartedAsync(Report(A, 0));
        _clock.Advance(90);
        await _engine.OnStartedAsync(Report(B, 0));
        _clock.Advance(stopDelaySeconds);
        await _engine.OnStoppedAsync(Report(A, 90));
        Assert.Equal((A, User), Assert.Single(_repository.Completed));
    }

    [Fact]
    public async Task AnOldNextStartIsNotBorrowedToConfirmADelayedStop()
    {
        _settings.RequireNext = true;
        await _engine.OnStartedAsync(Report(A, 0));
        _clock.Advance(90);
        await _engine.OnStartedAsync(Report(B, 0));
        _clock.Advance(6);
        await _engine.OnStoppedAsync(Report(A, 90));
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(C, 0));
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task CapturedTimestampsSurviveDifferentProcessingOrder()
    {
        _settings.RequireNext = true;
        await _engine.OnStartedAsync(Report(A, 0));
        _clock.Advance(90);
        var stop = Report(A, 90);
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0));
        await _engine.OnStoppedAsync(stop);
        Assert.Single(_repository.Completed);
    }

    [Theory]
    [InlineData(119, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public async Task ConfirmationWindowIsBounded(int delay, bool shouldComplete)
    {
        _settings.RequireNext = true;
        await BeginAndStopA();
        _clock.Advance(delay);
        await _engine.OnStartedAsync(Report(B, 0));
        Assert.Equal(shouldComplete ? 1 : 0, _repository.Completed.Count);
    }

    [Fact]
    public async Task SkippingAnEpisodeCancelsInsteadOfConfirmingLater()
    {
        _settings.RequireNext = true;
        await BeginAndStopA();
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(C, 0));
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0));
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task UnrelatedPlaybackCancelsPendingCompletion()
    {
        _settings.RequireNext = true;
        await BeginAndStopA();
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(Movie, 0));
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0));
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task SameEpisodeIdFromDifferentSeriesDoesNotConfirm()
    {
        _settings.RequireNext = true;
        _repository.Episodes[B] = _repository.Episodes[B] with { SeriesId = Guid.NewGuid() };
        await BeginAndStopA();
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0));
        Assert.Empty(_repository.Completed);
    }

    [Theory]
    [InlineData("other-session", "device")]
    [InlineData("session", "other-device")]
    public async Task OtherSessionsAndDevicesCannotConfirm(string session, string device)
    {
        _settings.RequireNext = true;
        await BeginAndStopA();
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0) with { SessionId = session, DeviceId = device });
        Assert.Empty(_repository.Completed);
        await _engine.OnStartedAsync(Report(B, 0));
        Assert.Single(_repository.Completed);
    }

    [Fact]
    public async Task OtherUsersCannotConfirm()
    {
        _settings.RequireNext = true;
        await BeginAndStopA();
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0) with { UserIds = [OtherUser] });
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task AdditionalUsersFollowTheirOwnPreferences()
    {
        _settings.Overrides[OtherUser] = new CompletionSettings(true, true, TimeSpan.FromSeconds(120));
        await _engine.OnStoppedAsync(Report(A, 90) with { UserIds = [User, OtherUser, User] });
        Assert.Equal((A, User), Assert.Single(_repository.Completed));
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0) with { UserIds = [User, OtherUser] });
        Assert.Equal(new[] { (A, User), (A, OtherUser) }, _repository.Completed);
    }

    [Fact]
    public async Task FinaleDoesNotCompleteWithStrictConfirmation()
    {
        _settings.RequireNext = true;
        await _engine.OnStoppedAsync(Report(C, 90));
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(A, 0));
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task FinaleDoesCompleteWithoutStrictConfirmation()
    {
        await _engine.OnStoppedAsync(Report(C, 90));
        Assert.Equal((C, User), Assert.Single(_repository.Completed));
    }

    [Fact]
    public async Task DuplicateStopsIncludingConcurrentReportsAreIdempotent()
    {
        var stop = Report(A, 90);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _engine.OnStoppedAsync(stop)));
        Assert.Single(_repository.Completed);
    }

    [Fact]
    public async Task DuplicateStartsDoNotCancelTheNextPendingEpisode()
    {
        _settings.RequireNext = true;
        await BeginAndStopA();
        _clock.Advance(1);
        var startB = Report(B, 0);
        await _engine.OnStartedAsync(startB);
        _clock.Advance(90);
        await _engine.OnStoppedAsync(Report(B, 90));
        await _engine.OnStartedAsync(startB);
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(C, 0));
        Assert.Equal(new[] { (A, User), (B, User) }, _repository.Completed);
    }

    [Fact]
    public async Task RewatchWithNewPlaySessionCanCompleteAgain()
    {
        await _engine.OnStoppedAsync(Report(A, 90));
        _clock.Advance(10);
        await _engine.OnStartedAsync(Report(A, 0) with { PlaySessionId = "rewatch-a" });
        _clock.Advance(90);
        await _engine.OnStoppedAsync(Report(A, 90) with { PlaySessionId = "rewatch-a" });
        Assert.Equal(2, _repository.Completed.Count);
    }

    [Fact]
    public async Task MissingPlaySessionIdStillSupportsTransitionsAndDeduplicatesStops()
    {
        _settings.RequireNext = true;
        await _engine.OnStartedAsync(Report(A, 0) with { PlaySessionId = null });
        _clock.Advance(90);
        var stop = Report(A, 90) with { PlaySessionId = null };
        await _engine.OnStoppedAsync(stop);
        await _engine.OnStoppedAsync(stop);
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0) with { PlaySessionId = null });
        Assert.Single(_repository.Completed);
    }

    [Fact]
    public async Task ClientsReusingAPlaySessionIdAcrossEpisodesCanConfirm()
    {
        _settings.RequireNext = true;
        await _engine.OnStartedAsync(Report(A, 0) with { PlaySessionId = "playlist" });
        _clock.Advance(90);
        await _engine.OnStoppedAsync(Report(A, 90) with { PlaySessionId = "playlist" });
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0) with { PlaySessionId = "playlist" });
        Assert.Single(_repository.Completed);
    }

    [Fact]
    public async Task ReturningToAnEpisodeWithAReusedPlaySessionIdIsANewPlayback()
    {
        await _engine.OnStartedAsync(Report(A, 0) with { PlaySessionId = "playlist" });
        _clock.Advance(90);
        await _engine.OnStoppedAsync(Report(A, 90) with { PlaySessionId = "playlist" });
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0) with { PlaySessionId = "playlist" });
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(A, 0) with { PlaySessionId = "playlist" });
        _clock.Advance(90);
        await _engine.OnStoppedAsync(Report(A, 90) with { PlaySessionId = "playlist" });
        Assert.Equal(2, _repository.Completed.Count);
    }

    [Fact]
    public async Task DisablingBeforeConfirmationPreventsCompletion()
    {
        _settings.RequireNext = true;
        await BeginAndStopA();
        _settings.Enabled = false;
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0));
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task ExplicitUserDataEditCancelsOlderCompletion()
    {
        _settings.RequireNext = true;
        await BeginAndStopA();
        _clock.Advance(1);
        _engine.Invalidate(User, A);
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0));
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task ReplayingOldEpisodeOnAnotherDeviceProtectsItsNewResumePoint()
    {
        _settings.RequireNext = true;
        await BeginAndStopA();
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(A, 0) with { SessionId = "second-session", PlaySessionId = "rewatch" });
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0));
        Assert.Empty(_repository.Completed);
    }

    [Fact]
    public async Task AlternateVersionUsesItsOwnProgressIdButConfirmsLogicalNextEpisode()
    {
        _settings.RequireNext = true;
        var version = Guid.NewGuid();
        _repository.Episodes[A] = _repository.Episodes[A] with { ProgressItemId = version };
        await BeginAndStopA();
        _clock.Advance(1);
        await _engine.OnStartedAsync(Report(B, 0));
        Assert.Equal((version, User), Assert.Single(_repository.Completed));
    }

    [Fact]
    public async Task MissingSessionOrRealUserIsIgnored()
    {
        await _engine.OnStoppedAsync(Report(A, 90) with { SessionId = "" });
        await _engine.OnStoppedAsync(Report(A, 90) with { UserIds = [Guid.Empty] });
        Assert.Empty(_repository.Completed);
    }

    public void Dispose() => _engine.Dispose();

    private async Task BeginAndStopA()
    {
        await _engine.OnStartedAsync(Report(A, 0));
        _clock.Advance(90);
        await _engine.OnStoppedAsync(Report(A, 90));
    }

    private PlaybackReport Report(Guid item, int? seconds) => new(
        item, [User], "session", "device", $"play-{item:N}", null,
        seconds.HasValue ? Ticks(seconds.Value) : null, _clock.GetUtcNow());

    private static long Ticks(int seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }

    private sealed class Settings : ICompletionSettingsProvider
    {
        public bool Enabled { get; set; } = true;
        public bool RequireNext { get; set; }
        public Dictionary<Guid, CompletionSettings> Overrides { get; } = [];

        public CompletionSettings GetForUser(Guid userId) => Overrides.GetValueOrDefault(userId)
            ?? new CompletionSettings(Enabled, RequireNext, TimeSpan.FromSeconds(120));
    }

    private sealed class Repository : IPlaybackRepository
    {
        public Dictionary<Guid, EpisodePlayback> Episodes { get; } = [];
        public Dictionary<Guid, Guid> Next { get; } = [];
        public List<(Guid ItemId, Guid UserId)> Completed { get; } = [];
        public IReadOnlyList<OutroRange> Ranges { get; set; } = [new(Ticks(80), Ticks(100))];
        public int SegmentReads { get; private set; }

        public EpisodePlayback? ResolveEpisode(PlaybackReport report, Guid userId) => Episodes.GetValueOrDefault(report.ItemId);

        public Task<IReadOnlyList<OutroRange>> GetOutroRangesAsync(EpisodePlayback episode, CancellationToken cancellationToken)
        {
            SegmentReads++;
            return Task.FromResult(Ranges);
        }

        public Guid? GetNextEpisodeId(EpisodePlayback episode, Guid userId) => Next.TryGetValue(episode.Id, out var id) ? id : null;

        public void MarkCompleted(EpisodePlayback episode, Guid userId) => Completed.Add((episode.ProgressItemId, userId));
    }
}
