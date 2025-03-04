using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework;
using Pathoschild.Stardew.Automate.Framework.Commands;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Common;
using Pathoschild.Stardew.Common.Messages;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace Pathoschild.Stardew.Automate
{
    /// <summary>The mod entry point.</summary>
    internal class ModEntry : Mod
    {
        /*********
        ** Fields
        *********/
        /// <summary>The internal mod data.</summary>
        private DataModel Data = null!; // set in Entry

        /// <summary>The mod configuration.</summary>
        private ModConfig Config = null!; // set in Entry

        /// <summary>The configured key bindings.</summary>
        private ModConfigKeys Keys => this.Config.Controls;

        /// <summary>Manages machine groups.</summary>
        private MachineManager MachineManager = null!; // set in Entry

        /// <summary>Handles console commands from players.</summary>
        private CommandHandler CommandHandler = null!; // set in Entry

        /// <summary>Whether to automate machines for the current save.</summary>
        private bool EnableAutomation => this.Config.Enabled && Context.IsMainPlayer;

        /// <summary>Whether to track machine changes for the current save.</summary>
        private bool EnableAutomationChangeTracking =>
            this.Config.Enabled
            && !this.IsSecondaryScreen // in split-screen mode, the change will be tracked by the main player
            && (Context.IsMainPlayer || this.CurrentOverlay.Value is not null);

        /// <summary>Whether this is a secondary screen in split-screen mode.</summary>
        private bool IsSecondaryScreen => Context.IsSplitScreen && !Context.IsMainPlayer;

        /// <summary>The number of ticks until the next automation cycle.</summary>
        private int AutomateCountdown;

        /// <summary>The number of ticks until the config UI is registered with Generic Mod Config Menu.</summary>
        /// <remarks>This must happen later than <see cref="IGameLoopEvents.GameLaunched"/>, since Content Patcher packs haven't added their edits to <c>Data/Machines</c> yet at that point.</remarks>
        private int RegisterConfigCountdown = 10;

        /// <summary>The current overlay being displayed, if any.</summary>
        private readonly PerScreen<OverlayMenu?> CurrentOverlay = new();


        /*********
        ** Public methods
        *********/
        /// <inheritdoc />
        public override void Entry(IModHelper helper)
        {
            I18n.Init(helper.Translation);
            CommonHelper.RemoveObsoleteFiles(this, "Automate.pdb"); // removed in 1.28.4

            // read data file
            const string dataPath = "assets/data.json";
            try
            {
                DataModel? data = this.Helper.Data.ReadJsonFile<DataModel>(dataPath);
                if (data == null)
                {
                    data = new(null);
                    this.Monitor.Log($"The {dataPath} file seems to be missing or invalid. Floor connectors will be disabled.", LogLevel.Error);
                }
                this.Data = data;
            }
            catch (Exception ex)
            {
                this.Data = new(null);
                this.Monitor.Log($"The {dataPath} file seems to be invalid. Floor connectors will be disabled.\n{ex}", LogLevel.Error);
            }

            // read config
            this.Config = this.Helper.ReadConfig<ModConfig>();

            // init
            this.MachineManager = new MachineManager(
                config: () => this.Config,
                data: this.Data,
                defaultFactory: new AutomationFactory(
                    config: () => this.Config,
                    monitor: this.Monitor,
                    reflection: this.Helper.Reflection,
                    isBetterJunimosLoaded: helper.ModRegistry.IsLoaded("hawkfalcon.BetterJunimos")
                ),
                monitor: this.Monitor
            );

            this.CommandHandler = new CommandHandler(this.Monitor, () => this.Config, this.MachineManager);

            // hook events
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.Input.ButtonsChanged += this.OnButtonsChanged;
            helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
            helper.Events.Player.Warped += this.OnWarped;
            helper.Events.World.BuildingListChanged += this.OnBuildingListChanged;
            helper.Events.World.LocationListChanged += this.OnLocationListChanged;
            helper.Events.World.ObjectListChanged += this.OnObjectListChanged;
            helper.Events.World.TerrainFeatureListChanged += this.OnTerrainFeatureListChanged;
            helper.Events.World.LargeTerrainFeatureListChanged += this.OnLargeTerrainFeatureListChanged;

            // hook commands
            this.CommandHandler.RegisterWith(helper.ConsoleCommands);

            // log info
            this.Monitor.VerboseLog($"Initialized with automation every {this.Config.AutomationInterval} ticks.");
            if (this.Config.WarnForMissingBridgeMod)
                this.ReportMissingBridgeMods(this.Data.SuggestedIntegrations);
        }

        /// <inheritdoc />
        public override object GetApi()
        {
            return new AutomateAPI(this.Monitor, this.MachineManager);
        }


        /*********
        ** Private methods
        *********/
        /****
        ** Event handlers
        ****/
        /// <inheritdoc cref="IGameLoopEvents.SaveLoaded" />
        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            // disable if secondary player
            if (!this.EnableAutomation)
            {
                if (Context.IsMultiplayer)
                {
                    if (this.HostHasAutomate(out ISemanticVersion? installedVersion))
                        this.Monitor.Log($"Automate {installedVersion} is installed by the main player, so machines will be automated by their instance.");
                    else
                        this.Monitor.Log("Automate isn't installed by the main player, so machines won't be automated.", LogLevel.Warn);
                }
                else
                    this.Monitor.Log("You disabled Automate in the mod settings, so it won't do anything.", LogLevel.Info);
            }
        }

        /// <inheritdoc cref="IGameLoopEvents.DayStarted" />
        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            // reset machine state
            if (!this.IsSecondaryScreen) // in split-screen mode, machine state is managed by the main screen
            {
                this.MachineManager.Reset();
                this.AutomateCountdown = 0;
            }

            // reset overlay
            this.DisableOverlay();
        }

        /// <inheritdoc cref="IPlayerEvents.Warped" />
        private void OnWarped(object? sender, WarpedEventArgs e)
        {
            if (e.IsLocalPlayer)
                this.ResetOverlayIfShown();
        }

        /// <inheritdoc cref="IWorldEvents.LocationListChanged" />
        private void OnLocationListChanged(object? sender, LocationListChangedEventArgs e)
        {
            if (!this.EnableAutomationChangeTracking)
                return;

            this.Monitor.VerboseLog("Location list changed, reloading machines in affected locations.");

            try
            {
                if (e.Removed.Any())
                    this.MachineManager.QueueRemove(e.Removed);

                this.MachineManager.QueueReload(e.Added);
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "updating locations");
            }
        }

        /// <inheritdoc cref="IWorldEvents.BuildingListChanged" />
        private void OnBuildingListChanged(object? sender, BuildingListChangedEventArgs e)
        {
            if (!this.EnableAutomationChangeTracking || this.MachineManager.IsReloadQueued(e.Location))
                return;

            this.Monitor.VerboseLog(
                this.ReloadIfNeeded(e.Location, this.GetDiffList(e.Added, e.Removed, BaseMachine.GetTileAreaFor))
                    ? $"Building list changed in {e.Location.Name}, reloading its machines."
                    : $"Building list changed in {e.Location.Name}, but no reload is needed."
            );
        }

        /// <inheritdoc cref="IWorldEvents.ObjectListChanged" />
        private void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e)
        {
            if (!this.EnableAutomationChangeTracking || this.MachineManager.IsReloadQueued(e.Location))
                return;

            this.Monitor.VerboseLog(
                this.ReloadIfNeeded(e.Location, this.GetDiffList(e.Added, e.Removed))
                    ? $"Object list changed in {e.Location.Name}, reloading its machines."
                    : $"Object list changed in {e.Location.Name}, but no reload is needed."
            );
        }

        /// <inheritdoc cref="IWorldEvents.TerrainFeatureListChanged" />
        private void OnTerrainFeatureListChanged(object? sender, TerrainFeatureListChangedEventArgs e)
        {
            if (!this.EnableAutomationChangeTracking || this.MachineManager.IsReloadQueued(e.Location))
                return;

            this.Monitor.VerboseLog(
                this.ReloadIfNeeded(e.Location, this.GetDiffList(e.Added, e.Removed))
                    ? $"Terrain feature list changed in {e.Location.Name}, reloading its machines."
                    : $"Terrain feature list changed in {e.Location.Name}, but no reload is needed."
            );
        }

        /// <inheritdoc cref="IWorldEvents.LargeTerrainFeatureListChanged" />
        private void OnLargeTerrainFeatureListChanged(object? sender, LargeTerrainFeatureListChangedEventArgs e)
        {
            if (!this.EnableAutomationChangeTracking || this.MachineManager.IsReloadQueued(e.Location))
                return;

            this.Monitor.VerboseLog(
                this.ReloadIfNeeded(e.Location, this.GetDiffList(e.Added, e.Removed, BaseMachine.GetTileAreaFor))
                    ? $"Large terrain feature list changed in {e.Location.Name}, reloading its machines."
                    : $"Large terrain feature list changed in {e.Location.Name}, but no reload is needed."
            );
        }

        /// <inheritdoc cref="IGameLoopEvents.UpdateTicked" />
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            // add Generic Mod Config Menu integration
            if (this.RegisterConfigCountdown > 0 && --this.RegisterConfigCountdown == 0)
            {
                new GenericModConfigMenuIntegrationForAutomate(
                    data: this.Data,
                    getConfig: () => this.Config,
                    reset: () => this.Config = new ModConfig(),
                    saveAndApply: () =>
                    {
                        this.Helper.WriteConfig(this.Config);
                        this.ReloadConfig();
                    },
                    modRegistry: this.Helper.ModRegistry,
                    monitor: this.Monitor,
                    manifest: this.ModManifest
                ).Register();
            }

            // run automation
            if (Context.IsWorldReady && this.EnableAutomation)
            {
                try
                {
                    // reload machines if needed
                    if (this.EnableAutomationChangeTracking)
                    {
                        if (this.MachineManager.ReloadQueuedLocations())
                            this.ResetOverlayIfShown();
                    }

                    // process machines
                    if (--this.AutomateCountdown <= 0)
                    {
                        this.AutomateCountdown = this.Config.AutomationInterval;

                        foreach (IMachineGroup group in this.MachineManager.GetActiveMachineGroups())
                            group.Automate();
                    }
                }
                catch (Exception ex)
                {
                    this.HandleError(ex, "processing machines");
                }
            }
        }

        /// <inheritdoc cref="IInputEvents.ButtonsChanged" />
        private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
        {
            if (!this.Config.Enabled) // don't check EnableAutomation, since overlay is still available for farmhands
                return;

            try
            {
                // toggle overlay
                if (Context.IsPlayerFree && this.Keys.ToggleOverlay.JustPressed())
                {
                    if (this.CurrentOverlay.Value != null)
                        this.DisableOverlay();
                    else
                        this.EnableOverlay();
                }
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "handling key input");
            }
        }

        /// <inheritdoc cref="IMultiplayerEvents.ModMessageReceived" />
        private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            // update automation if chest options changed
            if (Context.IsMainPlayer && e.FromModID == "Pathoschild.ChestsAnywhere" && e.Type == nameof(AutomateUpdateChestMessage))
            {
                var message = e.ReadAs<AutomateUpdateChestMessage>();
                var location = message.LocationName != null
                    ? Game1.getLocationFromName(message.LocationName)
                    : null;
                var player = Game1.GetPlayer(e.FromPlayerID);

                string label;
                if (player is null)
                    label = $"unknown player {e.FromPlayerID}/{e.FromModID}";
                else if (player != Game1.MasterPlayer)
                    label = $"{player.Name}/{e.FromModID}";
                else
                    label = e.FromModID;

                if (location != null)
                {
                    this.Monitor.Log($"Received chest update from {label} for chest at {message.LocationName} ({message.Tile}), updating machines.");
                    this.MachineManager.QueueReload(location);
                }
                else
                    this.Monitor.Log($"Received chest update from {label} for chest at {message.LocationName} ({message.Tile}), but no such location was found.");
            }
        }

        /****
        ** Methods
        ****/
        /// <summary>Update when the configuration changes.</summary>
        public void ReloadConfig()
        {
            this.AutomateCountdown = Math.Min(this.AutomateCountdown, this.Config.AutomationInterval);

            if (!this.Config.Enabled)
            {
                if (this.MachineManager.GetActiveMachineGroups().Any())
                    this.Monitor.Log("Disabled per config change. Machines are no longer automated.", LogLevel.Warn);

                this.MachineManager.Clear();
                this.DisableOverlay();
            }
            else
            {
                this.MachineManager.Reset();
                this.ResetOverlayIfShown();
            }
        }

        /// <summary>Log warnings if custom-machine frameworks are installed without their automation component.</summary>
        /// <param name="integrations">Mods which add custom machine recipes and require a separate automation component.</param>
        private void ReportMissingBridgeMods(DataModelIntegration[] integrations)
        {
            var registry = this.Helper.ModRegistry;
            foreach (DataModelIntegration integration in integrations)
            {
                if (registry.IsLoaded(integration.Id) && !registry.IsLoaded(integration.SuggestedId))
                    this.Monitor.Log($"Machine recipes added by {integration.Name} aren't currently automated. Install {integration.SuggestedName} too to enable them: {integration.SuggestedUrl}.", LogLevel.Warn);
            }
        }

        /// <summary>Get whether the host player has Automate installed.</summary>
        /// <param name="version">The installed version, if any.</param>
        private bool HostHasAutomate([NotNullWhen(true)] out ISemanticVersion? version)
        {
            if (Context.IsMainPlayer || Context.IsSplitScreen)
            {
                version = this.ModManifest.Version;
                return true;
            }

            IMultiplayerPeer? host = this.Helper.Multiplayer.GetConnectedPlayer(Game1.MasterPlayer.UniqueMultiplayerID);
            IMultiplayerPeerMod? mod = host?.Mods.SingleOrDefault(p => string.Equals(p.ID, this.ModManifest.UniqueID, StringComparison.OrdinalIgnoreCase));

            version = mod?.Version;
            return mod != null;
        }

        /// <summary>Log an error and warn the user.</summary>
        /// <param name="ex">The exception to handle.</param>
        /// <param name="verb">The verb describing where the error occurred (e.g. "looking that up").</param>
        private void HandleError(Exception ex, string verb)
        {
            this.Monitor.Log($"Something went wrong {verb}:\n{ex}", LogLevel.Error);
            CommonHelper.ShowErrorMessage($"Huh. Something went wrong {verb}. The error log has the technical details.");
        }

        /// <summary>Disable the overlay, if shown.</summary>
        private void DisableOverlay()
        {
            this.CurrentOverlay.Value?.Dispose();
            this.CurrentOverlay.Value = null;
        }

        /// <summary>Enable the overlay.</summary>
        private void EnableOverlay()
        {
            if (!Context.IsMainPlayer)
            {
                this.MachineManager.Reset();
                this.MachineManager.ReloadQueuedLocations();
            }

            this.CurrentOverlay.Value ??= new OverlayMenu(
                events: this.Helper.Events,
                inputHelper: this.Helper.Input,
                reflection: this.Helper.Reflection,
                locationKey: this.MachineManager.Factory.GetLocationKey(Game1.currentLocation),
                machineData: this.MachineManager.GetMachineDataFor(Game1.currentLocation),
                junimoGroup: this.MachineManager.JunimoMachineGroup
            );
        }

        /// <summary>Reset the overlay if it's being shown.</summary>
        private void ResetOverlayIfShown()
        {
            if (this.CurrentOverlay.Value != null)
            {
                this.DisableOverlay();
                this.EnableOverlay();
            }
        }

        /// <summary>Rescan machines in a location if added/removed entities may change active automation.</summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="location">The location whose entities changed.</param>
        /// <param name="entities">The entities that were added or removed.</param>
        private bool ReloadIfNeeded<TEntity>(GameLocation location, IEnumerable<DiffEntry<TEntity>> entities)
            where TEntity : notnull
        {
            string locationKey = this.MachineManager.Factory.GetLocationKey(location);
            MachineDataForLocation? data = this.MachineManager.GetMachineDataFor(location);
            JunimoMachineGroup junimoData = this.MachineManager.JunimoMachineGroup;

            bool shouldReload = false;
            foreach ((Rectangle tileArea, TEntity entity, bool isAdded) in entities)
            {
                // ignore unknown entity
                IAutomatable? automateable = this.MachineManager.Factory.GetEntityFor(location, new Vector2(tileArea.X, tileArea.Y), entity);
                if (automateable is null)
                    continue;

                // reload if added to an unknown location
                if (data is null)
                {
                    if (isAdded)
                    {
                        shouldReload = true;
                        break;
                    }

                    continue;
                }

                // reload if potentially connected to a chest
                if (isAdded)
                {
                    shouldReload =
                        junimoData.ContainsOrAdjacent(locationKey, tileArea)
                        || (automateable is IContainer ? data.ContainsOrAdjacent(tileArea) : data.IsConnectedToChest(tileArea));

                    if (shouldReload)
                        break;
                }

                // reload if removed from a valid machine group
                if (data.IntersectsAutomatedGroup(tileArea) || junimoData.IntersectsAutomatedGroup(locationKey, tileArea))
                {
                    shouldReload = true;
                    break;
                }

                // else track entity change
                data.MarkOutdated(tileArea, automateable);
            }

            if (shouldReload)
                this.MachineManager.QueueReload(location);

            return shouldReload;
        }

        /// <summary>Get a standardized list of changed entities.</summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="added">The added entities.</param>
        /// <param name="removed">The removed entities.</param>
        private IEnumerable<DiffEntry<TEntity>> GetDiffList<TEntity>(IEnumerable<KeyValuePair<Vector2, TEntity>> added, IEnumerable<KeyValuePair<Vector2, TEntity>> removed)
            where TEntity : notnull
        {
            return
                added.Select(cur => new DiffEntry<TEntity>(new Rectangle((int)cur.Key.X, (int)cur.Key.Y, 1, 1), cur.Value, true))
                .Concat(removed.Select(cur => new DiffEntry<TEntity>(new Rectangle((int)cur.Key.X, (int)cur.Key.Y, 1, 1), cur.Value, false)));
        }

        /// <summary>Get a standardized list of changed entities.</summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="added">The added entities.</param>
        /// <param name="removed">The removed entities.</param>
        /// <param name="getTileArea">Get the tile area for an entity.</param>
        private IEnumerable<DiffEntry<TEntity>> GetDiffList<TEntity>(IEnumerable<TEntity> added, IEnumerable<TEntity> removed, Func<TEntity, Rectangle> getTileArea)
            where TEntity : notnull
        {
            return
                added.Select(cur => new DiffEntry<TEntity>(getTileArea(cur), cur, true))
                .Concat(removed.Select(cur => new DiffEntry<TEntity>(getTileArea(cur), cur, false)));
        }

        /// <summary>A standardized entity change.</summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="TileArea">The tile area covered by the entity.</param>
        /// <param name="Entity">The entity value.</param>
        /// <param name="Added">Whether the entity was added (else removed).</param>
        private readonly record struct DiffEntry<TEntity>(Rectangle TileArea, TEntity Entity, bool Added)
            where TEntity : notnull;
    }
}
