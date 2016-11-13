﻿using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Collections.Generic;
using Lidgren.Network;
using Barotrauma.Items.Components;

namespace Barotrauma.Networking
{
    enum ClientPacketHeader
    {
        REQUEST_AUTH,   //ask the server if a password is needed, if so we'll get nonce for encryption
        REQUEST_INIT,   //ask the server to give you initialization
        UPDATE_LOBBY,   //update state in lobby
        UPDATE_INGAME,  //update state ingame

        RESPONSE_STARTGAME //tell the server whether you're ready to start
    }
    enum ClientNetObject
    {
        END_OF_MESSAGE, //self-explanatory
        SYNC_IDS,       //ids of the last changes the client knows about
        CHAT_MESSAGE,   //also self-explanatory
        VOTE,           //you get the idea
        CHARACTER_INPUT,
        ITEM_INTERACTION,
        ENTITY_STATE
    }

    enum ServerPacketHeader
    {
        AUTH_RESPONSE,      //tell the player if they require a password to log in
        AUTH_FAILURE,       //the server won't authorize player yet, however connection is still alive
        UPDATE_LOBBY,       //update state in lobby (votes and chat messages)
        UPDATE_INGAME,      //update state ingame (character input and chat messages)

        QUERY_STARTGAME,    //ask the clients whether they're ready to start
        STARTGAME           //start a new round
    }
    enum ServerNetObject
    {
        END_OF_MESSAGE,
        SYNC_IDS,
        CHAT_MESSAGE,
        VOTE,
        ENTITY_POSITION,
        ENTITY_STATE,

        ENTITY_SPAWN
    }

    enum VoteType
    {
        Unknown,
        Sub,
        Mode,
        EndRound,
        Kick
    }

    abstract class NetworkMember
    {
#if DEBUG
        public Dictionary<string, long> messageCount = new Dictionary<string, long>();
#endif

        protected NetPeer netPeer;

        protected string name;

        protected TimeSpan updateInterval;
        protected DateTime updateTimer;

        protected GUIFrame inGameHUD;
        protected GUIListBox chatBox;
        protected GUITextBox chatMsgBox;        

        public int EndVoteCount, EndVoteMax;
        //private GUITextBlock endVoteText;

        public int Port;

        protected bool gameStarted;

        protected Character myCharacter;
        protected CharacterInfo characterInfo;


        protected RespawnManager respawnManager;

        public Voting Voting;

        public Character Character
        {
            get { return myCharacter; }
            set { myCharacter = value; }
        }

        public CharacterInfo CharacterInfo
        {
            get { return characterInfo; }
            set { characterInfo = value; }
        }

        public string Name
        {
            get { return name; }
            set
            {
                if (string.IsNullOrEmpty(name)) return;
                name = value;
            }
        }

        public bool GameStarted
        {
            get { return gameStarted; }
        }

        public GUIFrame InGameHUD
        {
            get { return inGameHUD; }
        }


        public virtual List<Client> ConnectedClients
        {
            get { return null; }
        }

        public NetworkMember()
        {
            inGameHUD = new GUIFrame(new Rectangle(0,0,0,0), null, null);
            inGameHUD.CanBeFocused = false;

            int width = (int)MathHelper.Clamp(GameMain.GraphicsWidth * 0.35f, 350, 500);
            int height = (int)MathHelper.Clamp(GameMain.GraphicsHeight * 0.15f, 100, 200);
            chatBox = new GUIListBox(new Rectangle(
                GameMain.GraphicsWidth - 20 - width,
                GameMain.GraphicsHeight - 40 - 25 - height,
                width, height),
                Color.White * 0.5f, GUI.Style, inGameHUD);
            chatBox.Padding = Vector4.Zero;

            chatMsgBox = new GUITextBox(
                new Rectangle(chatBox.Rect.X, chatBox.Rect.Y + chatBox.Rect.Height + 20, chatBox.Rect.Width, 25),
                Color.White * 0.5f, Color.Black, Alignment.TopLeft, Alignment.Left, GUI.Style, inGameHUD);
            chatMsgBox.Font = GUI.SmallFont;
            chatMsgBox.Padding = Vector4.Zero;
            chatMsgBox.OnEnterPressed = EnterChatMessage;
            chatMsgBox.OnTextChanged = TypingChatMessage;

            Voting = new Voting();
        }

