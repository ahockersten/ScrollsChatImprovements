using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

using ScrollsModLoader.Interfaces;
using UnityEngine;
using Mono.Cecil;

namespace ChatImprovements.mod {
    public class ChatImprovements : BaseMod {
        private const bool debug = false;

        private ChatUI target = null;
        private ChatRooms chatRooms;
        private GUIStyle timeStampStyle;
        private GUIStyle chatLogStyle;
        private Regex userRegex;
        private Regex linkFinder;
        // dict from room log, to another dict that maps chatline to a ChatLineInfo
        private Dictionary<ChatRooms.RoomLog, Dictionary<ChatRooms.ChatLine, ChatLineInfo>> chatLineToChatLineInfoCache = new Dictionary<ChatRooms.RoomLog, Dictionary<ChatRooms.ChatLine, ChatLineInfo>>();
        // dict from room name, to another dict that maps username to ChatUser
        private Dictionary<string, Dictionary<string, ChatRooms.ChatUser>> userNameToUserCache = new Dictionary<string, Dictionary<string, ChatRooms.ChatUser>>();

        private class ChatLineInfo {
            public string userName = null;
            public List<string> links = new List<string>();
        }

        public ChatImprovements() {
            // match until first instance of ':' (finds the username)
            userRegex = new Regex(@"[^:]*"
                /*, RegexOptions.Compiled*/); // the version of Mono used by Scrolls version of Unity does not support compiled regexes

            // from http://daringfireball.net/2010/07/improved_regex_for_matching_urls
            // I had to remove a " in there to make it work, but it should match well enough anyway
            linkFinder = new Regex(@"(?i)\b((?:[a-z][\w-]+:(?:/{1,3}|[a-z0-9%])|www\d{0,3}[.]|[a-z0-9.\-]+[.][a-z]{2,4}/)(?:[^\s()<>]+|\(([^\s()<>]+|(\([^\s()<>]+\)))*\))+(?:\(([^\s()<>]+|(\([^\s()<>]+\)))*\)|[^\s`!()\[\]{};:'.,<>?«»“”‘’]))"
                /*, RegexOptions.Compiled*/); // compiled regexes are not supported in the version of Unity used by Scrolls :(
        }

        // there must be a better way of generating the proper delegates without declaring these functions
        private void ChallengeUser(ChatRooms.ChatUser user) {
            typeof(ChatUI).GetMethod("ChallengeUser", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, new object[] { user });
        }
        private void TradeUser(ChatRooms.ChatUser user) {
            typeof(ChatUI).GetMethod("TradeUser", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, new object[] { user });
        }
        private void ProfileUser(ChatRooms.ChatUser user) {
            typeof(ChatUI).GetMethod("ProfileUser", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, new object[] { user });
        }

