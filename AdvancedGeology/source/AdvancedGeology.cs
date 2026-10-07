using System.Text;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AdvancedGeology.Patches;
using AdvancedGeology.Byproducts;
using AdvancedGeology.WorldGen;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace AdvancedGeology
{
    public sealed class AdvancedGeologyModSystem : ModSystem
    {
        private Harmony? harmony;
        private const string ConfigFileName = "advancedgeology.json";
        private AdvancedGeologyConfig config = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IAsset, AppliedDepositData> appliedDeposits = new();
        private sealed class AppliedDepositData
        {
            internal byte[]? Data;
        }

        public override void StartPre(ICoreAPI api)
        {
            base.StartPre(api);

            try
            {
                AdvancedGeologyConfig? loaded = api.LoadModConfig<AdvancedGeologyConfig>(ConfigFileName);
                if (loaded == null)
                {
                    api.StoreModConfig(config, ConfigFileName);
                }
                else
                {
                    config = loaded;
                }
            }
            catch (Exception exception)
            {
                api.Logger.Warning(
                    "[AdvancedGeology] Could not load {0}; using defaults: {1}",
                    ConfigFileName,
                    exception.Message);
                config = new AdvancedGeologyConfig();
            }
            AdvancedGeologyConfig.Active = config;


            DepositGeneratorRegistry.RegisterDepositGenerator<LayeredSurfaceDepositGenerator>("disc-layeredsurface");
            DepositGeneratorRegistry.RegisterDepositGenerator<SaltDomeDepositGenerator>("saltdome");
            DepositGeneratorRegistry.RegisterDepositGenerator<ProceduralProspectingDepositGenerator>("procedural-prospecting");
            api.Logger.VerboseDebug("[AdvancedGeology] Registered custom deposit generators: disc-layeredsurface, saltdome, procedural-prospecting");


            ByproductComposition.RegisterIgnoredAttribute();
        }

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            bool canJewelryEnabled = api.ModLoader.IsModEnabled("canjewelry");
            harmony = new Harmony("advancedgeology");
            harmony.CreateClassProcessor(typeof(Patch_ForestFloorSystem_CheckAndReplaceForestFloor)).Patch();
            ByproductPatches.Apply(harmony);
            harmony.Patch(AccessTools.Method(typeof(GenDeposits), nameof(GenDeposits.initAssets)),
                prefix: new HarmonyMethod(typeof(AdvancedGeologyModSystem), nameof(BeforeDepositAssets)));
            if (canJewelryEnabled)
            {
                CanJewelryAcquisitionPatch.Apply(harmony, api);
                CanJewelryGemCompatibility.Apply(harmony, api);
            }
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            api.Event.InitWorldGenerator(() => VerifyBlockLayerMappings(api), "standard");

            AdvancedGeologyCommands.Register(api);

            if (api.ModLoader.IsModEnabled("canjewelry"))
            {
                CanJewelryGemCompatibility.ScheduleRuntimeVerification(api);
            }


        }

        public override void Dispose()
        {
            ByproductSystem.Dispose();
            harmony?.UnpatchAll("advancedgeology");
            base.Dispose();
        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            if (api.ModLoader.IsModEnabled("canjewelry"))
            {
                CanJewelryGemCompatibility.InitializeAndReconcile(api);
            }
        }


        private static void BeforeDepositAssets(ICoreServerAPI api) => SuppressLegacyOreDeposits(api);


        /// <summary>
        /// Disables conventional ore deposits while preserving supported gems, rocks, soils,
        /// prospecting registrations, saltpeter, and the custom salt dome.
        /// </summary>
        private static void SuppressLegacyOreDeposits(ICoreAPI api)
        {
            byte[] emptyArray = Encoding.UTF8.GetBytes("[]");
            int suppressedFiles = 0;
            int preservedGemFiles = 0;
            int preservedRockFiles = 0;
            int preservedSoilFiles = 0;
            int preservedProspectingFiles = 0;
            int preservedSaltDomeFiles = 0;
            int suppressedGemFiles = 0;
            int suppressedSoilFiles = 0;
            int rescaledFiles = 0;

            foreach ((AssetLocation location, IAsset asset) in api.Assets.AllAssets.ToArray())
            {
                string path = location.Path.Replace('\\', '/').ToLowerInvariant();
                if (!path.StartsWith("worldgen/deposits/", StringComparison.Ordinal)) continue;
                AppliedDepositData applied = appliedDeposits.GetOrCreateValue(asset);
                if (ReferenceEquals(applied.Data, asset.Data)) continue;
                // Reload replaces Data even when the IAsset instance is reused.
                try
                {

                if (IsSaltDomeDepositAssetPath(location))
                {
                    preservedSaltDomeFiles++;
                    if (ScaleDepositTriesByAbundance(asset)) rescaledFiles++;
                    continue;
                }
                if (!IsConventionalOreDepositAssetPath(path))
                {
                    string relative = path["worldgen/deposits/".Length..];
                    bool isGem = relative.StartsWith("gem/", StringComparison.Ordinal);
                    if (isGem && AdvancedGeologyConfig.Active.IHateGems)
                    {
                        asset.Data = emptyArray;
                        suppressedGemFiles++;
                        continue;
                    }

                    if (isGem)
                    {
                        preservedGemFiles++;
                        if (ScaleDepositTriesByAbundance(asset)) rescaledFiles++;
                    }
                    else if (relative.StartsWith("rock/", StringComparison.Ordinal)) preservedRockFiles++;
                    else if (relative.StartsWith("soil/", StringComparison.Ordinal))
                    {
                        if (AdvancedGeologyConfig.Active.VanillaOresOnly && IsNonVanillaSoilDepositAssetPath(location))
                        {
                            asset.Data = emptyArray;
                            suppressedSoilFiles++;
                            continue;
                        }

                        preservedSoilFiles++;
                        if (ScaleDepositTriesByAbundance(asset)) rescaledFiles++;
                    }
                    else if (relative.StartsWith("prospecting/", StringComparison.Ordinal)) preservedProspectingFiles++;
                    continue;
                }

                asset.Data = emptyArray;
                suppressedFiles++;
                }
                finally
                {
                    applied.Data = asset.Data;
                }
            }

            api.Logger.VerboseDebug(
                "[AdvancedGeology] Disabled {0} conventional ore deposit asset file(s), {1} gem file(s), {2} soil file(s); preserved {3} gem, {4} rock, {5} soil, {6} prospecting, {7} salt dome; rescaled {8} file(s) at abundance {9}",
                suppressedFiles,
                suppressedGemFiles,
                suppressedSoilFiles,
                preservedGemFiles,
                preservedRockFiles,
                preservedSoilFiles,
                preservedProspectingFiles,
                preservedSaltDomeFiles,
                rescaledFiles,
                AdvancedGeologyConfig.Active.GlobalMineralAbundance);
        }

        /// <summary>Scales conventional deposit attempts by the configured abundance.</summary>
        private static bool ScaleDepositTriesByAbundance(IAsset asset)
        {
            string? rescaled = ScaleDepositTriesJson(
                asset.ToText(),
                AdvancedGeologyConfig.Active.GlobalMineralAbundance);
            if (rescaled == null) return false;

            asset.Data = Encoding.UTF8.GetBytes(rescaled);
            return true;
        }

        /// <summary>Scales every <c>triesPerChunk</c>, or returns null when unchanged.</summary>
        public static string? ScaleDepositTriesJson(string json, double abundance)
        {
            if (abundance == 1.0) return null;

            JContainer root;
            try
            {
                if (JToken.Parse(json) is not JContainer parsed) return null;
                root = parsed;
            }
            catch (JsonException)
            {
                return null;
            }

            // Materialize before replacing tokens during traversal.
            JProperty[] tries = root.Descendants()
                .OfType<JProperty>()
                .Where(property =>
                    property.Name.Equals("triesPerChunk", StringComparison.OrdinalIgnoreCase)
                    && property.Value.Type is JTokenType.Float or JTokenType.Integer)
                .ToArray();

            if (tries.Length == 0) return null;

            foreach (JProperty property in tries)
            {
                property.Value = Math.Max(0.0, property.Value.Value<double>() * abundance);
            }

            return root.ToString(Formatting.None);
        }

        public static bool IsSaltDomeDepositAssetPath(AssetLocation location)
        {
            return location.Domain.Equals("advancedgeology", StringComparison.OrdinalIgnoreCase)
                && location.Path.Replace('\\', '/').Equals(
                    "worldgen/deposits/mineralore/halite.json",
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Identifies AdvancedGeology soil deposits excluded by VanillaOresOnly.</summary>
        public static bool IsNonVanillaSoilDepositAssetPath(AssetLocation location)
        {
            return location.Domain.Equals("advancedgeology", StringComparison.OrdinalIgnoreCase)
                && location.Path.Replace('\\', '/').Equals(
                    "worldgen/deposits/soil/kaolinite.json",
                    StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsConventionalOreDepositAssetPath(string path)
        {
            const string prefix = "worldgen/deposits/";
            string normalized = path.Replace('\\', '/').ToLowerInvariant();
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) return false;

            string relative = normalized[prefix.Length..];
            return !relative.Equals("mineralore/saltpeter.json", StringComparison.Ordinal)
                && !relative.StartsWith("gem/", StringComparison.Ordinal)
                && !relative.StartsWith("rock/", StringComparison.Ordinal)
                && !relative.StartsWith("soil/", StringComparison.Ordinal)
                && !relative.StartsWith("prospecting/", StringComparison.Ordinal);
        }


        /// <summary>
        /// Verifies the block layer system has correct rock-to-gravel/sand mappings for our rocks.
        /// </summary>
        private void VerifyBlockLayerMappings(ICoreServerAPI api)
        {
            string[] ourRocks = ["gneiss", "schist", "quartzite", "dolomite", "diorite", "gabbro", "dacite", "rhyolite"];

            BlockLayerConfig config = BlockLayerConfig.GetInstance(api);

            // Check rockstrata variants include our rocks
            HashSet<string> strataRocks = new();
            foreach (var variant in config.RockStrata.Variants)
            {
                strataRocks.Add(variant.BlockCode.Path);
            }
            foreach (string rock in ourRocks)
            {
                string blockcode = "rock-" + rock;
                if (!strataRocks.Contains(blockcode))
                {
                    api.Logger.Warning("[AdvancedGeology] Rock strata missing entry for {0}", blockcode);
                }
            }

            // Check block layer mappings include our rocks
            foreach (var layer in config.Blocklayers)
            {
                if (layer.BlockIdMapping == null) continue;

                foreach (string rock in ourRocks)
                {
                    Block rockBlock = api.World.GetBlock(new AssetLocation("rock-" + rock));
                    if (rockBlock == null)
                    {
                        api.Logger.Warning("[AdvancedGeology] Block 'rock-{0}' not found!", rock);
                        continue;
                    }

                    if (!layer.BlockIdMapping.ContainsKey(rockBlock.BlockId))
                    {
                        api.Logger.Warning("[AdvancedGeology] Block layer '{0}' missing mapping for rock-{1} (blockId={2})",
                            layer.Name, rock, rockBlock.BlockId);
                    }
                }

                // Only check the first {rocktype} layer as they all use the same mapping logic
                break;
            }

            api.Logger.VerboseDebug("[AdvancedGeology] Block layer mapping verification complete");
        }
    }
}