        public bool TypingChatMessage(GUITextBox textBox, string text)
        {
            string tempStr;
            string command = ChatMessage.GetChatMessageCommand(text, out tempStr);
            switch (command)
            {
                case "r":
                case "radio":
                    textBox.TextColor = ChatMessage.MessageColor[(int)ChatMessageType.Radio];
                    break;
                case "d":
                case "dead":
                    textBox.TextColor = ChatMessage.MessageColor[(int)ChatMessageType.Dead];
                    break;
                default:
                    textBox.TextColor = ChatMessage.MessageColor[(int)ChatMessageType.Default];
                    break;
            }

            return true;
        }

        public bool CanUseRadio(Character sender)
        {
            if (sender == null) return false;

            var radio = sender.Inventory.Items.FirstOrDefault(i => i != null && i.GetComponent<WifiComponent>() != null);
            if (radio == null || !sender.HasEquippedItem(radio)) return false;
                       
            var radioComponent = radio.GetComponent<WifiComponent>();
            return radioComponent.HasRequiredContainedItems(false);
        }

        public bool EnterChatMessage(GUITextBox textBox, string message)
        {
            textBox.TextColor = ChatMessage.MessageColor[(int)ChatMessageType.Default];

            if (string.IsNullOrWhiteSpace(message)) return false;

            SendChatMessage(message);
                        
            if (textBox == chatMsgBox) textBox.Deselect();

            return true;
        }

        public void AddChatMessage(string message, ChatMessageType type, string senderName="", Character senderCharacter = null)
        {
            AddChatMessage(ChatMessage.Create(senderName, message, type, senderCharacter));
        }
        
        public void AddChatMessage(ChatMessage message)
        {

            if (message.Type == ChatMessageType.Radio && 
                Character.Controlled != null &&
                message.Sender != null && message.Sender != myCharacter)
            {
                var radio = message.Sender.Inventory.Items.First(i => i != null && i.GetComponent<WifiComponent>() != null);
                if (radio == null) return;

                message.Sender.ShowSpeechBubble(2.0f, ChatMessage.MessageColor[(int)ChatMessageType.Radio]);

                var radioComponent = radio.GetComponent<WifiComponent>();
                radioComponent.Transmit(message.TextWithSender);
                return;
            }

            GameServer.Log(message.TextWithSender, message.Color);

            string displayedText = message.Text;

            if (message.Sender != null)
            {
                if (message.Type == ChatMessageType.Default && Character.Controlled != null)
                {
                    displayedText = message.ApplyDistanceEffect(Character.Controlled);
                    if (string.IsNullOrWhiteSpace(displayedText)) return;
                }

                message.Sender.ShowSpeechBubble(2.0f, ChatMessage.MessageColor[(int)ChatMessageType.Default]);
            }

            GameMain.NetLobbyScreen.NewChatMessage(message);

            while (chatBox.CountChildren > 20)
            {
                chatBox.RemoveChild(chatBox.children[1]);
            }

            if (!string.IsNullOrWhiteSpace(message.SenderName))
            {
                displayedText = message.SenderName + ": " + displayedText;
            }
            
            GUITextBlock msg = new GUITextBlock(new Rectangle(0, 0, 0, 20), displayedText,
                ((chatBox.CountChildren % 2) == 0) ? Color.Transparent : Color.Black * 0.1f, message.Color,
                Alignment.Left, null, null, true);
            msg.Font = GUI.SmallFont;
            msg.UserData = message.SenderName;

            msg.Padding = new Vector4(20.0f, 0, 0, 0);

            float prevSize = chatBox.BarSize;

            msg.Padding = new Vector4(20, 0, 0, 0);
            chatBox.AddChild(msg);

            if ((prevSize == 1.0f && chatBox.BarScroll == 0.0f) || (prevSize < 1.0f && chatBox.BarScroll == 1.0f)) chatBox.BarScroll = 1.0f;

            GUISoundType soundType = GUISoundType.Message;
            if (message.Type == ChatMessageType.Radio)
            {
                soundType = GUISoundType.RadioMessage;
            }
            else if (message.Type == ChatMessageType.Dead)
            {
                soundType = GUISoundType.DeadMessage;
            }

            GUI.PlayUISound(soundType);
        }

