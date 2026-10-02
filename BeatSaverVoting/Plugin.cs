using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BS_Utils.Utilities;
using HarmonyLib;
using IPA;
using IPA.Utilities;
using IPA.Utilities.Async;
using IPALogger = IPA.Logging.Logger;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEngine;

namespace BeatSaverVoting
{
    public delegate void VoteCallback(string hash, bool success, bool userDirection, int newTotal);

    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        private sealed class CoroutineRunner : MonoBehaviour
        {
        }

        private static readonly UI.VotingUI VotingView = new UI.VotingUI();
        private static Harmony _harmony;
        private static CoroutineRunner _coroutineRunner;
        private static readonly object VoteFileGate = new object();
        private static readonly List<Task<Exception>> PendingVoteWrites = new List<Task<Exception>>();
        private static Task _voteFileTail = Task.CompletedTask;
        private static Task<Dictionary<string, SongVote>> _voteLoadTask;
        private static bool _votesReady;
        private static bool _startupReady;
        private static bool _exiting;

        private sealed class VoteFileRequest
        {
            private readonly string _path;
            private readonly KeyValuePair<string, SongVote>[] _votes;

            internal VoteFileRequest(string path, KeyValuePair<string, SongVote>[] votes)
            {
                _path = path;
                _votes = votes;
            }

            internal Dictionary<string, SongVote> Load(Task previous)
            {
                if (!File.Exists(_path))
                {
                    var error = Write(previous);
                    if (error != null)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                    return null;
                }

                return JsonConvert.DeserializeObject<Dictionary<string, SongVote>>(File.ReadAllText(_path, Encoding.UTF8));
            }

            internal Exception Write(Task previous)
            {
                try
                {
                    var votes = new Dictionary<string, SongVote>(StringComparer.OrdinalIgnoreCase);
                    foreach (var vote in _votes)
                        votes.Add(vote.Key, vote.Value);
                    File.WriteAllText(_path, JsonConvert.SerializeObject(votes), Encoding.UTF8);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }
        }

        public enum VoteType { Upvote, Downvote };

        public struct SongVote
        {
            public string hash;
            [JsonConverter(typeof(StringEnumConverter))]
            public VoteType voteType;

            public SongVote(string hash, VoteType voteType)
            {
                this.hash = hash;
                this.voteType = voteType;
            }
        }

        public static void VoteForSong(string hash, VoteType type, VoteCallback callback)
        {
            VotingView.VoteForSong(hash, type == VoteType.Upvote, callback);
        }

        public static VoteType? CurrentVoteStatus(string hash)
        {
            return votedSongs.TryGetValue(hash, out var vote) ? vote.voteType : (VoteType?) null;
        }

        internal const string BeatsaverURL = "https://api.beatsaver.com";
        private static readonly string VotedSongsPath = $"{Environment.CurrentDirectory}/UserData/votedSongs.json";
        internal static Dictionary<string, SongVote> votedSongs = new Dictionary<string, SongVote>(StringComparer.OrdinalIgnoreCase);

        internal static HMUI.TableView tableView;
        internal static Sprite favoriteIcon;
        internal static Sprite favoriteUpvoteIcon;
        internal static Sprite favoriteDownvoteIcon;
        internal static Sprite upvoteIcon;
        internal static Sprite downvoteIcon;

