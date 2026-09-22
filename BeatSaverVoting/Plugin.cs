using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BS_Utils.Utilities;
using HarmonyLib;
using IPA;
using IPA.Utilities;
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
            return votedSongs.ContainsKey(hash) ? votedSongs[hash].voteType : (VoteType?) null;
        }

        internal const string BeatsaverURL = "https://api.beatsaver.com";
        private static readonly string VotedSongsPath = $"{Environment.CurrentDirectory}/UserData/votedSongs.json";
        internal static Dictionary<string, SongVote> votedSongs = new Dictionary<string, SongVote>();

        internal static HMUI.TableView tableView;
        internal static Sprite favoriteIcon;
        internal static Sprite favoriteUpvoteIcon;
        internal static Sprite favoriteDownvoteIcon;
        internal static Sprite upvoteIcon;
        internal static Sprite downvoteIcon;

        [OnStart]
        public async Task OnApplicationStart()
        {
            BSEvents.lateMenuSceneLoadedFresh += BSEvents_menuSceneLoadedFresh;
            BSEvents.gameSceneLoaded += BSEvents_gameSceneLoaded;

            favoriteIcon = await BeatSaberMarkupLanguage.Utilities.LoadSpriteFromAssemblyAsync("BeatSaverVoting.Icons.Favorite.png");
            favoriteUpvoteIcon = await BeatSaberMarkupLanguage.Utilities.LoadSpriteFromAssemblyAsync("BeatSaverVoting.Icons.FavoriteUpvote.png");
            favoriteDownvoteIcon = await BeatSaberMarkupLanguage.Utilities.LoadSpriteFromAssemblyAsync("BeatSaverVoting.Icons.FavoriteDownvote.png");
            upvoteIcon = await BeatSaberMarkupLanguage.Utilities.LoadSpriteFromAssemblyAsync("BeatSaverVoting.Icons.Upvote.png");
            downvoteIcon = await BeatSaberMarkupLanguage.Utilities.LoadSpriteFromAssemblyAsync("BeatSaverVoting.Icons.Downvote.png");

            _harmony = new Harmony("com.kyle1413.BeatSaber.BeatSaverVoting");
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            if (!File.Exists(VotedSongsPath))
            {
                File.WriteAllText(VotedSongsPath, JsonConvert.SerializeObject(votedSongs), Encoding.UTF8);
            }
            else
            {
                votedSongs = JsonConvert.DeserializeObject<Dictionary<string, SongVote>>(File.ReadAllText(VotedSongsPath, Encoding.UTF8)) ?? votedSongs;
            }
        }

        [OnExit]
        public void OnEnd()
        {
            _harmony.UnpatchSelf();
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
            for (var frame = 0; frame < 120; frame++)
            {
                if (VotingView.Setup())
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
            File.WriteAllText(VotedSongsPath, JsonConvert.SerializeObject(votedSongs), Encoding.UTF8);
        }

    }
}