        private void OpenLink(ChatRooms.ChatUser fakeUser) {
            MethodInfo closeUserMenu = typeof(ChatUI).GetMethod("CloseUserMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            closeUserMenu.Invoke(target, new object[] { });
            Process.Start(fakeUser.name);
        }

        private void CreateMenu(ChatLineInfo chatLineInfo, string roomName) {
            Dictionary<string, ChatRooms.ChatUser> userCache;
            ChatRooms.ChatUser user = null;
            // the scenario where a user is disconnected from a room but still connected to Mojang's servers seems
            // unlikely, but I want to guard against it anyway
            bool roomStillAvailable = userNameToUserCache.TryGetValue(roomName, out userCache);
            bool foundUser = roomStillAvailable && userCache.TryGetValue(chatLineInfo.userName, out user);
            bool foundLinks = chatLineInfo.links.Count > 0;

            bool canOpenContextMenu = (bool)typeof(ChatUI).GetField("canOpenContextMenu", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
            FieldInfo userContextMenuField = typeof(ChatUI).GetField("userContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            if (canOpenContextMenu && (foundUser || foundLinks)) {
                Vector3 mousePosition = Input.mousePosition;
                Rect rect = new Rect(Mathf.Min((float)(Screen.width - 105), mousePosition.x), Mathf.Min((float)(Screen.height - 90 - 5), (float)Screen.height - mousePosition.y), 100f, 30f);

                ContextMenu<ChatRooms.ChatUser> userContextMenu = new ContextMenu<ChatRooms.ChatUser>(user, rect);
                if (foundLinks) {
                    foreach (string link in chatLineInfo.links) {
                        ChatRooms.ChatUser fakeUser = new ChatRooms.ChatUser();
                        fakeUser.name = link;
                        userContextMenu.add("Open " + link, new ContextMenu<ChatRooms.ChatUser>.URCMCallback(OpenLink));
                    }
                }
                if (foundUser && App.MyProfile.ProfileInfo.id != user.id) {
                    if (user.acceptChallenges) {
                        userContextMenu.add("Challenge", new ContextMenu<ChatRooms.ChatUser>.URCMCallback(ChallengeUser));
                    }
                    if (user.acceptTrades) {
                        userContextMenu.add("Trade", new ContextMenu<ChatRooms.ChatUser>.URCMCallback(TradeUser));
                    }
                    userContextMenu.add("Profile", new ContextMenu<ChatRooms.ChatUser>.URCMCallback(ProfileUser));
                }
                userContextMenuField.SetValue(target, userContextMenu);

                App.AudioScript.PlaySFX("Sounds/hyperduck/UI/ui_button_click");
            }
        }

        public static string GetName() {
            return "ChatImprovements";
        }

        public static int GetVersion() {
            return 1;
        }

        public static MethodDefinition[] GetHooks(TypeDefinitionCollection scrollsTypes, int version) {
            try {
                return new MethodDefinition[] {
                    scrollsTypes["ChatRooms"].Methods.GetMethod("LeaveRoom", new Type[]{typeof(string)}),
                    scrollsTypes["ChatRooms"].Methods.GetMethod("SetRoomInfo", new Type[] {typeof(RoomInfoMessage)}),
                    scrollsTypes["ChatUI"].Methods.GetMethod("OnGUI")[0]
                };
            }
            catch {
                return new MethodDefinition[] { };
            }
        }

        public override bool BeforeInvoke(InvocationInfo info, out object returnValue) {
            if (info.target is ChatRooms && info.targetMethod.Equals("LeaveRoom")) {
                string room = (string)info.arguments[0];
                if (userNameToUserCache.ContainsKey(room)) {
                    userNameToUserCache.Remove(room);
                }
            }

            returnValue = null;
            return false;
        }

        public override void AfterInvoke(InvocationInfo info, ref object returnValue) {
            if (info.target is ChatRooms && info.targetMethod.Equals("SetRoomInfo")) {
                RoomInfoMessage roomInfo = (RoomInfoMessage) info.arguments[0];
                if (!userNameToUserCache.ContainsKey(roomInfo.roomName)) {
                    userNameToUserCache.Add(roomInfo.roomName, new Dictionary<string, ChatRooms.ChatUser>());
                }
                Dictionary<string, ChatRooms.ChatUser> userCache = userNameToUserCache[roomInfo.roomName];
                userCache.Clear();

                RoomInfoMessage.RoomInfoProfile[] profiles = roomInfo.profiles;
                for (int i = 0; i < profiles.Length; i++) {
                    RoomInfoMessage.RoomInfoProfile p = profiles[i];
                    ChatRooms.ChatUser user = ChatRooms.ChatUser.fromRoomInfoProfile(p);
                    userCache.Add(user.name, user);
                }
            }
            else if (info.target is ChatUI && info.targetMethod.Equals("OnGUI")) {
                if (target != (ChatUI)info.target) {
                    chatRooms = (ChatRooms)typeof(ChatUI).GetField("chatRooms", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(info.target);
                    timeStampStyle = (GUIStyle)typeof(ChatUI).GetField("timeStampStyle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(info.target);
                    chatLogStyle = (GUIStyle)typeof(ChatUI).GetField("chatLogStyle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(info.target);
                    target = (ChatUI)info.target;
                }
                ChatRooms.RoomLog currentRoomChatLog = chatRooms.GetCurrentRoomChatLog();
                if (currentRoomChatLog != null) {
                    // these need to be refetched on every run, because otherwise old values will be used
                    Rect chatlogAreaInner = (Rect)typeof(ChatUI).GetField("chatlogAreaInner", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(info.target);
                    Vector2 chatScroll = (Vector2)typeof(ChatUI).GetField("chatScroll", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(info.target);
                    bool allowSendingChallenges = (bool)typeof(ChatUI).GetField("allowSendingChallenges", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(info.target);
                    ContextMenu<ChatRooms.ChatUser> userContextMenu = (ContextMenu<ChatRooms.ChatUser>)typeof(ChatUI).GetField("userContextMenu", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(info.target);

                    // set invisible draw color. We want the layout effects of drawing stuff, but we let the
                    // original code do all of the actual drawing
                    Color oldColor = GUI.color;
                    // disable warning that one of these expressions is unreachable (due to debug being const)
                    #pragma warning disable 0429
                    GUI.color = debug ? Color.red : Color.clear;
                    #pragma warning restore 0429

                    GUILayout.BeginArea(chatlogAreaInner);
                    GUILayout.BeginScrollView(chatScroll, new GUILayoutOption[] { GUILayout.Width(chatlogAreaInner.width), GUILayout.Height(chatlogAreaInner.height)});
                    foreach (ChatRooms.ChatLine current in currentRoomChatLog.getLines()) {
                        GUILayout.BeginHorizontal(new GUILayoutOption[0]);
                        GUILayout.Label(current.timestamp, timeStampStyle, new GUILayoutOption[] {
                            GUILayout.Width(20f + (float)Screen.height * 0.042f)});

                        if (!chatLineToChatLineInfoCache.ContainsKey(currentRoomChatLog)) {
                            chatLineToChatLineInfoCache.Add(currentRoomChatLog, new Dictionary<ChatRooms.ChatLine, ChatLineInfo>());
                        }
                        Dictionary<ChatRooms.ChatLine, ChatLineInfo> chatLineCache = chatLineToChatLineInfoCache[currentRoomChatLog];
                        ChatLineInfo chatLineInfo = null;
                        if (!chatLineCache.ContainsKey(current)) {
                            chatLineInfo = new ChatLineInfo();
                            Match userMatch = userRegex.Match(current.text);
                            if (userMatch.Success) {
                                // strip HTML from user name (usually a color). Yes. I know. Regexes should not be used on 
                                // XML, but here it should not pose a problem
                                String strippedMatch = Regex.Replace(userMatch.Value, @"<[^>]*>", String.Empty);
                                chatLineInfo.userName = strippedMatch;
                            }
                            MatchCollection linksFound = linkFinder.Matches(current.text);
                            foreach (Match m in linksFound) {
                                chatLineInfo.links.Add(m.Value);
                            }
                            chatLineCache.Add(current, chatLineInfo);
                        }
                        else {
                            chatLineInfo = chatLineCache[current];
                        }
                        Dictionary<string, ChatRooms.ChatUser> roomUsers;
                        // this should always be true, but it doesn't hurt to be a bit paranoid
                        bool foundRoomUsers = userNameToUserCache.TryGetValue(chatRooms.GetCurrentRoom(), out roomUsers);
                        bool senderOrLinkAvailable = chatLineInfo.userName != null || chatLineInfo.links.Count > 0;
                        if (senderOrLinkAvailable) {
                            if (GUILayout.Button(current.text, chatLogStyle, new GUILayoutOption[] { GUILayout.Width(chatlogAreaInner.width - (float)Screen.height * 0.1f - 20f) }) &&
                                allowSendingChallenges && userContextMenu == null) {
                                CreateMenu(chatLineInfo, chatRooms.GetCurrentRoom());
                            }
                        }
                        // do the drawing found in the original code to make sure we don't fall out of sync
                        else {
                            GUILayout.Label(current.text, chatLogStyle, new GUILayoutOption[] {
				                        GUILayout.Width(chatlogAreaInner.width - (float)Screen.height * 0.1f - 20f)});
                        }
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndScrollView();
                    GUILayout.EndArea();
                    // restore old color. Should not be necessary, but it does not hurt to be paranoid
                    GUI.color = oldColor;
                }
            }
            return;
        }
    }
}
