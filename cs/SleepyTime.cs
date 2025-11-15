using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using ConVar;

namespace Oxide.Plugins
{
    [Info("SleepyTime", "Waynieoaks", "1.0.0")]
    [Description("Allows players to use /sleep to toggle sleeping state and skip night if all asleep.")]
    public class SleepyTime : RustPlugin
    {
        private const string UiBackgroundColor = "0 0.4 0.7 0.8";
		private const string UiTextColor = "1 1 1 1";
		private const string SleepUiPanelName = "SleepyTimeUI";
		
		// --- Config class ---
        private class Configuration
        {
            public bool Autowake = true;
			public bool RequireBed = true;
			public bool ShowBanner = true;
            public float MorningHour = 8f;
            public float EveningHour = 20f;
        }

        private Configuration config;

        // --- Config loading/saving ---

        protected override void LoadDefaultConfig()
        {
            config = new Configuration();
            SaveConfig();
        }

        private void LoadConfigValues()
        {
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                    throw new System.Exception("Config file is empty, creating new one.");

                // In case you add new fields later, make sure they have sane defaults
                if (config.MorningHour <= 0f || config.MorningHour >= 24f)
                    config.MorningHour = 8f;

                if (config.EveningHour <= 0f || config.EveningHour >= 24f)
                    config.EveningHour = 20f;
            }
            catch
            {
                PrintWarning("Failed to load config, creating default configuration.");
                LoadDefaultConfig();
            }

            SaveConfig();
        }

        private void SaveConfig()
        {
            Config.WriteObject(config, true);
        }

        private void Init()
        {
            LoadConfigValues();
        }

        // --- Chat command ---

        [ChatCommand("sleep")]
		private void CmdSleep(BasePlayer player, string command, string[] args)
		{
			if (player == null || !player.IsConnected)
				return;

			// If already sleeping, wake them up (always allowed)
			if (player.IsSleeping())
			{
				player.EndSleeping();
				player.SendNetworkUpdateImmediate();
				
				// Update status (or clear UI if nobody left sleeping)
				BroadcastSleepStatus();
				return;
			}

			// If config requires a bed/bag, enforce it
			if (config.RequireBed && !IsOnBedOrBag(player))
			{
				SendReply(player, "You must be standing on a bed or sleeping bag to sleep.");
				return;
			}

			// Get current in-game hour (0–24)
			float hour = TOD_Sky.Instance.Cycle.Hour;

			// Block going to sleep if it's not night
			// Night = before MorningHour OR after/equal EveningHour
			// So we BLOCK if we are between MorningHour and EveningHour
			if (hour >= config.MorningHour && hour < config.EveningHour)
			{
				SendReply(player, "You can only sleep at night.");
				return;
			}

			// Otherwise, put them to sleep in place
			player.StartSleeping();
			player.SendNetworkUpdateImmediate();
			
			// Tell everyone how many are sleeping
			BroadcastSleepStatus();

			// After going to sleep, check if all online players are now sleeping
			CheckAllPlayersSleeping();
		}

        // --- Helper methods ---
		
		private void ShowSleepUi(BasePlayer player, int sleeping, int total)
		{
			if (player == null || !player.IsConnected)
				return;

			// Remove old panel if it exists
			CuiHelper.DestroyUi(player, SleepUiPanelName);

			var container = new CuiElementContainer();

			// Add the panel and give it a name via Add(...)
			container.Add(new CuiPanel
			{
				RectTransform =
				{
					AnchorMin = "0.34 0.14",
					AnchorMax = "0.64 0.18"
				},
				Image =
				{
					Color = UiBackgroundColor
				}
			}, "Overlay", SleepUiPanelName);

			// Add the label as a child of that panel
			container.Add(new CuiLabel
			{
				RectTransform =
				{
					AnchorMin = "0 0",
					AnchorMax = "1 1"
				},
				Text =
				{
					Text = $"{sleeping}/{total} players are sleeping – night will skip when everyone is in bed.",
					FontSize = 14,
					Align = TextAnchor.MiddleCenter,
					Color = UiTextColor
				}
			}, SleepUiPanelName);

			CuiHelper.AddUi(player, container);
		}

		private void ClearSleepUi(BasePlayer player)
		{
			if (player == null || !player.IsConnected)
				return;

			CuiHelper.DestroyUi(player, SleepUiPanelName);
		}

		private void UpdateSleepUiAll(int sleeping, int total)
		{
			foreach (var p in BasePlayer.activePlayerList)
			{
				if (p == null || !p.IsConnected)
					continue;

				ShowSleepUi(p, sleeping, total);
			}
		}

		private void ClearSleepUiAll()
		{
			foreach (var p in BasePlayer.activePlayerList)
			{
				if (p == null || !p.IsConnected)
					continue;

				ClearSleepUi(p);
			}
		}
		
		private void BroadcastSleepStatus()
		{
			if (!config.ShowBanner)
			{
				ClearSleepUiAll();
				return;
			}
			
			var players = BasePlayer.activePlayerList;

			if (players == null || players.Count == 0)
			{
				ClearSleepUiAll();
				return;
			}

			int total = 0;
			int sleeping = 0;

			foreach (var p in players)
			{
				if (p == null || !p.IsConnected)
					continue;

				total++;

				if (p.IsSleeping())
					sleeping++;
			}

			if (total == 0)
			{
				ClearSleepUiAll();
				return;
			}

			// If nobody is sleeping, hide the banner
			if (sleeping == 0)
			{
				ClearSleepUiAll();
				return;
			}

			// If everyone is sleeping, hide the banner and let the skip happen quietly
			if (sleeping == total)
			{
				ClearSleepUiAll();
				return;
			}

			// Otherwise show the banner with current status
			UpdateSleepUiAll(sleeping, total);
		}

        private void CheckAllPlayersSleeping()
        {
            var players = BasePlayer.activePlayerList;

            if (players == null || players.Count == 0)
                return;

            // If any online player is NOT sleeping, do nothing
            foreach (var p in players)
            {
                if (p == null || !p.IsConnected)
                    continue;

                if (!p.IsSleeping())
                    return;
            }

            // If we get here, all connected players are sleeping
            SkipToDayAndWakeEveryone();
        }
		
		private bool IsOnBedOrBag(BasePlayer player)
		{
			if (player == null || player.transform == null)
				return false;

			// Start raycast just above feet and cast down a short distance
			var origin = player.transform.position + Vector3.up * 0.1f;
			RaycastHit hit;

			if (!UnityEngine.Physics.Raycast(origin, Vector3.down, out hit, 2f))
				return false;

			var entity = hit.GetEntity();
			if (entity == null)
				return false;

			// Check if the thing under the player is a sleeping bag or a bed
			return entity is SleepingBag;
		}

        private void SkipToDayAndWakeEveryone()
        {
            ClearSleepUiAll();
			
			// Set time to morning
            Env.time = config.MorningHour;

            // Optional auto-wake behaviour based on config
            if (!config.Autowake)
                return;

            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.IsConnected)
                    continue;

                if (p.IsSleeping())
                {
                    p.EndSleeping();
                    p.SendNetworkUpdateImmediate();
                }
            }
        }
		
		private void Unload()
		{
			ClearSleepUiAll();
		}
		
    }
}