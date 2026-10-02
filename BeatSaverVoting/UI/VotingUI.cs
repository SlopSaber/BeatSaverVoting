using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using System.Linq;
using System.Collections;
using System.Reflection;
using UnityEngine;
using TMPro;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.ComponentModel;
using Component = UnityEngine.Component;
using System.Runtime.CompilerServices;
using UnityEngine.Networking;
using BeatSaverVoting.Utilities;
using HMUI;
using IPA.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OculusStudios.Platform.Core;
using UnityEngine.UI;
using UnityEngine.XR;

namespace BeatSaverVoting.UI
{
    public class VotingUI : INotifyPropertyChanged
    {

        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        [Serializable]
        private struct Auth
        {
            public string steamId;
            public string oculusId;
            public string proof;
        }

        private struct Payload
        {
            public Auth auth;
            public bool direction;
            public string hash;
        }

        private sealed class PreparationResult<T>
        {
            internal T Value;
            internal Exception Error;
        }

        private static PreparationResult<Song> ParseSong(object response)
        {
            try
            {
                var json = JObject.Parse((string)response);
                return new PreparationResult<Song> { Value = json.Children().Any() ? new Song(json) : null };
            }
            catch (Exception exception)
            {
                return new PreparationResult<Song> { Error = exception };
            }
        }

        private static PreparationResult<string> SerializePayload(object payload)
        {
            try
            {
                return new PreparationResult<string> { Value = JsonConvert.SerializeObject((Payload)payload) };
            }
            catch (Exception exception)
            {
                return new PreparationResult<string> { Error = exception };
            }
        }

        internal BeatmapLevel lastSong;
        private Song _lastBeatSaverSong;
        private IPlatform _userModel;
        private readonly string _userAgent = $"BeatSaverVoting/{Assembly.GetExecutingAssembly().GetName().Version}";
        [UIComponent("voteTitle")]
        public TextMeshProUGUI voteTitle;
        [UIComponent("voteText")]
        public TextMeshProUGUI voteText;
        [UIComponent("upButton")]
        public PageButton upButton;
        [UIComponent("downButton")]
        public PageButton downButton;

        private bool _upInteractable = true;
        [UIValue("UpInteractable")]
        public bool UpInteractable
        {
            get => _upInteractable;
            set
            {
                _upInteractable = value;
                NotifyPropertyChanged();
            }
        }
        private bool _downInteractable = true;
        private bool _isSetup;
        private int _ratingRevision;
        [UIValue("DownInteractable")]
        public bool DownInteractable
        {
            get => _downInteractable;
            set
            {
                _downInteractable = value;
                NotifyPropertyChanged();
            }
        }

        internal bool Setup()
        {
            if (_isSetup) return true;

            var resultsView = Resources.FindObjectsOfTypeAll<ResultsViewController>().FirstOrDefault();

            if (!resultsView) return false;
            
            var platformLeaderboardsModel = Resources.FindObjectsOfTypeAll<PlatformLeaderboardsModel>().FirstOrDefault();
            
            if (!platformLeaderboardsModel) return false;

            _userModel = platformLeaderboardsModel._platform;

            try
            {
                BSMLParser.Instance.Parse(BeatSaberMarkupLanguage.Utilities.GetResourceContent(Assembly.GetExecutingAssembly(), "BeatSaverVoting.UI.votingUI.bsml"), resultsView.gameObject, this);
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("BSMLParser has not initialized"))
            {
                return false;
            }

            resultsView.didActivateEvent += ResultsView_didActivateEvent;
            SetColors();
            _isSetup = true;
            return true;
        }

        private static AnimationClip GenerateButtonAnimation(float r, float g, float b, float a, float x, float y) =>
            GenerateButtonAnimation(
                AnimationCurve.Constant(0, 1, r),
                AnimationCurve.Constant(0, 1, g),
                AnimationCurve.Constant(0, 1, b),
                AnimationCurve.Constant(0, 1, a),
                AnimationCurve.Constant(0, 1, x),
                AnimationCurve.Constant(0, 1, y)
            );

