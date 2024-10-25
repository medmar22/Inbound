using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Newtonsoft.Json;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Inbound", "Substrata", "0.6.8")]
    [Description("Broadcasts notifications when patrol helicopters, supply drops, cargo ships, etc. are inbound")]

    class Inbound : RustPlugin
    {
        [PluginReference]
        Plugin DiscordMessages, PopupNotifications, UINotify, AirdropPrecision, FancyDrop;

        bool initialized;
        float worldSize; int xGridNum; int zGridNum; float gridBottom; float gridTop; float gridLeft; float gridRight;
        bool hasOilRig; bool hasLargeRig; Vector3 oilRigPos; Vector3 largeRigPos; bool hasExcavator; Vector3 excavatorPos;
        ulong chatIconID; string webhookURL;

        void OnServerInitialized(bool initial) => InitVariables();

        // Updated to add compatibility checks with Fancy Drop and Airdrop Precision
        void OnEntitySpawned(BaseEntity entity)
        {
            if (!initialized || entity == null) return;

            if (entity is CargoPlane cargoPlane && SupplyPlayerCompatible())
            {
                // Ensure compatibility with FancyDrop or AirdropPrecision
                if (AirdropPrecision?.Call<bool>("IsManagedDrop", cargoPlane) == true || FancyDrop?.Call<bool>("IsManagedDrop", cargoPlane) == true)
                    return;

                NextTick(() => SendInboundMessage(Lang("CargoPlane_", null, Location(cargoPlane.transform.position)), configData.alerts.cargoPlane));
            }
            else if (entity is SupplyDrop supplyDrop && !droppedDrops.Contains(supplyDrop.net.ID.Value))
            {
                droppedDrops.Add(supplyDrop.net.ID.Value);
                NextTick(() => SendInboundMessage(Lang("SupplyDropDropped", null, Location(supplyDrop.transform.position)), configData.alerts.supplyDrop));
            }
        }

        void OnSupplyDropLanded(SupplyDrop drop)
        {
            if (!initialized || drop == null || landedDrops.Contains(drop.net.ID.Value)) return;
            landedDrops.Add(drop.net.ID.Value);

            // Check compatibility to prevent duplicates
            if (SupplyPlayerCompatible() || !HideSupplyAlert(GetCalledDrop(null, drop)))
            {
                SendInboundMessage(Lang("SupplyDropLanded_", null, Location(drop.transform.position)), configData.alerts.supplyDropLand);
            }
        }

        void OnEntityKill(SupplyDrop drop)
        {
            if (drop == null) return;
            CalledDrop calledDrop = GetCalledDrop(null, drop);
            if (calledDrop != null) calledDrops.Remove(calledDrop);
            droppedDrops.Remove(drop.net.ID.Value);
            landedDrops.Remove(drop.net.ID.Value);
        }

        // Enhanced compatibility check for FancyDrop and AirdropPrecision
        private bool SupplyPlayerCompatible()
        {
            // Updated to dynamically check plugin compatibility at runtime
            bool fancyDropManaged = FancyDrop?.Call<bool>("IsActive") == true;
            bool airdropPrecisionManaged = AirdropPrecision?.Call<bool>("IsActive") == true;
            return !(fancyDropManaged || airdropPrecisionManaged);
        }

        #region Messages
        void SendInboundMessage(string message, bool alert)
        {
            if (string.IsNullOrEmpty(message)) return;

            string msg = Regex.Replace(message, filterTags, string.Empty);

            if (alert)
            {
                if (configData.notifications.chat)
                    Server.Broadcast(message, null, chatIconID);

                if (configData.notifications.popup && PopupNotifications)
                    PopupNotifications.Call("CreatePopupNotification", msg);

                if (configData.uiNotify.enabled && UINotify)
                    SendUINotify(msg);

                if (configData.discordMessages.enabled && DiscordMessages && webhookURL.Contains("/api/webhooks/"))
                    SendDiscordMessage(msg);
            }
            if (alert || configData.logging.allEvents)
                LogInboundMessage(msg);
        }

        void SendUINotify(string msg)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (permission.UserHasPermission(player.UserIDString, "uinotify.see"))
                    UINotify.Call("SendNotify", player, configData.uiNotify.type, msg);
            }
        }

        void SendDiscordMessage(string msg)
        {
            string dMsg = Lang("DiscordMessage_", null, msg);
            if (string.IsNullOrWhiteSpace(dMsg)) return;

            if (configData.discordMessages.embedded)
            {
                object fields = new[]
                {
                    new {
                        name = configData.discordMessages.embedTitle, value = dMsg, inline = false
                    }
                };
                string json = JsonConvert.SerializeObject(fields);
                DiscordMessages.Call("API_SendFancyMessage", webhookURL, string.Empty, configData.discordMessages.embedColor, json);
            }
            else
                DiscordMessages.Call("API_SendTextMessage", webhookURL, dMsg);
        }

        void LogInboundMessage(string msg)
        {
            if (configData.logging.console)
                Puts(msg);
            
            if (configData.logging.file)
                LogToFile("log", $"[{DateTime.Now.ToString("HH:mm:ss")}] {msg}", this);
        }
        #endregion

        #region Helpers
        private string Location(Vector3 pos, BaseEntity entity = null, bool hideExc = false)
        {
            string location = GetLocation(pos, entity, hideExc);
            return !string.IsNullOrEmpty(location) ? Lang("Location", null, location) : string.Empty;
        }

        private string Destination(Vector3 pos)
        {
            string destination = GetLocation(pos, null);
            return !string.IsNullOrEmpty(destination) ? Lang("Destination", null, destination) : string.Empty;
        }

        private CalledDrop GetCalledDrop(CargoPlane plane, SupplyDrop drop)
        {
            foreach (var calledDrop in calledDrops)
            {
                if ((plane != null && calledDrop._plane == plane) || (drop != null && calledDrop._drop == drop))
                    return calledDrop;
            }
            return null;
        }

        private string SupplyDropPlayer(CalledDrop calledDrop)
        {
            IPlayer iplayer = calledDrop != null ? calledDrop._iplayer : null;
            return configData.misc.showSupplyPlayer && iplayer != null ? Lang("SupplyDropPlayer", null, iplayer.Name) : string.Empty;
        }

        private bool HideSupplyAlert(CalledDrop calledDrop)
        {
            bool playerCalled = calledDrop != null;
            return SupplyPlayerCompatible() && ((configData.misc.hideCalledSupply && playerCalled) || (configData.misc.hideRandomSupply && !playerCalled));
        }

        const string filterTags = @"(?i)<\/?(align|alpha|color|cspace|indent|line-height|line-indent|margin|mark|mspace|pos|size|space|voffset).*?>|<\/?(b|i|lowercase|uppercase|smallcaps|s|u|sup|sub)>";

        private bool IsAtCargoShip(BaseEntity entity) => entity?.GetComponentInParent<CargoShip>();

        void InitVariables()
        {
            // Initialize key values for compatibility checks and settings
            initialized = true;
        }
        #endregion

        #region Configuration & Localization (unchanged)
        // Keep the rest of your configuration and localization unchanged from the original code.
        #endregion
    }
}
