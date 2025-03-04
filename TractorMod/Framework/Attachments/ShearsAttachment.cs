using System;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Common;
using Pathoschild.Stardew.TractorMod.Framework.Config;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.TractorMod.Framework.Attachments
{
    /// <summary>An attachment for the shears.</summary>
    internal class ShearsAttachment : BaseAttachment
    {
        /*********
        ** Fields
        *********/
        /// <summary>The attachment settings.</summary>
        private readonly GenericAttachmentConfig Config;

        /// <summary>The minimum delay before attempting to recheck the same tile.</summary>
        private readonly TimeSpan AnimalCheckDelay = TimeSpan.FromSeconds(1);


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="config">The attachment settings.</param>
        /// <param name="modRegistry">Fetches metadata about loaded mods.</param>
        public ShearsAttachment(GenericAttachmentConfig config, IModRegistry modRegistry)
            : base(modRegistry)
        {
            this.Config = config;
        }

        /// <inheritdoc />
        public override bool IsEnabled(Farmer player, Tool? tool, Item? item, GameLocation location)
        {
            return
                this.Config.Enable
                && tool is Shears;
        }

        /// <inheritdoc />
        public override bool Apply(Vector2 tile, SObject? tileObj, TerrainFeature? tileFeature, Farmer player, Tool? tool, Item? item, GameLocation location)
        {
            Shears shears = (Shears)tool.AssertNotNull();

            if (this.TryStartCooldown(tile.ToString(), this.AnimalCheckDelay))
            {
                FarmAnimal? animal = this.GetBestHarvestableFarmAnimal(shears, location, tile);
                if (animal != null)
                {
                    Vector2 useAt = this.GetToolPixelPosition(tile);

                    shears.animal = animal;
                    shears.DoFunction(location, (int)useAt.X, (int)useAt.Y, 0, player);

                    return true;
                }
            }

            return false;
        }
    }
}