        private static AnimationClip GenerateButtonAnimation(AnimationCurve r, AnimationCurve g, AnimationCurve b, AnimationCurve a, AnimationCurve x, AnimationCurve y)
        {
            var animation = new AnimationClip { legacy = true };

            animation.SetCurve("Icon", typeof(Transform), "localScale.x", x);
            animation.SetCurve("Icon", typeof(Transform), "localScale.y", y);
            animation.SetCurve("Icon", typeof(Graphic), "m_Color.r", r);
            animation.SetCurve("Icon", typeof(Graphic), "m_Color.g", g);
            animation.SetCurve("Icon", typeof(Graphic), "m_Color.b", b);
            animation.SetCurve("Icon", typeof(Graphic), "m_Color.a", a);

            return animation;
        }

        private static void SetupButtonAnimation(Component t, Color c)
        {
            var anim = t.GetComponent<ButtonStaticAnimations>();

            anim.SetField("_normalClip", GenerateButtonAnimation(c.r, c.g, c.b, 0.502f, 1, 1));
            anim.SetField("_highlightedClip", GenerateButtonAnimation(c.r, c.g, c.b, 1, 1.5f, 1.5f));
        }

        private void SetColors()
        {
            var upArrow = upButton.GetComponentInChildren<ImageView>();
            var downArrow = downButton.GetComponentInChildren<ImageView>();

            if (upArrow == null || downArrow == null) return;

            SetupButtonAnimation(upButton, new Color(0.341f, 0.839f, 0.341f));
            SetupButtonAnimation(downButton, new Color(0.984f, 0.282f, 0.305f));
        }

        private void ResultsView_didActivateEvent(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            GetVotesForMap();
        }

        [UIAction("up-pressed")]
        private void UpvoteButtonPressed()
        {
            VoteForSong(_lastBeatSaverSong, true, UpdateUIAfterVote);
        }
        [UIAction("down-pressed")]
        private void DownvoteButtonPressed()
        {
            VoteForSong(_lastBeatSaverSong, false, UpdateUIAfterVote);
        }

        private void GetVotesForMap()
        {
            var revision = ++_ratingRevision;
            var isCustomLevel = lastSong.levelID.StartsWith("custom_level_");
            _lastBeatSaverSong = null;
            UpInteractable = false;
            DownInteractable = false;
            downButton.gameObject.SetActive(isCustomLevel);
            upButton.gameObject.SetActive(isCustomLevel);
            voteTitle.gameObject.SetActive(isCustomLevel);
            voteText.text = isCustomLevel ? "Loading..." : "";

            if (isCustomLevel)
            {
                voteTitle.StartCoroutine(GetRatingForSong(lastSong, revision));
            }
        }

        private IEnumerator GetSongInfo(string hash)
        {
            using var www = UnityWebRequest.Get($"{Plugin.BeatsaverURL}/maps/hash/{hash.ToLowerInvariant()}");
            www.SetRequestHeader("user-agent", _userAgent);

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError)
            {
                Logging.log.Error($"Unable to connect to {Plugin.BeatsaverURL}! " +
                                  (www.result == UnityWebRequest.Result.ConnectionError ? $"Network error: {www.error}" :
                                      (www.result == UnityWebRequest.Result.ProtocolError ? $"HTTP error: {www.error}" : "Unknown error")));
            }
            else
            {
                var parse = Task.Factory.StartNew(ParseSong, www.downloadHandler.text, CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
                while (!parse.IsCompleted)
                    yield return null;

                var result = parse.Result;
                if (result.Error != null)
                    Logging.log.Critical("Unable to get song rating! Excpetion: " + result.Error);
                else if (result.Value == null)
                    Logging.log.Error("Song doesn't exist on BeatSaver!");

                yield return result.Value;
            }
        }

