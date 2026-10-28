using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.OutroCompletion.Core;
using Jellyfin.Plugin.OutroCompletion.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.OutroCompletion.Tests;

[Collection("Jellyfin entity statics")]
public sealed class RepositoryTests
{
    [Fact]
    public void CompletionPatchesOnlyPlayedAndResumeState()
    {
        var episode = new Episode { Id = Guid.NewGuid(), Path = "/media/episode.mkv" };
        var user = new User("test", "authentication", "password-reset");
        var original = new UserItemData { Key = "test", PlayCount = 7, IsFavorite = true, PlaybackPositionTicks = 900, AudioStreamIndex = 2 };
        UpdateUserItemDataDto? saved = null;
        UserDataSaveReason? reason = null;
        var library = Stub<ILibraryManager>((method, _) => method.Name switch
        {
            "GetItemById" => episode,
            "GetLocalAlternateVersionIds" => Array.Empty<Guid>(),
            _ => throw new NotSupportedException(method.Name)
        });
        var users = Stub<IUserManager>((_, _) => user);
        var data = Stub<IUserDataManager>((method, args) =>
        {
            if (method.Name == "GetUserData") return original;
            if (method.Name == "SaveUserData")
            {
                saved = Assert.IsType<UpdateUserItemDataDto>(args![2]);
                reason = Assert.IsType<UserDataSaveReason>(args[3]);
                return null;
            }

            throw new NotSupportedException(method.Name);
        });
        var segments = Stub<IMediaSegmentManager>((_, _) => throw new NotSupportedException());
        var repository = new JellyfinPlaybackRepository(library, users, data, segments, NullLogger<JellyfinPlaybackRepository>.Instance);
        var previousLibrary = BaseItem.LibraryManager;
        try
        {
            BaseItem.LibraryManager = library;
            repository.MarkCompleted(new EpisodePlayback(episode.Id, episode.Id, Guid.NewGuid(), 1000), user.Id);
        }
        finally
        {
            BaseItem.LibraryManager = previousLibrary;
        }
        Assert.NotNull(saved);
        Assert.True(saved.Played);
        Assert.Equal(0, saved.PlaybackPositionTicks);
        Assert.Null(saved.PlayCount);
        Assert.Null(saved.LastPlayedDate);
        Assert.Null(saved.Rating);
        Assert.Null(saved.IsFavorite);
        Assert.Equal(UserDataSaveReason.PlaybackFinished, reason);
        Assert.Equal(7, original.PlayCount);
        Assert.True(original.IsFavorite);
        Assert.Equal(2, original.AudioStreamIndex);
    }

    [Fact]
    public async Task QueriesNativeOutroSegmentsForThePlayedVersionAndEnabledProviders()
    {
        var item = new Episode { Id = Guid.NewGuid(), Path = "/media/alternate.mkv" };
        var options = new LibraryOptions();
        var library = Stub<ILibraryManager>((method, args) => method.Name switch
        {
            "GetItemById" => item,
            "GetLibraryOptions" => options,
            _ => throw new NotSupportedException(method.Name)
        });
        var segmentManager = Stub<IMediaSegmentManager>((method, args) =>
        {
            Assert.Equal("GetSegmentsAsync", method.Name);
            Assert.Same(item, args![0]);
            Assert.Equal(new[] { MediaSegmentType.Outro }, Assert.IsAssignableFrom<IEnumerable<MediaSegmentType>>(args[1]));
            Assert.Same(options, args[2]);
            Assert.Equal(true, args[3]);
            return Task.FromResult<IEnumerable<MediaSegmentDto>>([
                new() { Type = MediaSegmentType.Outro, StartTicks = 80, EndTicks = 100 },
                new() { Type = MediaSegmentType.Intro, StartTicks = 5, EndTicks = 10 }
            ]);
        });
        var repository = new JellyfinPlaybackRepository(library,
            Stub<IUserManager>((_, _) => null), Stub<IUserDataManager>((_, _) => null),
            segmentManager, NullLogger<JellyfinPlaybackRepository>.Instance);
        var ranges = await repository.GetOutroRangesAsync(new EpisodePlayback(Guid.NewGuid(), item.Id, Guid.NewGuid(), 120), default);
        Assert.Equal(new OutroRange(80, 100), Assert.Single(ranges));
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceStub>();
        ((InterfaceStub)(object)proxy).Handler = invoke;
        return proxy;
    }

    public class InterfaceStub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}

[CollectionDefinition("Jellyfin entity statics", DisableParallelization = true)]
public sealed class JellyfinEntityCollection;