        public virtual void SendChatMessage(string message, ChatMessageType? type = null) { }

        public virtual void KickPlayer(string kickedName, bool ban, bool range = false) { }

        public virtual void Update(float deltaTime) 
        {
            if (gameStarted && Screen.Selected == GameMain.GameScreen)
            {
                chatMsgBox.Visible = Character.Controlled == null || Character.Controlled.CanSpeak;

                inGameHUD.Update(deltaTime);

                GameMain.GameSession.CrewManager.Update(deltaTime);
                
                if (Character.Controlled == null || Character.Controlled.IsDead)
                {
                    GameMain.GameScreen.Cam.TargetPos = Vector2.Zero;
                    GameMain.LightManager.LosEnabled = false;
                }
            }

            if (PlayerInput.KeyHit(InputType.Chat) && chatMsgBox.Visible)
            {
                if (chatMsgBox.Selected)
                {
                    chatMsgBox.Text = "";
                    chatMsgBox.Deselect();
                }
                else
                {
                    chatMsgBox.Select();
                }
            }
        }

        public virtual void Draw(Microsoft.Xna.Framework.Graphics.SpriteBatch spriteBatch)
        {
            if (!gameStarted || Screen.Selected != GameMain.GameScreen) return;

            GameMain.GameSession.CrewManager.Draw(spriteBatch);

            inGameHUD.Draw(spriteBatch);

            if (EndVoteCount > 0)
            {
                if (GameMain.NetworkMember.myCharacter == null)
                {
                    GUI.DrawString(spriteBatch, new Vector2(GameMain.GraphicsWidth - 180.0f, 40),
                        "Votes to end the round (y/n): " + EndVoteCount + "/" + (EndVoteMax - EndVoteCount), Color.White, null, 0, GUI.SmallFont);
                }
                else
                {
                    GUI.DrawString(spriteBatch, new Vector2(GameMain.GraphicsWidth - 140.0f, 40),
                        "Votes (y/n): " + EndVoteCount + "/" + (EndVoteMax - EndVoteCount), Color.White, null, 0, GUI.SmallFont);
                }
            }

            if (respawnManager != null)
            {
                string respawnInfo = "";

                if (respawnManager.CurrentState == RespawnManager.State.Waiting &&
                    respawnManager.CountdownStarted)
                {
                    respawnInfo = respawnManager.RespawnTimer <= 0.0f ? "" : "Respawn Shuttle dispatching in " + ToolBox.SecondsToReadableTime(respawnManager.RespawnTimer);

                }
                else if (respawnManager.CurrentState == RespawnManager.State.Transporting)
                {
                    respawnInfo = respawnManager.TransportTimer <= 0.0f ? "" : "Shuttle leaving in " + ToolBox.SecondsToReadableTime(respawnManager.TransportTimer);
                }

                if (!string.IsNullOrEmpty(respawnInfo))
                {                
                    GUI.DrawString(spriteBatch,
                        new Vector2(120.0f, 10),
                        respawnInfo, Color.White, null, 0, GUI.SmallFont);
                }

            }
        }

        public virtual bool SelectCrewCharacter(GUIComponent component, object obj)
        {
            return false;
        }

        public virtual void Disconnect() { }
    }

}