        private IEnumerator GetRatingForSong(BeatmapLevel level, int revision)
        {
            if (!level.levelID.StartsWith("custom_level_")) yield break;

            var cd = new CoroutineWithData(voteTitle, GetSongInfo(SongCore.Collections.GetCustomLevelHash(level.levelID)));
            yield return cd.Coroutine;

            try
            {
                if (!(cd.result is Song song) || lastSong != level || revision != _ratingRevision) yield break;

                _lastBeatSaverSong = song;

                voteText.text = GetScoreFromVotes(_lastBeatSaverSong.upVotes, _lastBeatSaverSong.downVotes);

                var canVote = XRSettings.loadedDeviceName.IndexOf("oculus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              XRSettings.loadedDeviceName.IndexOf("openxr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              Environment.CommandLine.ToLower().Contains("-vrmode oculus") || Environment.CommandLine.ToLower().Contains("fpfc");

                UpInteractable = canVote;
                DownInteractable = canVote;

                if (!lastSong.levelID.StartsWith("custom_level_")) yield break;
                var lastLevelHash = SongCore.Collections.GetCustomLevelHash(lastSong.levelID).ToLower();

                if (!Plugin.votedSongs.TryGetValue(lastLevelHash, out var voteInfo)) yield break;

                if (voteInfo.voteType == Plugin.VoteType.Upvote)
                {
                    UpInteractable = false;
                }
                else if (voteInfo.voteType == Plugin.VoteType.Downvote)
                {
                    DownInteractable = false;
                }
            }
            catch (Exception e)
            {
                Logging.log.Critical("Unable to get song rating! Excpetion: " + e);
            }
        }

        internal void VoteForSong(string hash, bool upvote, VoteCallback callback)
        {
            voteTitle.StartCoroutine(VoteForSongAsync(hash, upvote, callback));
        }

        private IEnumerator VoteForSongAsync(string hash, bool upvote, VoteCallback callback)
        {
            var cd = new CoroutineWithData(voteTitle, GetSongInfo(hash));
            yield return cd.Coroutine;

            if (cd.result is Song song)
                VoteForSong(song, upvote, callback);
        }

        private void VoteForSong(Song song, bool upvote, VoteCallback callback)
        {
            if (song == null)
            {
                callback?.Invoke(null, false, false, -1);
                return;
            }

            var userTotal = Plugin.votedSongs.ContainsKey(song.hash) ? (Plugin.votedSongs[song.hash].voteType == Plugin.VoteType.Upvote ? 1 : -1) : 0;
            var oldValue = song.upVotes - song.downVotes - userTotal;
            VoteForSong(song.hash, upvote, oldValue, callback);
        }

        private void VoteForSong(string hash, bool upvote, int currentVoteCount, VoteCallback callback)
        {
            try
            {
                voteTitle.StartCoroutine(VoteWithUserInfo(hash, upvote, currentVoteCount, callback));
            }
            catch(Exception ex)
            {
                Logging.log.Warn("Failed To Vote For Song " + ex.Message);
            }

        }

        private IEnumerator VoteWithUserInfo(string hash, bool upvote, int currentVoteCount, VoteCallback callback)
        {
            UpdateView("Voting...");

            var task = _userModel.user.GetAccessTokenAsync();
            while (!task.IsCompleted)
                yield return null;
            if (task.IsFaulted || task.IsCanceled)
            {
                UpdateView("Authentication failed");
                callback?.Invoke(hash, false, false, currentVoteCount);
                yield break;
            }
            var authToken = task.Result;
            var userId = _userModel.user.userId.ToString();

            if (_userModel.vendor == Vendor.Valve)
            {
                yield return PerformVote(hash, new Payload {auth = new Auth { steamId = userId, proof = authToken }, direction = upvote, hash = hash}, currentVoteCount, callback);
            }
            else if (_userModel.vendor == Vendor.Meta)
            {
                yield return PerformVote(hash, new Payload { auth = new Auth { oculusId = userId, proof = authToken }, direction = upvote, hash = hash}, currentVoteCount, callback);
            }
        }

        private readonly Dictionary<long, string> _errorMessages = new Dictionary<long, string>
        {
            {500, "Server \nerror"},
            {401, "Invalid\nauth ticket"},
            {404, "Beatmap not\nfound"},
            {400, "Bad\nrequest"}
        };

        private IEnumerator PerformVote(string hash, Payload payload, int currentVoteCount, VoteCallback callback)
        {
            var serialize = Task.Factory.StartNew(SerializePayload, payload, CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            while (!serialize.IsCompleted)
                yield return null;
            var prepared = serialize.Result;
            if (prepared.Error != null)
            {
                Logging.log.Error("Unable to prepare vote! Exception: " + prepared.Error);
                callback?.Invoke(hash, false, false, currentVoteCount);
                yield break;
            }

            using var voteWWW = UnityWebRequest.Post($"{Plugin.BeatsaverURL}/vote", prepared.Value, "application/json");
            voteWWW.SetRequestHeader("user-agent", _userAgent);
            voteWWW.timeout = 30;
            yield return voteWWW.SendWebRequest();

            if (voteWWW.result == UnityWebRequest.Result.ConnectionError)
            {
                Logging.log.Error(voteWWW.error);
                callback?.Invoke(hash, false, false, currentVoteCount);
            }
            else if (voteWWW.responseCode < 200 || voteWWW.responseCode > 299)
            {
                var errorMessage = _errorMessages.TryGetValue(voteWWW.responseCode, out var knownError) ? knownError : "Error\n" + voteWWW.responseCode;
                UpdateView(errorMessage, !_errorMessages.ContainsKey(voteWWW.responseCode));

                Logging.log.Error("Error: " + voteWWW.downloadHandler.text);
                callback?.Invoke(hash, false, false, currentVoteCount);
            } else {
                Logging.log.Debug($"Current vote count: {currentVoteCount}, new total: {currentVoteCount + (payload.direction ? 1 : -1)}");
                callback?.Invoke(hash, true, payload.direction, currentVoteCount + (payload.direction ? 1 : -1));
            }
        }

        private void UpdateView(string text, bool up = false, bool? down = null)
        {
            UpInteractable = up;
            DownInteractable = down ?? up;
            voteText.text = text;
        }

        private static string GetScoreFromVotes(int upVotes, int downVotes)
        {
            double totalVotes = upVotes + downVotes;
            var rawScore = upVotes / totalVotes;
            var scoreWeighted = rawScore - (rawScore - 0.5) * Math.Pow(2.0, -Math.Log(totalVotes / 2 + 1, 3.0));

            return $"{scoreWeighted:0.#%} ({totalVotes})";
        }

        private void UpdateUIAfterVote(string hash, bool success, bool upvote, int newTotal) {
            if (!success) return;

            var hasPreviousVote = Plugin.votedSongs.ContainsKey(hash);

            if (_lastBeatSaverSong != null && hash == _lastBeatSaverSong.hash)
            {
                ++_ratingRevision;
                UpInteractable = !upvote;
                DownInteractable = upvote;

                if (hasPreviousVote)
                {
                    var diff = upvote ? 1 : -1;
                    _lastBeatSaverSong.upVotes += diff;
                    _lastBeatSaverSong.downVotes += -diff;
                }
                else if (upvote)
                {
                    _lastBeatSaverSong.upVotes += 1;
                }
                else
                {
                    _lastBeatSaverSong.downVotes += 1;
                }

                voteText.text = GetScoreFromVotes(_lastBeatSaverSong.upVotes, _lastBeatSaverSong.downVotes);
            }
            if (!Plugin.votedSongs.ContainsKey(hash) || Plugin.votedSongs[hash].voteType != (upvote ? Plugin.VoteType.Upvote : Plugin.VoteType.Downvote))
            {
                Plugin.votedSongs[hash] = new Plugin.SongVote(hash, upvote ? Plugin.VoteType.Upvote : Plugin.VoteType.Downvote);
                var write = Plugin.WriteVotesAsync();
                voteTitle.StartCoroutine(WriteVotesAndRefresh(write));
            }
        }

        private static IEnumerator WriteVotesAndRefresh(Task<Exception> write)
        {
            while (!write.IsCompleted)
                yield return null;
            if (Plugin.FinishVoteWrite(write) && Plugin.tableView != null)
                Plugin.tableView.RefreshCellsContent();
        }
    }
}