        [OnStart]
        public async Task OnApplicationStart()
        {
            var request = new VoteFileRequest(VotedSongsPath, votedSongs.ToArray());
            lock (VoteFileGate)
            {
                _voteLoadTask = _voteFileTail.ContinueWith(request.Load, CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
                _voteFileTail = _voteLoadTask;
            }

            BSEvents.lateMenuSceneLoadedFresh += BSEvents_menuSceneLoadedFresh;
            BSEvents.gameSceneLoaded += BSEvents_gameSceneLoaded;

            favoriteIcon = await BeatSaberMarkupLanguage.Utilities.LoadSpriteFromAssemblyAsync("BeatSaverVoting.Icons.Favorite.png");
            favoriteUpvoteIcon = await BeatSaberMarkupLanguage.Utilities.LoadSpriteFromAssemblyAsync("BeatSaverVoting.Icons.FavoriteUpvote.png");
            favoriteDownvoteIcon = await BeatSaberMarkupLanguage.Utilities.LoadSpriteFromAssemblyAsync("BeatSaverVoting.Icons.FavoriteDownvote.png");
            upvoteIcon = await BeatSaberMarkupLanguage.Utilities.LoadSpriteFromAssemblyAsync("BeatSaverVoting.Icons.Upvote.png");
            downvoteIcon = await BeatSaberMarkupLanguage.Utilities.LoadSpriteFromAssemblyAsync("BeatSaverVoting.Icons.Downvote.png");

            Dictionary<string, SongVote> loaded = null;
            Exception loadError = null;
            try
            {
                loaded = await _voteLoadTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                loadError = exception;
            }

            await UnityMainThreadTaskScheduler.Factory.StartNew(() =>
            {
                if (_exiting) return;
                if (loadError != null)
                {
                    Utilities.Logging.log.Error("Unable to load votes! Exception: " + loadError);
                    _votesReady = true;
                }
                else
                {
                    CompleteVoteLoading(loaded);
                }

                _harmony = new Harmony("com.kyle1413.BeatSaber.BeatSaverVoting");
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                _startupReady = true;
            });
        }

        [OnExit]
        public void OnEnd()
        {
            _exiting = true;
            BSEvents.lateMenuSceneLoadedFresh -= BSEvents_menuSceneLoadedFresh;
            BSEvents.gameSceneLoaded -= BSEvents_gameSceneLoaded;
            try
            {
                _voteFileTail.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Utilities.Logging.log.Error("Unable to finish vote-file work! Exception: " + exception);
            }
            foreach (var write in PendingVoteWrites.ToArray())
                FinishVoteWrite(write);

            _harmony?.UnpatchSelf();
            if (_coroutineRunner != null)
            {
                UnityEngine.Object.Destroy(_coroutineRunner.gameObject);
                _coroutineRunner = null;
            }
        }

        private static void BSEvents_gameSceneLoaded()
        {
            VotingView.lastSong = BS_Utils.Plugin.LevelData.GameplayCoreSceneSetupData?.beatmapLevel;
        }

        private static void BSEvents_menuSceneLoadedFresh(ScenesTransitionSetupData data)
        {
            if (_coroutineRunner == null)
            {
                var gameObject = new GameObject("BeatSaverVotingCoroutineRunner");
                UnityEngine.Object.DontDestroyOnLoad(gameObject);
                _coroutineRunner = gameObject.AddComponent<CoroutineRunner>();
            }

            _coroutineRunner.StartCoroutine(SetupAfterMenuSceneLoad());
        }

        private static IEnumerator SetupAfterMenuSceneLoad()
        {
            while (!_startupReady)
            {
                if (_exiting) yield break;
                yield return null;
            }
            if (_exiting) yield break;

            for (var frame = 0; frame < 120; frame++)
            {
                if (frame % 5 == 0 && VotingView.Setup())
                {
                    var tableViewController = Resources.FindObjectsOfTypeAll<LevelCollectionTableView>().FirstOrDefault();
                    if (tableViewController != null)
                    {
                        tableView = tableViewController.GetField<HMUI.TableView, LevelCollectionTableView>("_tableView");
                    }

                    yield break;
                }

                yield return null;
            }

            Utilities.Logging.log.Warn("BeatSaver voting UI did not become ready after menu scene load.");
        }

        [Init]
        public void Init(IPALogger pluginLogger)
        {
            Utilities.Logging.log = pluginLogger;
        }

        public static void WriteVotes()
        {
            if (!_votesReady && _voteLoadTask != null)
                CompleteVoteLoading(_voteLoadTask.GetAwaiter().GetResult());

            var error = QueueVoteWrite().GetAwaiter().GetResult();
            if (error != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        internal static Task<Exception> WriteVotesAsync()
        {
            var write = QueueVoteWrite();
            PendingVoteWrites.Add(write);
            return write;
        }

        internal static bool FinishVoteWrite(Task<Exception> write)
        {
            PendingVoteWrites.Remove(write);
            var error = write.GetAwaiter().GetResult();
            if (error == null) return true;
            Utilities.Logging.log.Error("Unable to save votes! Exception: " + error);
            return false;
        }

        private static Task<Exception> QueueVoteWrite()
        {
            var request = new VoteFileRequest(VotedSongsPath, votedSongs.ToArray());
            lock (VoteFileGate)
            {
                var write = _voteFileTail.ContinueWith(request.Write, CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
                _voteFileTail = write;
                return write;
            }
        }

        private static void CompleteVoteLoading(Dictionary<string, SongVote> loaded)
        {
            if (_votesReady) return;
            if (loaded != null)
                foreach (var entry in loaded)
                    if (!votedSongs.ContainsKey(entry.Key))
                        votedSongs[entry.Key] = entry.Value;
            _votesReady = true;
        }

    }
}
