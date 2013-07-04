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

        // fields and methods in ChatUI
        private FieldInfo allowSendingChallengesField;
        private FieldInfo canOpenContextMenuField;
        private FieldInfo chatlogAreaInnerField;
        private FieldInfo chatLogStyleField;
        private FieldInfo chatRoomsField;
        private FieldInfo chatScrollField;
        private FieldInfo timeStampStyleField;
        private FieldInfo userContextMenuField;
        private MethodInfo closeUserMenuMethod;
        private MethodInfo challengeUserMethod;
        private MethodInfo profileUserMethod;
        private MethodInfo tradeUserMethod;

        private ChatUI target = null;
        // dict from room log, to another dict that maps chatline to a ChatLineInfo
        private Dictionary<ChatRooms.RoomLog, Dictionary<ChatRooms.ChatLine, ChatLineInfo>> chatLineToChatLineInfoCache = new Dictionary<ChatRooms.RoomLog, Dictionary<ChatRooms.ChatLine, ChatLineInfo>>();
        // dict from room name, to another dict that maps username to ChatUser
        private Dictionary<string, Dictionary<string, ChatRooms.ChatUser>> userNameToUserCache = new Dictionary<string, Dictionary<string, ChatRooms.ChatUser>>();
        private Regex userRegex;
        private Regex linkFinder;

        private class ChatLineInfo {
            public string userName = null;
            public string link = null;
        }

        private class ExtendedChatUser : ChatRooms.ChatUser {
            public string link = null;

            public ExtendedChatUser(ChatRooms.ChatUser u) : base() {
                if (u != null) {
                    name = u.name;
                    id = u.id;
                    adminRole = u.adminRole;
                    acceptChallenges = u.acceptChallenges;
                    acceptTrades = u.acceptTrades;
                }
            }
        }

        public ChatImprovements() {
            // match until first instance of ':' (finds the username)
            userRegex = new Regex(@"[^:]*"
                /*, RegexOptions.Compiled*/); // the version of Mono used by Scrolls version of Unity does not support compiled regexes

            // from http://daringfireball.net/2010/07/improved_regex_for_matching_urls
            // I had to remove a " in there to make it work, but it should match well enough anyway
            linkFinder = new Regex(@"(?i)\b((?:[a-z][\w-]+:(?:/{1,3}|[a-z0-9%])|www\d{0,3}[.]|[a-z0-9.\-]+[.][a-z]{2,4}/)(?:[^\s()<>]+|\(([^\s()<>]+|(\([^\s()<>]+\)))*\))+(?:\(([^\s()<>]+|(\([^\s()<>]+\)))*\)|[^\s`!()\[\]{};:'.,<>?������]))"
                /*, RegexOptions.Compiled*/); // compiled regexes are not supported in the version of Unity used by Scrolls :(

            chatRoomsField = typeof(ChatUI).GetField("chatRooms", BindingFlags.Instance | BindingFlags.NonPublic);
            timeStampStyleField = typeof(ChatUI).GetField("timeStampStyle", BindingFlags.Instance | BindingFlags.NonPublic);
            chatLogStyleField = typeof(ChatUI).GetField("chatLogStyle", BindingFlags.Instance | BindingFlags.NonPublic);
            challengeUserMethod = typeof(ChatUI).GetMethod("ChallengeUser", BindingFlags.Instance | BindingFlags.NonPublic);
            tradeUserMethod = typeof(ChatUI).GetMethod("TradeUser", BindingFlags.Instance | BindingFlags.NonPublic);
            profileUserMethod = typeof(ChatUI).GetMethod("ProfileUser", BindingFlags.Instance | BindingFlags.NonPublic);
            userContextMenuField = typeof(ChatUI).GetField("userContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            canOpenContextMenuField = typeof(ChatUI).GetField("canOpenContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            chatlogAreaInnerField = typeof(ChatUI).GetField("chatlogAreaInner", BindingFlags.Instance | BindingFlags.NonPublic); ;
            chatScrollField = typeof(ChatUI).GetField("chatScrollField", BindingFlags.Instance | BindingFlags.NonPublic); ;
            allowSendingChallengesField = typeof(ChatUI).GetField("allowSendingChallenges", BindingFlags.Instance | BindingFlags.NonPublic); ;
            closeUserMenuMethod = typeof(ChatUI).GetMethod("CloseUserMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        // there must be a better way of generating the proper delegates without declaring these functions
        private void ChallengeUser(ChatRooms.ChatUser user) {
            challengeUserMethod.Invoke(target, new object[] { user });
        }
        private void TradeUser(ChatRooms.ChatUser user) {
            tradeUserMethod.Invoke(target, new object[] { user });
        }
        private void ProfileUser(ChatRooms.ChatUser user) {
            profileUserMethod.Invoke(target, new object[] { user });
        }

        private void OpenLink(ChatRooms.ChatUser user) {
            closeUserMenuMethod.Invoke(target, new object[] { });
            ExtendedChatUser eu = (ExtendedChatUser)user;
            Process.Start(eu.link);
        }

        private void CreateMenu(ChatLineInfo chatLineInfo, string roomName) {
            Dictionary<string, ChatRooms.ChatUser> userCache;
            ChatRooms.ChatUser user = null;
            // the scenario where a user is disconnected from a room but still connected to Mojang's servers seems
            // unlikely, but I want to guard against it anyway
            bool roomStillAvailable = userNameToUserCache.TryGetValue(roomName, out userCache);
            bool foundUser = roomStillAvailable && userCache.TryGetValue(chatLineInfo.userName, out user);
            bool foundLink = chatLineInfo.link != null;

            bool canOpenContextMenu = (bool)canOpenContextMenuField.GetValue(target);
            if (canOpenContextMenu && (foundUser || foundLink)) {
                Vector3 mousePosition = Input.mousePosition;
                // need 30 pixels of extra space per item added
                int extraHeightNeeded = (foundLink ? 1 : 0) * 30 + (foundUser && App.MyProfile.ProfileInfo.id != user.id && user.acceptChallenges ? 1 : 0) * 30 +
                    (foundUser && App.MyProfile.ProfileInfo.id != user.id && user.acceptTrades ? 1 : 0) * 30 + (foundUser && App.MyProfile.ProfileInfo.id != user.id ? 1 : 0) * 30;
                Rect rect = new Rect(Mathf.Min((float)(Screen.width - 105), mousePosition.x), Mathf.Min((float)(Screen.height - extraHeightNeeded - 5), (float)Screen.height - mousePosition.y), 100f, 30f);

                ContextMenu<ChatRooms.ChatUser> userContextMenu = null;
                if (foundLink) {
                    ExtendedChatUser extendedUser = new ExtendedChatUser(user);
                    extendedUser.link = chatLineInfo.link;
                    userContextMenu = new ContextMenu<ChatRooms.ChatUser>(extendedUser, rect);
                    userContextMenu.add("Open Link", new ContextMenu<ChatRooms.ChatUser>.URCMCallback(OpenLink));
                }
                if (foundUser && App.MyProfile.ProfileInfo.id != user.id) {
                    if (userContextMenu == null) {
                        userContextMenu = new ContextMenu<ChatRooms.ChatUser>(user, rect);
                    }
                    if (user.acceptChallenges) {
                        userContextMenu.add("Challenge", new ContextMenu<ChatRooms.ChatUser>.URCMCallback(ChallengeUser));
                    }
                    if (user.acceptTrades) {
                        userContextMenu.add("Trade", new ContextMenu<ChatRooms.ChatUser>.URCMCallback(TradeUser));
                    }
                    userContextMenu.add("Profile", new ContextMenu<ChatRooms.ChatUser>.URCMCallback(ProfileUser));
                }
                if (userContextMenu != null) {
                    userContextMenuField.SetValue(target, userContextMenu);
                    App.AudioScript.PlaySFX("Sounds/hyperduck/UI/ui_button_click");
                }
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
                target = (ChatUI)info.target;

                ChatRooms chatRooms = (ChatRooms) chatRoomsField.GetValue(target);
                ChatRooms.RoomLog currentRoomChatLog = chatRooms.GetCurrentRoomChatLog();
                if (currentRoomChatLog != null) {
                    GUIStyle timeStampStyle = (GUIStyle)chatRoomsField.GetValue(target);
                    GUIStyle chatLogStyle = (GUIStyle)chatLogStyleField.GetValue(target);
                    Rect chatlogAreaInner = (Rect)chatlogAreaInnerField.GetValue(target);
                    Vector2 chatScroll = (Vector2)chatScrollField.GetValue(target);
                    bool allowSendingChallenges = (bool)allowSendingChallengesField.GetValue(target);
                    ContextMenu<ChatRooms.ChatUser> userContextMenu = (ContextMenu<ChatRooms.ChatUser>) userContextMenuField.GetValue(target);

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
                            Match match = linkFinder.Match(current.text);
                            if (match.Success) {
                                chatLineInfo.link = match.Value;
                            }
                            chatLineCache.Add(current, chatLineInfo);
                        }
                        else {
                            chatLineInfo = chatLineCache[current];
                        }
                        Dictionary<string, ChatRooms.ChatUser> roomUsers;
                        // this should always be true, but it doesn't hurt to be a bit paranoid
                        bool foundRoomUsers = userNameToUserCache.TryGetValue(chatRooms.GetCurrentRoom(), out roomUsers);
                        bool senderOrLinkAvailable = chatLineInfo.userName != null || chatLineInfo.link != null;
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
