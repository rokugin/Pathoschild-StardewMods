using System.Collections.Generic;
using ContentPatcher.Framework.Conditions;
using ContentPatcher.Framework.Migrations;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace ContentPatcher.Framework.Patches
{
    /// <summary>A patch which can be applied to an asset.</summary>
    internal interface IPatch : IContextual
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The path of indexes from the root <c>content.json</c> to this patch, used to sort patches by global load order.</summary>
        /// <remarks>For example, the first patch in <c>content.json</c> is <c>[0]</c>. If that patch is an <see cref="PatchType.Include"/> patch, the third patch it loads would be <c>[0, 2]</c> (i.e. patch index 2 within patch index 0).</remarks>
        int[] IndexPath { get; }

        /// <summary>The path to the patch from the root content file.</summary>
        LogPathBuilder Path { get; }

        /// <summary>The patch type.</summary>
        PatchType Type { get; }

        /// <summary>The parent patch for which this patch was loaded, if any.</summary>
        IPatch? ParentPatch { get; }

        /// <summary>The content pack which requested the patch.</summary>
        IContentPack ContentPack { get; }

        /// <summary>The aggregate migration which applies for this patch.</summary>
        IRuntimeMigration Migrator { get; }

        /// <summary>The normalized asset key from which to load the local asset (if applicable).</summary>
        string? FromAsset { get; }

        /// <summary>The raw asset key from which to load the local asset (if applicable), including tokens.</summary>
        ITokenString? RawFromAsset { get; }

        /// <summary>The normalized asset name to intercept.</summary>
        IAssetName? TargetAsset { get; }

        /// <summary>The locale code in the target asset's name to match (like <c>fr-FR</c> to target <c>Characters/Dialogue/Abigail.fr-FR</c>), or an empty string to match only the base unlocalized asset, or <c>null</c> to match all localized or unlocalized variants of the <see cref="TargetAsset"/>.</summary>
        string? TargetLocale { get; }

        /// <summary>If the <see cref="TargetAsset"/> was redirected by a runtime migration, the asset name before it was redirected.</summary>
        public IAssetName? TargetAssetBeforeRedirection { get; }

        /// <summary>The raw asset name to intercept, including tokens.</summary>
        ITokenString? RawTargetAsset { get; }

        /// <summary>The priority for this patch when multiple patches apply.</summary>
        /// <remarks>This is an <see cref="AssetLoadPriority"/> or <see cref="AssetEditPriority"/> value, depending on the patch type.</remarks>
        int Priority { get; }

        /// <summary>When the patch should be updated.</summary>
        UpdateRate UpdateRate { get; }

        /// <summary>The conditions which determine whether this patch should be applied.</summary>
        Condition[] Conditions { get; }

        /// <summary>Whether the patch is currently applied to the target asset.</summary>
        bool IsApplied { get; set; }


        /*********
        ** Public methods
        *********/
        /// <summary>Get whether the <see cref="FromAsset"/> file exists.</summary>
        bool FromAssetExists();

        /// <summary>Load the initial version of the asset.</summary>
        /// <typeparam name="T">The asset type.</typeparam>
        /// <param name="assetName">The asset name to load.</param>
        /// <exception cref="System.NotSupportedException">The current patch type doesn't support loading assets.</exception>
        T Load<T>(IAssetName assetName)
            where T : notnull;

        /// <summary>Apply the patch to a loaded asset.</summary>
        /// <typeparam name="T">The asset type.</typeparam>
        /// <param name="asset">The asset to edit.</param>
        /// <exception cref="System.NotSupportedException">The current patch type doesn't support editing assets.</exception>
        void Edit<T>(IAssetData asset)
            where T : notnull;

        /// <summary>Get a human-readable list of changes applied to the asset for display when troubleshooting.</summary>
        IEnumerable<string> GetChangeLabels();
    }
}
