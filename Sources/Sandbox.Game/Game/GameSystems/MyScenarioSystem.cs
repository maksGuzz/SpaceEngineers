﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Medieval.Entities;
using Medieval.ObjectBuilders;
using Sandbox.Common;
using Sandbox.Common.ObjectBuilders.Voxels;
using Sandbox.Engine.Networking;
using Sandbox.Engine.Utils;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.Graphics;
using VRage.Input;
using VRage.Library.Utils;
using VRage.Voxels;
using VRageMath;
using VRageRender;
using VRage.FileSystem;
using Sandbox.Graphics.GUI;
using Sandbox.Game.Entities.Character;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Multiplayer;
using Sandbox.ModAPI;
using Sandbox.Engine.Multiplayer;
using SteamSDK;
using ProtoBuf;
using Sandbox.Game.Gui;
using VRage.Utils;
using Sandbox.Definitions;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Engine.Physics;
using Sandbox.Common.ModAPI;
using Sandbox.Game.GUI;
using Sandbox.Game.Screens;
using Sandbox.Game.Localization;
using VRage;

namespace Sandbox.Game.GameSystems
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation, 1000)]
    public class MyScenarioSystem : MySessionComponentBase
    {
        public static int LoadTimeout = 120;
        public static MyScenarioSystem Static;

        private readonly HashSet<ulong> m_playersReadyForBattle = new HashSet<ulong>();

        private TimeSpan m_startBattlePreparationOnClients = TimeSpan.FromSeconds(0);

        //#### for server and clients
        public enum MyState
        {
            Loaded,
            JoinScreen,
            WaitingForClients,
            Running,
        }

        private MyState m_gameState = MyState.Loaded;
        public MyState GameState { get { return m_gameState; } }

        // Time when battle was started (server or client local time).
        private TimeSpan m_startBattleTime = TimeSpan.FromSeconds(0);

        private StringBuilder m_tmpStringBuilder = new StringBuilder();

        private MyGuiScreenScenarioWaitForPlayers m_waitingScreen;

        // Absolute server time when server starts sending preparation requests to clients.
        public DateTime ServerPreparationStartTime { get; private set; }
        // Absolute server time when server starts battle game.
        public DateTime ServerStartGameTime { get; private set; }//max value when not started yet

        // Cached time limit from lobby.
        private TimeSpan? m_battleTimeLimit;

        private bool OnlinePrivateMode { get { return MySession.Static.OnlineMode == MyOnlineModeEnum.PRIVATE; } }


        public MyScenarioSystem()
        {
            Static = this;
            ServerStartGameTime = DateTime.MaxValue;
        }

        void MySyncScenario_ClientWorldLoaded()
        {
            MySyncScenario.ClientWorldLoaded -= MySyncScenario_ClientWorldLoaded;

            m_waitingScreen = new MyGuiScreenScenarioWaitForPlayers();
            MyGuiSandbox.AddScreen(m_waitingScreen);
        }

        void MySyncScenario_StartScenario(long serverStartGameTime)
        {
            Debug.Assert(!Sync.IsServer);

            ServerStartGameTime = new DateTime(serverStartGameTime);

            StartScenario();
        }

        void MySyncScenario_PlayerReadyToStart(ulong steamId)
        {
            Debug.Assert(Sync.IsServer);

            if (m_gameState == MyState.WaitingForClients)
            {
                m_playersReadyForBattle.Add(steamId);

                if (AllPlayersReadyForBattle())
                {
                    StartScenario();

                    foreach (var playerId in m_playersReadyForBattle)
                    {
                        if (playerId != MySteam.UserId)
                            MySyncScenario.StartScenarioRequest(playerId, ServerStartGameTime.Ticks);
                    }
                }
            }
            else if (m_gameState == MyState.Running)
            {
                MySyncScenario.StartScenarioRequest(steamId, ServerStartGameTime.Ticks);
            }
        }

        private bool AllPlayersReadyForBattle()
        {
            foreach (var player in Sync.Players.GetAllPlayers())
            {
                if (!m_playersReadyForBattle.Contains(player.SteamId))
                    return false;
            }
            return true;
        }
        int m_bootUpCount = 0;
        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();

            if (!MySession.Static.IsScenario)
                return;

            if (!Sync.IsServer)
                return;

            if (MySession.Static.OnlineMode == MyOnlineModeEnum.OFFLINE)//!Sync.MultiplayerActive)
            {
                if (m_gameState == MyState.Loaded)
                {
                    m_gameState = MyState.Running;
                    ServerStartGameTime = DateTime.UtcNow;
                }
                return;
            }

            switch (m_gameState)
            {
                case MyState.Loaded:
                    if (MySession.Static.OnlineMode != MyOnlineModeEnum.OFFLINE && MyMultiplayer.Static == null)
                    {
                        m_bootUpCount++;
                        if (m_bootUpCount > 100)//because MyMultiplayer.Static is initialized later than this part of game
                        {
                            //network start failure - trying to save what we can :-)
                            MyPlayerCollection.RequestLocalRespawn();
                            m_gameState = MyState.Running;
                            return;
                        }
                    }
                    if (MySandboxGame.IsDedicated)
                    {
                        ServerPreparationStartTime = DateTime.UtcNow;
                        MyMultiplayer.Static.ScenarioStartTime = ServerPreparationStartTime;
                        m_gameState = MyState.Running;
                        return;
                    }
                    if (MySession.Static.OnlineMode == MyOnlineModeEnum.OFFLINE || MyMultiplayer.Static != null)
                    {
                        if (MyMultiplayer.Static != null)
                        {
                            MyMultiplayer.Static.Scenario = true;
                            MyMultiplayer.Static.ScenarioBriefing = MySession.Static.GetWorld().Checkpoint.Briefing;
                        }
                        MyGuiScreenScenarioMpServer guiscreen = new MyGuiScreenScenarioMpServer();
                        guiscreen.Briefing = MySession.Static.GetWorld().Checkpoint.Briefing;
                        MyGuiSandbox.AddScreen(guiscreen);
                        m_playersReadyForBattle.Add(MySteam.UserId);
                        m_gameState = MyState.JoinScreen;
                    }
                    break;
                case MyState.JoinScreen:
                    break;
                case MyState.WaitingForClients:
                    // Check timeout
                    TimeSpan currenTime = MySession.Static.ElapsedPlayTime;
                    if (AllPlayersReadyForBattle() || (LoadTimeout>0 && currenTime - m_startBattlePreparationOnClients > TimeSpan.FromSeconds(LoadTimeout)))
                    {
                        StartScenario();
                        foreach (var playerId in m_playersReadyForBattle)
                        {
                            if (playerId != MySteam.UserId)
                                MySyncScenario.StartScenarioRequest(playerId, ServerStartGameTime.Ticks);
                        }
                    }
                    break;
                case MyState.Running:
                    break;
            }
        }

        public override void LoadData()
        {
            base.LoadData();

            MySyncScenario.PlayerReadyToStartScenario += MySyncScenario_PlayerReadyToStart;
            MySyncScenario.StartScenario += MySyncScenario_StartScenario;
            MySyncScenario.ClientWorldLoaded += MySyncScenario_ClientWorldLoaded;
            MySyncScenario.PrepareScenario += MySyncBattleGame_PrepareScenario;
        }

        void MySyncBattleGame_PrepareScenario(long preparationStartTime)
        {
            Debug.Assert(!Sync.IsServer);

            ServerPreparationStartTime = new DateTime(preparationStartTime);
        }

        protected override void UnloadData()
        {
            base.UnloadData();

            MySyncScenario.PlayerReadyToStartScenario -= MySyncScenario_PlayerReadyToStart;
            MySyncScenario.StartScenario -= MySyncScenario_StartScenario;
            MySyncScenario.ClientWorldLoaded -= MySyncScenario_ClientWorldLoaded;
            MySyncScenario.PrepareScenario -= MySyncBattleGame_PrepareScenario;

            /*if (Sync.IsServer && MySession.Static.Battle)
            {
                Sync.Players.PlayerCharacterDied -= Players_PlayerCharacterDied;
                MySession.Static.Factions.FactionCreated -= Factions_FactionCreated;
            }*/
        }

        internal void PrepareForStart()
        {
            Debug.Assert(Sync.IsServer);

            m_gameState = MyState.WaitingForClients;
            m_startBattlePreparationOnClients = MySession.Static.ElapsedPlayTime;

            var onlineMode = GetOnlineModeFromCurrentLobbyType();
            if (onlineMode != MyOnlineModeEnum.OFFLINE)
            {
                m_waitingScreen = new MyGuiScreenScenarioWaitForPlayers();
                MyGuiSandbox.AddScreen(m_waitingScreen);

                ServerPreparationStartTime = DateTime.UtcNow;
                MyMultiplayer.Static.ScenarioStartTime = ServerPreparationStartTime;
                MySyncScenario.PrepareScenarioFromLobby(ServerPreparationStartTime.Ticks);
            }
            else
            {
                StartScenario();
            }
        }

        private void StartScenario()
        {
            if (Sync.IsServer)
            {
                ServerStartGameTime = DateTime.UtcNow;
            }
            if (m_waitingScreen != null)
            {
                MyGuiSandbox.RemoveScreen(m_waitingScreen);
                m_waitingScreen = null;
            }
            m_gameState = MyState.Running;
            m_startBattleTime = MySession.Static.ElapsedPlayTime;
            MyPlayerCollection.RequestLocalRespawn();
        }

        internal static MyOnlineModeEnum GetOnlineModeFromCurrentLobbyType()
        {
            MyMultiplayerLobby lobby = MyMultiplayer.Static as MyMultiplayerLobby;
            if (lobby == null)
            {
                Debug.Fail("Multiplayer lobby not found");
                return MyOnlineModeEnum.PRIVATE;
            }

            switch (lobby.GetLobbyType())
            {
                case LobbyTypeEnum.Private:
                    return MyOnlineModeEnum.PRIVATE;
                case LobbyTypeEnum.FriendsOnly:
                    return MyOnlineModeEnum.FRIENDS;
                case LobbyTypeEnum.Public:
                    return MyOnlineModeEnum.PUBLIC;
            }

            return MyOnlineModeEnum.PRIVATE;
        }

        internal static void SetLobbyTypeFromOnlineMode(MyOnlineModeEnum onlineMode)
        {
            MyMultiplayerLobby lobby = MyMultiplayer.Static as MyMultiplayerLobby;
            if (lobby == null)
            {
                Debug.Fail("Multiplayer lobby not found");
                return;
            }

            LobbyTypeEnum lobbyType = LobbyTypeEnum.Private;

            switch (onlineMode)
            {
                case MyOnlineModeEnum.FRIENDS:
                    lobbyType = LobbyTypeEnum.FriendsOnly;
                    break;
                case MyOnlineModeEnum.PUBLIC:
                    lobbyType = LobbyTypeEnum.Public;
                    break;
            }

            lobby.SetLobbyType(lobbyType);
        }

        //loads next mission, SP only
        //id can be workshop ID or save name (in that case official scenarios are searched first, if not found, then user's saves)
        public void LoadNextScenario(string id)
        {
            ulong workshopID;
            if(ulong.TryParse(id,out workshopID))
            {
                MySteamWorkshop.SubscribedItem item = new MySteamWorkshop.SubscribedItem();
                item.PublishedFileId = workshopID;

                MySteamWorkshop.CreateWorldInstanceAsync(item, MySteamWorkshop.MyWorkshopPathInfo.CreateScenarioInfo(), true, delegate(bool success, string sessionPath)
                {
                    if (success)
                        LoadMission(sessionPath, false, MyOnlineModeEnum.OFFLINE, 1);
                    else
                        MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
                                    messageText: MyTexts.Get(MySpaceTexts.MessageBoxTextWorkshopDownloadFailed),
                                    messageCaption: MyTexts.Get(MySpaceTexts.ScreenCaptionWorkshop)));
                });
            }
            //else
            //    LoadMission(save.Item1, false, MyOnlineModeEnum.OFFLINE, 1);



        }

        public static void LoadMission(string sessionPath, bool multiplayer, MyOnlineModeEnum onlineMode, short maxPlayers)
        {
            MyLog.Default.WriteLine("LoadSession() - Start");
            MyLog.Default.WriteLine(sessionPath);

            ulong checkpointSizeInBytes;
            var checkpoint = MyLocalCache.LoadCheckpoint(sessionPath, out checkpointSizeInBytes);

            checkpoint.Settings.OnlineMode = onlineMode;
            checkpoint.Settings.MaxPlayers = maxPlayers;
            checkpoint.Settings.Scenario = true;
            checkpoint.Settings.GameMode = MyGameModeEnum.Survival;
            checkpoint.Settings.ScenarioEditMode = false;

            if (!MySession.IsCompatibleVersion(checkpoint))
            {
                MyLog.Default.WriteLine(MyTexts.Get(MySpaceTexts.DialogTextIncompatibleWorldVersion).ToString());
                MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
                    messageCaption: MyTexts.Get(MySpaceTexts.MessageBoxCaptionError),
                    messageText: MyTexts.Get(MySpaceTexts.DialogTextIncompatibleWorldVersion),
                    buttonType: MyMessageBoxButtonsType.OK));
                MyLog.Default.WriteLine("LoadSession() - End");
                return;
            }

            if (checkpoint.BriefingVideo!=null && checkpoint.BriefingVideo.Length > 0)
                MyGuiSandbox.OpenUrlWithFallback(checkpoint.BriefingVideo, "Scenario briefing video");

            if (!MySteamWorkshop.CheckLocalModsAllowed(checkpoint.Mods, checkpoint.Settings.OnlineMode == MyOnlineModeEnum.OFFLINE))
            {
                MyLog.Default.WriteLine(MyTexts.Get(MySpaceTexts.DialogTextLocalModsDisabledInMultiplayer).ToString());
                MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
                    messageCaption: MyTexts.Get(MySpaceTexts.MessageBoxCaptionError),
                    messageText: MyTexts.Get(MySpaceTexts.DialogTextLocalModsDisabledInMultiplayer),
                    buttonType: MyMessageBoxButtonsType.OK));
                MyLog.Default.WriteLine("LoadSession() - End");
                return;
            }


            MySteamWorkshop.DownloadModsAsync(checkpoint.Mods, delegate(bool success)
            {
                if (success || (checkpoint.Settings.OnlineMode == MyOnlineModeEnum.OFFLINE) && MySteamWorkshop.CanRunOffline(checkpoint.Mods))
                {
                    //Sandbox.Audio.MyAudio.Static.Mute = true;

                    MyScreenManager.CloseAllScreensNowExcept(null);
                    MyGuiSandbox.Update(MyEngineConstants.UPDATE_STEP_SIZE_IN_MILLISECONDS);

                    // May be called from gameplay, so we must make sure we unload the current game
                    if (MySession.Static != null)
                    {
                        MySession.Static.Unload();
                        MySession.Static = null;
                    }

                    //seed 0 has special meaning - please randomize at mission start. New seed will be saved and game will run with it ever since.
                    //  if you use this, YOU CANNOT HAVE ANY PROCEDURAL ASTEROIDS ALREADY SAVED
                    if (checkpoint.Settings.ProceduralSeed == 0)
                        checkpoint.Settings.ProceduralSeed = MyRandom.Instance.Next();

                    MyGuiScreenGamePlay.StartLoading(delegate
                    {
                        checkpoint.Settings.Scenario = true;
                        MySession.LoadMission(sessionPath, checkpoint, checkpointSizeInBytes);
                    });
                }
                else
                {
                    MyLog.Default.WriteLine(MyTexts.Get(MySpaceTexts.DialogTextDownloadModsFailed).ToString());
                    MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
                        messageCaption: MyTexts.Get(MySpaceTexts.MessageBoxCaptionError),
                        messageText: MyTexts.Get(MySpaceTexts.DialogTextDownloadModsFailed),
                        buttonType: MyMessageBoxButtonsType.OK, callback: delegate(MyGuiScreenMessageBox.ResultEnum result)
                        {
                            if (MyFakes.QUICK_LAUNCH != null)
                                MyGuiScreenMainMenu.ReturnToMainMenu();
                        }));
                }
                MyLog.Default.WriteLine("LoadSession() - End");
            });

        }


    }
}
