using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace AdvancedGeology.WorldGen.ProceduralDeposits;

public sealed partial class ProceduralDepositWorldGenSystem : ModSystem
{
    private const int ChunkSize = GlobalConstants.ChunkSize;
    private const ulong ChanceSalt = 0x4348414E4345UL;
    private const ulong XSalt = 0x58434F4F5244UL;
    private const ulong ZSalt = 0x5A434F4F5244UL;
    private const ulong YSalt = 0x59434F4F5244UL;
    private const ulong YawSalt = 0x594157UL;
    private const ulong EdgeSalt = 0x45444745UL;
    private const ulong WeatheringSalt = 0x57454154484552UL;
    private const ulong BloomSalt = 0x424C4F4F4DUL;
    private const ulong LensSalt = 0x4C454E5342444FUL;
    private const ulong AccessorySalt = 0x4143434553534FUL;
    private const ulong GradeSalt = 0x475241444553454CUL;
    private const ulong AlbiteSalt = 0x414C4249544553UL;
    private const ulong SchorlSalt = 0x5343484F524C53UL;
    private const ulong SurfaceNuggetSalt = 0x4E55474745545355UL;
    private const int SplineZoneCount = 13;
    private const int MaximumSoilDepth = 4;
    private const int PlanCacheCapacity = 64;

    private readonly List<LoadedDefinition> loadedDefinitions = new();
    private readonly List<CompiledProceduralDeposit> definitions = new();
    private ICoreServerAPI? serverApi;
    private IWorldGenBlockAccessor? blockAccessor;
    private bool[] proceduralOutputBlocks = Array.Empty<bool>();
    private readonly Dictionary<PlanKey, object> planCache = new(PlanCacheCapacity);
    private readonly Queue<PlanKey> planCacheOrder = new(PlanCacheCapacity);
    private readonly HashSet<ulong> missingTerrainContext = new();
    private readonly Dictionary<int, int> surfaceNuggetBlocks = new();

    public override double ExecuteOrder() => 0.21;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void AssetsFinalize(ICoreAPI api)
    {
        if (api.Side != EnumAppSide.Server) return;

        var server = (ICoreServerAPI)api;
        loadedDefinitions.Clear();
        var seenCodes = new HashSet<string>(StringComparer.Ordinal);

        Dictionary<AssetLocation, ProceduralDepositDefinition[]> assets = server.Assets
            .GetMany<ProceduralDepositDefinition[]>(server.Logger, "worldgen/proceduraldeposits");

        foreach ((AssetLocation location, ProceduralDepositDefinition[] loaded) in assets.OrderBy(entry => entry.Key.ToString()))
        {
            foreach (ProceduralDepositDefinition definition in loaded)
            {
                if (!definition.Enabled) continue;
                if (!ValidateDefinition(server, location, definition)) continue;
                if (!seenCodes.Add(definition.Code))
                {
                    server.Logger.Error(
                        "[AdvancedGeology] Duplicate procedural deposit code {0} in {1}",
                        definition.Code,
                        location);
                    continue;
                }
                loadedDefinitions.Add(new LoadedDefinition(location, definition));
            }
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        serverApi = api;
        api.Event.InitWorldGenerator(() => InitializeWorldDefinitions(api), "standard");
        api.Event.GetWorldgenBlockAccessor(provider => blockAccessor = provider.GetBlockAccessor(false));
        api.Event.MapRegionNeighborsLoaded(GenerateProspectingMaps, "standard");
        api.Event.ChunkColumnGeneration(GenerateChunkColumn, EnumWorldGenPass.TerrainFeatures, "standard");
    }

    private void InitializeWorldDefinitions(ICoreServerAPI api)
    {
        definitions.Clear();
        AdvancedGeology.Byproducts.ByproductSystem.Initialize();
        planCache.Clear();
        planCacheOrder.Clear();
        missingTerrainContext.Clear();
        featureBasePpt.Clear();

        if (loadedDefinitions.Count == 0)
        {
            proceduralOutputBlocks = Array.Empty<bool>();
            return;
        }

        bool vanillaOresOnly = AdvancedGeologyConfig.Active.VanillaOresOnly;
        int nonVanillaSkipped = 0;
        foreach (LoadedDefinition loaded in loadedDefinitions)
        {
            ProceduralDepositDefinition definition = loaded.Definition;
            if (vanillaOresOnly && !DepositContentFilters.IsVanillaProgressionDeposit(definition))
            {
                nonVanillaSkipped++;
                continue;
            }

            if (!ProceduralDepositTemplateRegistry.TryGet(
                definition.Template,
                out IProceduralDepositTemplate template))
            {
                api.Logger.Error(
                    "[AdvancedGeology] Procedural deposit {0} in {1} has unknown template {2}",
                    definition.Code,
                    loaded.Location,
                    definition.Template);
                continue;
            }

            var compiled = new CompiledProceduralDeposit(definition, template, api);
            if (compiled.ByproductValidationError is string byproductValidationError)
            {
                api.Logger.Error("[AdvancedGeology] Procedural deposit {0}: {1}", definition.Code, byproductValidationError);
                continue;
            }
            if (definition.Palette.GossanSurfaceExclusions.Length > 0 && compiled.ExcludedGossanSurfaceCount == 0)
            {
                api.Logger.Warning(
                    "[AdvancedGeology] Procedural deposit {0} declares gossan surface exclusions that match no block",
                    definition.Code);
            }

            string[] missingSlots = template.RequiredMaterialSlots
                .Where(slot => !compiled.HasResolvedSlot(slot))
                .ToArray();
            if (missingSlots.Length > 0)
            {
                api.Logger.Error(
                    "[AdvancedGeology] Procedural deposit {0} has unresolved material slots: {1}. Attempted: {2}",
                    definition.Code,
                    string.Join(", ", missingSlots),
                    DescribeUnresolvedSlots(api, definition, missingSlots));
                continue;
            }
            if (definition.Intrude)
            {
                api.Logger.VerboseDebug(
                    "[AdvancedGeology] Procedural deposit {0} intrudes from {1} source-host block variant(s) through {2} natural-rock block variant(s)",
                    definition.Code,
                    compiled.SourceHostBlockCount,
                    compiled.IntrusionTargetBlockCount);
            }

            // Warn when optional zones would silently disappear.
            string[] unresolvedOptional = definition.Palette.Materials.Keys
                .Where(slot => !compiled.HasResolvedSlot(slot))
                .OrderBy(slot => slot, StringComparer.Ordinal)
                .ToArray();
            if (unresolvedOptional.Length > 0)
            {
                api.Logger.Warning(
                    "[AdvancedGeology] Procedural deposit {0} declares material slots that resolve to no block: {1}",
                    definition.Code,
                    string.Join(", ", unresolvedOptional));
            }
            if (definition.Supergene.Enabled
                && (!compiled.HasResolvedSlot(ProceduralMaterialSlots.Oxide)
                    || !compiled.HasResolvedSlot(ProceduralMaterialSlots.Enriched)))
            {
                api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has incomplete supergene material mappings", definition.Code);
                continue;
            }
            if (definition.Supergene.Enabled
                && definition.Supergene.GossanSoilDepth > 0
                && !compiled.HasResolvedSlot(ProceduralMaterialSlots.Gossan))
            {
                api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has no resolvable gossan material", definition.Code);
                continue;
            }
            if (!AdvancedGeology.Byproducts.ByproductSystem.Register(compiled, out string byproductError))
            {
                api.Logger.Error("[AdvancedGeology] Procedural deposit {0}: {1}", definition.Code, byproductError);
                continue;
            }
            definitions.Add(compiled);
        }
        definitions.Sort((left, right) => string.CompareOrdinal(left.Definition.Code, right.Definition.Code));
        proceduralOutputBlocks = new bool[api.World.Blocks.Count];
        foreach (CompiledProceduralDeposit definition in definitions)
        {
            definition.CollectOutputBlockIds(proceduralOutputBlocks);
        }
        if (definitions.Count > 0)
        {
            api.Logger.VerboseDebug(
                "[AdvancedGeology] Initialized {0} procedural deposit definition(s); skipped {1} non-vanilla definition(s)",
                definitions.Count,
                nonVanillaSkipped);
        }
    }
    private static string DescribeUnresolvedSlots(
        ICoreServerAPI api,
        ProceduralDepositDefinition definition,
        IEnumerable<string> slots)
    {
        string[] grades = { "poor", "medium", "rich", "bountiful" };
        var attempts = new List<string>();
        foreach (string slot in slots)
        {
            if (!definition.Palette.Materials.TryGetValue(slot, out string? pattern))
            {
                attempts.Add($"{slot}=<undeclared>");
                continue;
            }

            string[] rocks = pattern.Contains("{rock}", StringComparison.Ordinal)
                ? definition.Palette.ReplaceableRockVariants
                : new[] { string.Empty };
            string[] requestedGrades = pattern.Contains("{grade}", StringComparison.Ordinal)
                ? grades
                : new[] { string.Empty };
            foreach (string rock in rocks)
            {
                foreach (string grade in requestedGrades)
                {
                    string code = pattern
                        .Replace("{rock}", rock, StringComparison.Ordinal)
                        .Replace("{grade}", grade, StringComparison.Ordinal);
                    bool exists = api.World.GetBlock(new AssetLocation(code))?.Code != null;
                    attempts.Add($"{code}={(exists ? "ok" : "missing")}");
                }
            }
        }
        return string.Join("; ", attempts);
    }


    private void GenerateChunkColumn(IChunkColumnGenerateRequest request)
    {
        if (definitions.Count == 0 || blockAccessor == null || serverApi == null) return;

        blockAccessor.BeginColumn();

        int baseX = request.ChunkX * ChunkSize;
        int baseZ = request.ChunkZ * ChunkSize;
        var candidates = new List<DepositCandidate>(4);

        foreach (CompiledProceduralDeposit definition in definitions)
        {
            CollectCandidates(definition, request, baseX, baseZ, candidates);
        }

        candidates.Sort(static (left, right) => ProceduralDepositMath.CompareDepositOrder(
            left.Compiled.Definition.Priority,
            left.Instance.FeatureId,
            right.Compiled.Definition.Priority,
            right.Instance.FeatureId));
        foreach (DepositCandidate candidate in candidates)
        {
            candidate.Template.Realize(this, candidate, request, baseX, baseZ);
        }

        PlaceSurfaceNuggets(request, baseX, baseZ, candidates);
        foreach (IWorldChunk chunk in request.Chunks)
        {
            AdvancedGeology.Byproducts.ByproductSystem.FlushChunk(chunk);
        }
    }

    private void CollectCandidates(
        CompiledProceduralDeposit compiled,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ,
        List<DepositCandidate> candidates)
    {
        ProceduralDepositDefinition definition = compiled.Definition;
        int reach = compiled.MaximumHorizontalReach;
        int cellSize = definition.Placement.CellSize;
        int minCellX = ProceduralDepositMath.FloorDiv(baseX - reach, cellSize);
        int maxCellX = ProceduralDepositMath.FloorDiv(baseX + ChunkSize - 1 + reach, cellSize);
        int minCellZ = ProceduralDepositMath.FloorDiv(baseZ - reach, cellSize);
        int maxCellZ = ProceduralDepositMath.FloorDiv(baseZ + ChunkSize - 1 + reach, cellSize);

        for (int cellX = minCellX; cellX <= maxCellX; cellX++)
        {
            for (int cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
            {
                ulong featureId = ProceduralDepositMath.FeatureId(
                    serverApi!.World.Seed,
                    compiled.CodeHash,
                    cellX,
                    cellZ);
                if (!TryGetFeatureCenter(definition, featureId, cellX, cellZ, out int centerX, out int centerZ)) continue;
                if (centerX + reach < baseX || centerX - reach >= baseX + ChunkSize ||
                    centerZ + reach < baseZ || centerZ - reach >= baseZ + ChunkSize)
                {
                    continue;
                }

                if (!MatchesClimate(definition, centerX, centerZ)) continue;

                if (!TryCreateInstance(compiled, featureId, centerX, centerZ, out ProceduralDepositInstance instance))
                {
                    continue;
                }

                object? plan = GetOrCreatePlan(compiled, instance);
                if (compiled.Template.RequiresPlan && plan == null) continue;
                candidates.Add(new DepositCandidate(compiled, instance, compiled.Template, plan));
            }
        }
    }


    /// <summary>Caches deterministic plans shared across chunk columns.</summary>
    private object? GetOrCreatePlan(
        CompiledProceduralDeposit compiled,
        in ProceduralDepositInstance instance)
    {
        var key = new PlanKey(instance.FeatureId, instance.CenterY);
        if (planCache.TryGetValue(key, out object? cached)) return cached;

        object? plan = compiled.Template.CreatePlan(instance, compiled.Definition);
        if (plan == null) return null;

        if (planCacheOrder.Count >= PlanCacheCapacity)
        {
            planCache.Remove(planCacheOrder.Dequeue());
        }
        planCache[key] = plan;
        planCacheOrder.Enqueue(key);
        return plan;
    }

    private bool TryCreateInstance(
        CompiledProceduralDeposit compiled,
        ulong featureId,
        int centerX,
        int centerZ,
        out ProceduralDepositInstance instance)
    {
        ProceduralDepositDefinition definition = compiled.Definition;
        int centerY;
        string yMode = definition.Placement.YMode;

        if (string.Equals(yMode, "sourceRock", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryFindSourceY(compiled, centerX, centerZ, out int sourceY))
            {
                instance = default;
                return false;
            }
            centerY = sourceY + definition.Placement.SourceOffset;
        }
        else
        {
            if (!TryGetSurfaceHeight(centerX, centerZ, out int surfaceY)
                || !TryGetSurfaceDepthBand(
                    surfaceY,
                    definition.Placement.MinDepth,
                    definition.Placement.MaxDepth,
                    out int minimumY,
                    out int maximumY))
            {
                instance = default;
                return false;
            }

            if (definition.Intrude)
            {
                if (!TrySelectIntrusionOriginHostY(
                    compiled,
                    featureId ^ YSalt,
                    centerX,
                    centerZ,
                    minimumY,
                    maximumY,
                    out centerY))
                {
                    instance = default;
                    return false;
                }
            }
            else
            {
                centerY = (int)Math.Round(ProceduralDepositMath.Range(
                    featureId ^ YSalt,
                    minimumY,
                    maximumY));
            }
        }

        int worldHeight = serverApi!.World.BlockAccessor.MapSizeY;
        centerY = GameMath.Clamp(centerY, 1, worldHeight - 2);

        ProceduralTerrainPlane plane = default;
        if (definition.TerrainOrientation.Enabled)
        {
            if (!TryFitTerrainPlane(
                centerX,
                centerZ,
                definition.TerrainOrientation.SampleRadius,
                definition.TerrainOrientation.SampleSpacing,
                out plane))
            {
                if (missingTerrainContext.Add(featureId))
                {
                    serverApi!.Logger.Warning(
                        "[AdvancedGeology] Procedural deposit {0} could not sample its complete terrain-orientation footprint for feature {1}; this feature is skipped",
                        definition.Code,
                        featureId);
                }
                instance = default;
                return false;
            }
            plane = ProceduralDepositMath.LimitTerrainPlane(plane, definition.TerrainOrientation);
        }

        double yawDeg = ProceduralDepositMath.Range(
            featureId ^ YawSalt,
            definition.Geometry.YawMinDeg,
            definition.Geometry.YawMaxDeg);
        double yaw = yawDeg * Math.PI / 180.0;
        instance = new ProceduralDepositInstance(
            featureId,
            centerX,
            centerY,
            centerZ,
            Math.Cos(yaw),
            Math.Sin(yaw),
            plane.GradientX,
            plane.GradientZ);
        return true;
    }

    internal static bool TryGetSurfaceDepthBand(
        int surfaceY,
        int minDepth,
        int maxDepth,
        out int minimumY,
        out int maximumY)
    {
        maximumY = surfaceY - minDepth;
        minimumY = Math.Max(1, surfaceY - maxDepth);
        return maximumY >= 1 && minimumY <= maximumY;
    }

    internal static (double ExposureChance, double MaximumProtrudingFraction) GetProtrusionBounds(
        int minDepth,
        int maxDepth,
        int verticalHalfHeight)
    {
        if (maxDepth < minDepth || verticalHalfHeight <= 0) return default;

        int exposedDepthCount = Math.Max(0, Math.Min(maxDepth, verticalHalfHeight - 1) - minDepth + 1);
        double exposureChance = exposedDepthCount / (double)(maxDepth - minDepth + 1);
        double maximumProtrudingFraction = Math.Max(0, verticalHalfHeight - minDepth)
            / (double)(verticalHalfHeight * 2);
        return (exposureChance, maximumProtrudingFraction);
    }


    internal static int GetMaximumVerticalReach(
        ProceduralDepositDefinition definition,
        IProceduralDepositTemplate template)
    {
        return template.Code switch
        {
            "ellipsoid" => (int)Math.Ceiling(definition.Geometry.RadiusY),
            "epithermalVein" => definition.Epithermal.VerticalHalfHeight,
            "sheetedPlate" => definition.SheetedPlate.VerticalHalfHeight,
            "splineNetwork" => definition.Spline.VerticalHalfHeight,
            "ooliticIronstone" => definition.Oolitic.VerticalHalfHeight,
            "greisenStockwork" => definition.Greisen.VerticalHalfHeight,
            "prabornaPyrolusite" => definition.Praborna.VerticalHalfHeight,
            "carbonatiteComplex" => definition.Carbonatite.VerticalHalfHeight,
            "schwazTetrahedrite" => definition.Tetrahedrite.VerticalHalfHeight,
            "metasomaticSiderite" => definition.MetasomaticSiderite.VerticalHalfHeight,
            "bandedIronFormation" => definition.BandedIron.VerticalHalfHeight,
            "mississippiValley" => definition.Mvt.VerticalHalfHeight,
            "bimodalFelsicVms" => definition.BimodalFelsicVms.VerticalHalfHeight,
            "cyprusVms" => definition.CyprusVms.VerticalHalfHeight,
            "besshiVms" => definition.BesshiVms.VerticalHalfHeight,
            "alluvialLignite" => definition.Lignite.VerticalHalfHeight,
            "foldedAnthracite" => definition.Anthracite.VerticalHalfHeight,
            "reopenedArsenideVein" => definition.ArsenideVein.VerticalHalfHeight,
            "cyclothemCoal" => definition.CyclothemCoal.VerticalHalfHeight,
            "stratiformCinnabar" => definition.StratiformCinnabar.VerticalHalfHeight,
            "porphyryTin" => definition.PorphyryTin.VerticalHalfHeight,
            "playaBorate" => definition.PlayaBorate.VerticalHalfHeight,
            "tinSkarn" => definition.TinSkarn.VerticalHalfHeight,
            "volcanicAlunite" => definition.VolcanicAlunite.VerticalHalfHeight,
            "cobaltArsenideVein" => definition.CobaltArsenideVein.VerticalHalfHeight,
            "iocgBreccia" => definition.IocgBreccia.VerticalHalfHeight,
            "sudburyContactNickel" => definition.SudburyNickel.VerticalHalfHeight,
            "lacustrineBorate" => definition.LacustrineBorate.VerticalHalfHeight,
            "lacustrineAlum" => definition.LacustrineBorate.VerticalHalfHeight,
            "porphyryCopperMoly" => definition.PorphyryCopperMoly.VerticalHalfHeight,
            "stratiformCopper" => definition.StratiformCopper.VerticalHalfHeight,
            "massiveNativeCopper" => definition.MassiveNativeCopper.VerticalHalfHeight,
            "veinGraphite" => definition.VeinGraphite.VerticalHalfHeight,
            "flakeGraphiteSchist" => definition.FlakeGraphiteSchist.VerticalHalfHeight,
            "unconformityUranium" => definition.UnconformityUranium.VerticalHalfHeight,
            "sparryMagnesiteReplacement" => definition.SparryMagnesite.VerticalHalfHeight,
            "cryptocrystallineMagnesite" => definition.CryptocrystallineMagnesite.VerticalHalfHeight,
            "peralkalineHreeComplex" => definition.PeralkalineHree.VerticalHalfHeight,
            "messinianSulfur" => definition.MessinianSulfur.VerticalHalfHeight,
            "orogenicQuartzGold" => definition.OrogenicQuartzGold.VerticalHalfHeight,
            "stratiformChromitite" => definition.StratiformChromitite.VerticalHalfHeight,
            "marinePhosphorite" => definition.MarinePhosphorite.VerticalHalfHeight,
            "lapisLazuliMarble" => definition.LapisLazuliMarble.VerticalHalfHeight,
            "ivittuutCryolite" => definition.IvittuutCryolite.VerticalHalfHeight,
            "anorthositeIlmenite" => definition.AnorthositeIlmenite.VerticalHalfHeight,
            _ => 0
        };
    }

    internal void RealizeEllipsoidCandidate(
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        CompiledProceduralDeposit compiled = candidate.Compiled;
        ProceduralDepositDefinition definition = compiled.Definition;
        ProceduralDepositInstance instance = candidate.Instance;
        EllipsoidGeometryDefinition geometry = definition.Geometry;
        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;
        int worldHeight = serverApi!.World.BlockAccessor.MapSizeY;
        int primarySlot = compiled.GetSlotId(ProceduralMaterialSlots.Primary);
        int oxideSlot = compiled.GetSlotId(ProceduralMaterialSlots.Oxide);
        int enrichedSlot = compiled.GetSlotId(ProceduralMaterialSlots.Enriched);

        for (int localX = 0; localX < ChunkSize; localX++)
        {
            int worldX = baseX + localX;
            for (int localZ = 0; localZ < ChunkSize; localZ++)
            {
                int worldZ = baseZ + localZ;
                int surfaceY = heightMap[localZ * ChunkSize + localX];
                ProceduralColumnSample column = ProceduralDepositMath.SampleColumn(instance, geometry, worldX, worldZ);

                if (column.Intersects)
                {
                    int minY = GameMath.Clamp((int)Math.Floor(column.BaseY), 1, worldHeight - 2);
                    int maxY = GameMath.Clamp((int)Math.Ceiling(Math.Min(column.RoofY, surfaceY)), 1, worldHeight - 2);
                    int oxidationDepth = GetOxidationDepth(definition, instance.FeatureId, worldX, worldZ);

                    for (int y = minY; y <= maxY; y++)
                    {
                        double metric = ProceduralDepositMath.EllipsoidMetric(instance, geometry, worldX, y, worldZ);
                        double edge = 1.0 + (ProceduralDepositMath.CoordinateNoise(instance.FeatureId, worldX, y, worldZ, EdgeSalt) - 0.5)
                            * geometry.EdgeNoise;
                        if (metric > edge) continue;

                        int chunkY = y / ChunkSize;
                        int localY = y % ChunkSize;
                        int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
                        IChunkBlocks data = request.Chunks[chunkY].Data;
                        int hostBlockId = data.GetBlockIdUnsafe(index3d);
                        if (!CanReplaceWithProceduralRock(compiled, hostBlockId)) continue;
                        EllipsoidMaterialZone zone = definition.Supergene.Enabled
                            ? ProceduralDepositMath.SelectMaterialZone(
                                surfaceY,
                                y,
                                oxidationDepth,
                                definition.Supergene.EnrichmentThickness)
                            : EllipsoidMaterialZone.Primary;
                        int slotId = zone switch
                        {
                            EllipsoidMaterialZone.Oxide => oxideSlot,
                            EllipsoidMaterialZone.Enriched => enrichedSlot,
                            _ => primarySlot
                        };
                        int placeBlockId = compiled.ResolveBlock(slotId, hostBlockId);
                        if (placeBlockId == 0) continue;

                        data.SetBlockUnsafe(index3d, placeBlockId);
                        AdvancedGeology.Byproducts.ByproductSystem.RecordPlacement(request.Chunks[chunkY], index3d, placeBlockId, instance.FeatureId, compiled);
                        data.SetFluid(index3d, 0);
                    }
                }

                TryPlaceGossan(compiled, instance, request, localX, localZ, worldX, worldZ, surfaceY, column);
            }
        }
    }

    internal void RealizeEpithermalCandidate(
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        CompiledProceduralDeposit compiled = candidate.Compiled;
        ProceduralDepositDefinition definition = compiled.Definition;
        EpithermalVeinDefinition settings = definition.Epithermal;
        EpithermalVeinPlan plan = candidate.EpithermalPlan!;
        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;
        int worldHeight = serverApi!.World.BlockAccessor.MapSizeY;
        int minimumY = GameMath.Clamp(
            candidate.Instance.CenterY - settings.VerticalHalfHeight,
            1,
            worldHeight - 2);
        int maximumPrototypeY = GameMath.Clamp(
            candidate.Instance.CenterY + settings.VerticalHalfHeight,
            1,
            worldHeight - 2);
        int quartzSlot = compiled.GetSlotId(ProceduralMaterialSlots.Quartz);
        int acanthiteSlot = compiled.GetSlotId(ProceduralMaterialSlots.Acanthite);
        int electrumSlot = compiled.GetSlotId(ProceduralMaterialSlots.Electrum);
        int nativeSilverSlot = compiled.GetSlotId(ProceduralMaterialSlots.NativeSilver);

        for (int localX = 0; localX < ChunkSize; localX++)
        {
            int worldX = baseX + localX;
            for (int localZ = 0; localZ < ChunkSize; localZ++)
            {
                int worldZ = baseZ + localZ;
                int surfaceY = heightMap[localZ * ChunkSize + localX];
                int maximumY = Math.Min(surfaceY, maximumPrototypeY);
                EpithermalVeinSample surfaceSample = plan.Sample(worldX, surfaceY, worldZ);
                bool surfaceExposed = surfaceSample.Inside
                    && ColumnHasReplaceableHost(
                        compiled,
                        request,
                        localX,
                        localZ,
                        surfaceY,
                        settings.GeyseriteCapMax + MaximumSoilDepth);
                int capThickness = surfaceExposed
                    ? plan.GetGeyseriteCapThickness(worldX, worldZ, settings)
                    : 0;

                for (int y = minimumY; y <= maximumY; y++)
                {
                    EpithermalVeinSample sample = plan.Sample(worldX, y, worldZ);
                    if (!sample.Inside) continue;

                    int depth = surfaceY - y;
                    if (surfaceExposed && depth <= capThickness) continue;

                    int chunkY = y / ChunkSize;
                    int localY = y % ChunkSize;
                    int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
                    IChunkBlocks data = request.Chunks[chunkY].Data;
                    int hostBlockId = data.GetBlockIdUnsafe(index3d);
                    if (!CanReplaceWithProceduralRock(compiled, hostBlockId)) continue;
                    EpithermalMaterialZone zone = SelectEpithermalZone(
                        candidate.Instance.FeatureId,
                        settings,
                        sample,
                        worldX,
                        y,
                        worldZ,
                        depth,
                        out bool geyseritePipe);

                    if (geyseritePipe)
                    {
                        if (compiled.GeyseriteBlockId == 0
                            || !CanReplaceWithProceduralRock(compiled, hostBlockId)) continue;
                        data.SetBlockUnsafe(index3d, compiled.GeyseriteBlockId);
                        AdvancedGeology.Byproducts.ByproductSystem.RecordPlacement(request.Chunks[chunkY], index3d, compiled.GeyseriteBlockId, candidate.Instance.FeatureId, compiled);
                        data.SetFluid(index3d, 0);
                        continue;
                    }

                    int slotId = zone switch
                    {
                        EpithermalMaterialZone.Acanthite => acanthiteSlot,
                        EpithermalMaterialZone.Electrum => electrumSlot,
                        EpithermalMaterialZone.NativeSilver => nativeSilverSlot,
                        _ => quartzSlot
                    };
                    int placeBlockId = compiled.ResolveBlock(slotId, hostBlockId);
                    if (placeBlockId == 0) continue;
                    data.SetBlockUnsafe(index3d, placeBlockId);
                    AdvancedGeology.Byproducts.ByproductSystem.RecordPlacement(request.Chunks[chunkY], index3d, placeBlockId, candidate.Instance.FeatureId, compiled);
                    data.SetFluid(index3d, 0);
                }

                if (surfaceExposed)
                {
                    PlaceEpithermalSurfaceCap(
                        compiled,
                        request,
                        localX,
                        localZ,
                        surfaceY,
                        capThickness);
                }
            }
        }

        PlaceGossanSmears(candidate, request, baseX, baseZ);
    }

    internal void RealizeSheetedPlateCandidate(
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        CompiledProceduralDeposit compiled = candidate.Compiled;
        SheetedPlateDefinition settings = compiled.Definition.SheetedPlate;
        SheetedPlatePlan plan = candidate.SheetedPlatePlan;
        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;
        int worldHeight = serverApi!.World.BlockAccessor.MapSizeY;
        int minimumY = GameMath.Clamp(candidate.Instance.CenterY - settings.VerticalHalfHeight, 1, worldHeight - 2);
        int maximumPrototypeY = GameMath.Clamp(
            candidate.Instance.CenterY + settings.VerticalHalfHeight,
            1,
            worldHeight - 2);

        int[] slots =
        {
            compiled.GetSlotId(ProceduralMaterialSlots.Primary),
            compiled.GetSlotId(ProceduralMaterialSlots.Secondary),
            compiled.GetSlotId(ProceduralMaterialSlots.Gangue),
            compiled.GetSlotId(ProceduralMaterialSlots.RareGangue),
            compiled.GetSlotId(ProceduralMaterialSlots.Albite),
            compiled.GetSlotId(ProceduralMaterialSlots.Quartz),
            compiled.GetSlotId(ProceduralMaterialSlots.Breccia)
        };

        int kaoliniteSlot = compiled.GetSlotId(ProceduralMaterialSlots.Kaolinite);
        int gossanSlot = compiled.GetSlotId(ProceduralMaterialSlots.Gossan);
        for (int localX = 0; localX < ChunkSize; localX++)
        {
            int worldX = baseX + localX;
            for (int localZ = 0; localZ < ChunkSize; localZ++)
            {
                int worldZ = baseZ + localZ;
                int surfaceY = heightMap[localZ * ChunkSize + localX];
                int maximumY = Math.Min(surfaceY, maximumPrototypeY);

                for (int y = minimumY; y <= maximumY; y++)
                {
                    SheetedPlateSample sample = plan.Sample(worldX, y, worldZ);
                    if (!sample.Inside && !sample.InHalo) continue;

                    int chunkY = y / ChunkSize;
                    if ((uint)chunkY >= (uint)request.Chunks.Length) continue;
                    int localY = y % ChunkSize;
                    int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
                    IChunkBlocks data = request.Chunks[chunkY].Data;
                    int hostBlockId = data.GetBlockIdUnsafe(index3d);
                    SheetedPlateZone zone = SelectSheetedPlateZone(
                        candidate.Instance.FeatureId,
                        settings,
                        sample,
                        worldX,
                        y,
                        worldZ);
                    int grade = SelectSheetedPlateGrade(
                        candidate.Instance.FeatureId,
                        sample,
                        worldX,
                        y,
                        worldZ);
                    int depth = GetSurfaceDepth(surfaceY, y);
                    if (compiled.Definition.Weathering.Enabled
                        && zone == SheetedPlateZone.Primary
                        && IsMartitized(
                            candidate.Instance.FeatureId,
                            depth,
                            worldX,
                            y,
                            worldZ))
                    {
                        zone = SheetedPlateZone.Secondary;
                    }
                    int weatheredSlot = GetKirunaWeatheredSlot(
                        compiled.Definition.Weathering,
                        zone,
                        depth,
                        kaoliniteSlot,
                        gossanSlot);
                    if (weatheredSlot >= 0)
                    {
                        if (!CanPlaceWeatheredSoil(compiled, hostBlockId)) continue;
                    }
                    else if (!CanReplaceWithProceduralRock(compiled, hostBlockId))
                    {
                        continue;
                    }

                    int placeBlockId = weatheredSlot >= 0
                        ? compiled.ResolveWeatheredBlock(weatheredSlot, grade, hostBlockId, y < surfaceY)
                        : compiled.ResolveBlock(slots[(int)zone], grade, hostBlockId);
                    if (placeBlockId == 0 && sample.Inside)
                    {
                        placeBlockId = compiled.ResolveBlock(
                            slots[(int)SheetedPlateZone.Primary],
                            grade,
                            hostBlockId);
                    }
                    if (placeBlockId == 0) continue;


                    data.SetBlockUnsafe(index3d, placeBlockId);
                    AdvancedGeology.Byproducts.ByproductSystem.RecordPlacement(request.Chunks[chunkY], index3d, placeBlockId, candidate.Instance.FeatureId, compiled);
                    data.SetFluid(index3d, 0);
                }
            }
        }
        PlaceKirunaGossanSmears(candidate, request, baseX, baseZ);
    }

    private bool HasPlaceableKirunaBodyColumn(
        CompiledProceduralDeposit compiled,
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int worldX,
        int worldZ,
        int surfaceY)
    {
        SheetedPlateDefinition settings = compiled.Definition.SheetedPlate;
        int minimumY = Math.Max(1, candidate.Instance.CenterY - settings.VerticalHalfHeight);
        int maximumY = Math.Min(surfaceY, candidate.Instance.CenterY + settings.VerticalHalfHeight);
        int primarySlot = compiled.GetSlotId(ProceduralMaterialSlots.Primary);
        if (primarySlot < 0) return false;

        for (int y = minimumY; y <= maximumY; y++)
        {
            SheetedPlateSample sample = candidate.SheetedPlatePlan.Sample(worldX, y, worldZ);
            if (!sample.Inside) continue;
            int chunkY = y / ChunkSize;
            if ((uint)chunkY >= (uint)request.Chunks.Length) continue;
            int localY = y % ChunkSize;
            int localX = ProceduralDepositMath.FloorMod(worldX, ChunkSize);
            int localZ = ProceduralDepositMath.FloorMod(worldZ, ChunkSize);
            int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
            int hostBlockId = request.Chunks[chunkY].Data.GetBlockIdUnsafe(index3d);
            if (!CanReplaceWithProceduralRock(compiled, hostBlockId)) continue;
            int placeBlockId = compiled.ResolveBlock(primarySlot, SelectSheetedPlateGrade(
                candidate.Instance.FeatureId,
                sample,
                worldX,
                y,
                worldZ),
                hostBlockId);
            if (placeBlockId != 0 && placeBlockId != hostBlockId) return true;
        }
        return false;
    }
    private void PlaceKirunaGossanSmears(
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        CompiledProceduralDeposit compiled = candidate.Compiled;
        SurfaceWeatheringDefinition weathering = compiled.Definition.Weathering;
        if (!weathering.Enabled
            || compiled.GossanBlockId == 0
            || weathering.GossanSmearSeedFraction <= 0)
        {
            return;
        }

        int maximumHeight = weathering.GossanSmearHeightMax;
        if (maximumHeight <= 0) return;
        SheetedPlatePlan plan = candidate.SheetedPlatePlan;
        ulong featureId = candidate.Instance.FeatureId;
        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;

        for (int seedX = baseX - maximumHeight; seedX < baseX + ChunkSize + maximumHeight; seedX++)
        {
            for (int seedZ = baseZ - maximumHeight; seedZ < baseZ + ChunkSize + maximumHeight; seedZ++)
            {
                if (!ProceduralDepositMath.IsSmearSeed(
                    featureId,
                    seedX,
                    seedZ,
                    weathering.GossanSmearSeedFraction))
                {
                    continue;
                }
                if (!TryGetSurfaceHeight(seedX, seedZ, out int seedSurfaceY)) continue;

                SheetedPlateSample sample = plan.Sample(seedX, seedSurfaceY, seedZ);
                if (!sample.Inside
                    || SelectSheetedPlateZone(
                        featureId,
                        compiled.Definition.SheetedPlate,
                        sample,
                        seedX,
                        seedSurfaceY,
                        seedZ) != SheetedPlateZone.Primary)
                {
                    continue;
                }
                if (!HasPlaceableKirunaBodyColumn(
                    compiled,
                    candidate,
                    request,
                    seedX,
                    seedZ,
                    seedSurfaceY))
                {
                    continue;
                }

                int height = ProceduralDepositMath.SmearHeight(
                    featureId,
                    seedX,
                    seedZ,
                    weathering.GossanSmearHeightMin,
                    maximumHeight);
                int apexY = seedSurfaceY + 1;
                for (int offsetX = -height; offsetX <= height; offsetX++)
                {
                    int worldX = seedX + offsetX;
                    int localX = worldX - baseX;
                    if (localX < 0 || localX >= ChunkSize) continue;

                    for (int offsetZ = -height; offsetZ <= height; offsetZ++)
                    {
                        int worldZ = seedZ + offsetZ;
                        int localZ = worldZ - baseZ;
                        if (localZ < 0 || localZ >= ChunkSize) continue;

                        int targetSurfaceY = heightMap[localZ * ChunkSize + localX];
                        int radius = Math.Max(Math.Abs(offsetX), Math.Abs(offsetZ));
                        if (!ProceduralDepositMath.SmearCovers(apexY, height, radius, targetSurfaceY)) continue;
                        PlaceSmearedGossanSoil(
                            compiled,
                            request,
                            localX,
                            localZ,
                            targetSurfaceY);
                    }
                }
            }
        }

    }

    internal void RealizeCarbonatiteComplexCandidate(
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        CompiledProceduralDeposit compiled = candidate.Compiled;
        CarbonatiteComplexDefinition settings = compiled.Definition.Carbonatite;
        CarbonatiteComplexPlan plan = candidate.CarbonatitePlan;
        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;
        int worldHeight = serverApi!.World.BlockAccessor.MapSizeY;
        int minimumY = GameMath.Clamp(candidate.Instance.CenterY - settings.VerticalHalfHeight, 1, worldHeight - 2);
        int maximumPrototypeY = GameMath.Clamp(candidate.Instance.CenterY + settings.VerticalHalfHeight, 1, worldHeight - 2);
        int[] slots = new int[Enum.GetValues<CarbonatiteMaterialZone>().Length];
        Array.Fill(slots, -1);
        slots[(int)CarbonatiteMaterialZone.CarbonatiteMatrix] = compiled.GetSlotId(ProceduralMaterialSlots.Carbonatite);
        slots[(int)CarbonatiteMaterialZone.Pyrochlore] = compiled.GetSlotId(ProceduralMaterialSlots.Pyrochlore);
        slots[(int)CarbonatiteMaterialZone.Monazite] = compiled.GetSlotId(ProceduralMaterialSlots.Monazite);
        slots[(int)CarbonatiteMaterialZone.Bastnasite] = compiled.GetSlotId(ProceduralMaterialSlots.Bastnasite);
        slots[(int)CarbonatiteMaterialZone.Fluorite] = compiled.GetSlotId(ProceduralMaterialSlots.Fluorite);
        slots[(int)CarbonatiteMaterialZone.Apatite] = compiled.GetSlotId(ProceduralMaterialSlots.Apatite);
        slots[(int)CarbonatiteMaterialZone.Magnetite] = compiled.GetSlotId(ProceduralMaterialSlots.Magnetite);
        slots[(int)CarbonatiteMaterialZone.Breccia] = compiled.GetSlotId(ProceduralMaterialSlots.Breccia);

        for (int localX = 0; localX < ChunkSize; localX++)
        {
            int worldX = baseX + localX;
            for (int localZ = 0; localZ < ChunkSize; localZ++)
            {
                int worldZ = baseZ + localZ;
                int maximumY = Math.Min(heightMap[localZ * ChunkSize + localX], maximumPrototypeY);
                for (int y = minimumY; y <= maximumY; y++)
                {
                    CarbonatiteComplexSample sample = plan.Sample(worldX, y, worldZ);
                    if (!sample.Inside || sample.Zone == CarbonatiteMaterialZone.None) continue;
                    int chunkY = y / ChunkSize;
                    if ((uint)chunkY >= (uint)request.Chunks.Length) continue;
                    int localY = y % ChunkSize;
                    int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
                    IChunkBlocks data = request.Chunks[chunkY].Data;
                    int hostBlockId = data.GetBlockIdUnsafe(index3d);
                    if (!CanReplaceWithProceduralRock(compiled, hostBlockId)) continue;
                    int placeBlockId = compiled.ResolveBlock(
                        slots[(int)sample.Zone],
                        SelectCarbonatiteGrade(candidate.Instance.FeatureId, sample, worldX, y, worldZ),
                        hostBlockId);
                    if (placeBlockId == 0) continue;
                    data.SetBlockUnsafe(index3d, placeBlockId);
                    AdvancedGeology.Byproducts.ByproductSystem.RecordPlacement(request.Chunks[chunkY], index3d, placeBlockId, candidate.Instance.FeatureId, compiled);
                    data.SetFluid(index3d, 0);
                }
            }
        }
    }

    internal void RealizeOoliticIronstoneCandidate(
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        CompiledProceduralDeposit compiled = candidate.Compiled;
        OoliticIronstoneDefinition settings = compiled.Definition.Oolitic;
        OoliticIronstonePlan plan = candidate.OoliticIronstonePlan;
        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;
        int worldHeight = serverApi!.World.BlockAccessor.MapSizeY;
        int minimumY = GameMath.Clamp(candidate.Instance.CenterY - settings.VerticalHalfHeight, 1, worldHeight - 2);
        int maximumPrototypeY = GameMath.Clamp(candidate.Instance.CenterY + settings.VerticalHalfHeight, 1, worldHeight - 2);
        int hematiteSlot = compiled.GetSlotId(ProceduralMaterialSlots.Hematite);
        int limoniteSlot = compiled.GetSlotId(ProceduralMaterialSlots.Limonite);
        int sideriteSlot = compiled.GetSlotId(ProceduralMaterialSlots.Siderite);
        int magnetiteSlot = compiled.GetSlotId(ProceduralMaterialSlots.Magnetite);
        int brecciaSlot = compiled.GetSlotId(ProceduralMaterialSlots.Breccia);

        for (int localX = 0; localX < ChunkSize; localX++)
        {
            int worldX = baseX + localX;
            for (int localZ = 0; localZ < ChunkSize; localZ++)
            {
                int worldZ = baseZ + localZ;
                int maximumY = Math.Min(heightMap[localZ * ChunkSize + localX], maximumPrototypeY);

                for (int y = minimumY; y <= maximumY; y++)
                {
                    OoliticIronstoneSample sample = plan.Sample(worldX, y, worldZ);
                    if (!sample.Inside && !sample.Halo) continue;

                    OoliticIronstoneZone zone = SelectOoliticIronstoneZone(
                        candidate.Instance.FeatureId,
                        sample,
                        worldX,
                        y,
                        worldZ);
                    if (zone == OoliticIronstoneZone.None) continue;

                    int chunkY = y / ChunkSize;
                    if ((uint)chunkY >= (uint)request.Chunks.Length) continue;
                    int localY = y % ChunkSize;
                    int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
                    IChunkBlocks data = request.Chunks[chunkY].Data;
                    int hostBlockId = data.GetBlockIdUnsafe(index3d);
                    if (!CanReplaceWithProceduralRock(compiled, hostBlockId)) continue;

                    int grade = SelectOoliticIronstoneGrade(
                        candidate.Instance.FeatureId,
                        sample,
                        worldX,
                        y,
                        worldZ);
                    int slot = zone switch
                    {
                        OoliticIronstoneZone.Hematite => hematiteSlot,
                        OoliticIronstoneZone.Limonite => limoniteSlot,
                        OoliticIronstoneZone.Siderite => sideriteSlot,
                        OoliticIronstoneZone.Magnetite => magnetiteSlot,
                        OoliticIronstoneZone.Breccia => brecciaSlot,
                        _ => -1
                    };
                    int placeBlockId = compiled.ResolveBlock(slot, grade, hostBlockId);
                    if (placeBlockId == 0) continue;

                    data.SetBlockUnsafe(index3d, placeBlockId);
                    AdvancedGeology.Byproducts.ByproductSystem.RecordPlacement(request.Chunks[chunkY], index3d, placeBlockId, candidate.Instance.FeatureId, compiled);
                    data.SetFluid(index3d, 0);
                }
            }
        }
    }

    internal void RealizeGreisenStockworkCandidate(
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        CompiledProceduralDeposit compiled = candidate.Compiled;
        GreisenStockworkDefinition settings = compiled.Definition.Greisen;
        GreisenStockworkPlan plan = candidate.GreisenPlan;
        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;
        int worldHeight = serverApi!.World.BlockAccessor.MapSizeY;
        int minimumY = GameMath.Clamp(candidate.Instance.CenterY - settings.VerticalHalfHeight, 1, worldHeight - 2);
        int maximumPrototypeY = GameMath.Clamp(candidate.Instance.CenterY + settings.VerticalHalfHeight, 1, worldHeight - 2);

        int[] zoneSlots = BuildGreisenZoneSlots(compiled);
        int kaoliniteSlot = compiled.GetSlotId(ProceduralMaterialSlots.Kaolinite);
        int gossanSlot = compiled.GetSlotId(ProceduralMaterialSlots.Gossan);

        for (int localX = 0; localX < ChunkSize; localX++)
        {
            int worldX = baseX + localX;
            for (int localZ = 0; localZ < ChunkSize; localZ++)
            {
                int worldZ = baseZ + localZ;
                int surfaceY = heightMap[localZ * ChunkSize + localX];
                int maximumY = Math.Min(surfaceY, maximumPrototypeY);

                for (int y = minimumY; y <= maximumY; y++)
                {
                    GreisenStockworkZone zone = plan.EvaluateZone(worldX, y, worldZ, surfaceY);
                    if (zone == GreisenStockworkZone.None) continue;

                    int chunkY = y / ChunkSize;
                    if ((uint)chunkY >= (uint)request.Chunks.Length) continue;
                    int localY = y % ChunkSize;
                    int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
                    IChunkBlocks data = request.Chunks[chunkY].Data;
                    int hostBlockId = data.GetBlockIdUnsafe(index3d);

                    int depth = GetSurfaceDepth(surfaceY, y);
                    int targetSlot = -1;

                    if (compiled.Definition.Weathering.Enabled && depth <= settings.WeatheringDepthMax)
                    {
                        if (depth <= 1)
                        {
                            targetSlot = gossanSlot >= 0 ? gossanSlot : zoneSlots[(int)zone];
                        }
                        else if (zone != GreisenStockworkZone.Quartz)
                        {
                            targetSlot = kaoliniteSlot >= 0 ? kaoliniteSlot : zoneSlots[(int)zone];
                        }
                        else
                        {
                            targetSlot = zoneSlots[(int)zone];
                        }
                    }
                    else
                    {
                        targetSlot = zoneSlots[(int)zone];
                    }

                    if (targetSlot < 0) continue;

                    bool isWeatheredSoil = targetSlot == gossanSlot || targetSlot == kaoliniteSlot;
                    if (isWeatheredSoil)
                    {
                        if (!CanPlaceWeatheredSoil(compiled, hostBlockId)) continue;
                    }
                    else if (!CanReplaceWithProceduralRock(compiled, hostBlockId))
                    {
                        continue;
                    }

                    int grade = SelectGreisenGrade(
                        candidate.Instance.FeatureId,
                        plan.Sample(worldX, y, worldZ),
                        worldX,
                        y,
                        worldZ);
                    int placeBlockId = isWeatheredSoil
                        ? compiled.ResolveWeatheredBlock(targetSlot, grade, hostBlockId, y < surfaceY)
                        : compiled.ResolveBlock(targetSlot, grade, hostBlockId);
                    if (placeBlockId == 0) continue;

                    data.SetBlockUnsafe(index3d, placeBlockId);
                    AdvancedGeology.Byproducts.ByproductSystem.RecordPlacement(request.Chunks[chunkY], index3d, placeBlockId, candidate.Instance.FeatureId, compiled);
                    data.SetFluid(index3d, 0);
                }
            }
        }
    }

    private static int[] BuildGreisenZoneSlots(CompiledProceduralDeposit compiled)
    {
        var slots = new int[Enum.GetValues<GreisenStockworkZone>().Length];
        Array.Fill(slots, -1);
        slots[(int)GreisenStockworkZone.Cassiterite] = compiled.GetSlotId(ProceduralMaterialSlots.Cassiterite);
        slots[(int)GreisenStockworkZone.Wolframite] = compiled.GetSlotId(ProceduralMaterialSlots.Wolframite);
        slots[(int)GreisenStockworkZone.Chalcopyrite] = compiled.GetSlotId(ProceduralMaterialSlots.Chalcopyrite);
        slots[(int)GreisenStockworkZone.Arsenopyrite] = compiled.GetSlotId(ProceduralMaterialSlots.Arsenopyrite);
        slots[(int)GreisenStockworkZone.Sphalerite] = compiled.GetSlotId(ProceduralMaterialSlots.Sphalerite);
        slots[(int)GreisenStockworkZone.Galena] = compiled.GetSlotId(ProceduralMaterialSlots.Galena);
        slots[(int)GreisenStockworkZone.Molybdenite] = compiled.GetSlotId(ProceduralMaterialSlots.Molybdenite);
        slots[(int)GreisenStockworkZone.Bismuthinite] = compiled.GetSlotId(ProceduralMaterialSlots.Bismuthinite);
        slots[(int)GreisenStockworkZone.NativeBismuth] = compiled.GetSlotId(ProceduralMaterialSlots.NativeBismuth);
        slots[(int)GreisenStockworkZone.Pyrite] = compiled.GetSlotId(ProceduralMaterialSlots.Pyrite);
        slots[(int)GreisenStockworkZone.Columbite] = compiled.GetSlotId(ProceduralMaterialSlots.Columbite);
        slots[(int)GreisenStockworkZone.Tantalite] = compiled.GetSlotId(ProceduralMaterialSlots.Tantalite);
        int microliteSlot = compiled.GetSlotId(ProceduralMaterialSlots.Microlite);
        if (microliteSlot < 0) microliteSlot = compiled.GetSlotId(ProceduralMaterialSlots.Pyrochlore);
        slots[(int)GreisenStockworkZone.Microlite] = microliteSlot;
        slots[(int)GreisenStockworkZone.Lepidolite] = compiled.GetSlotId(ProceduralMaterialSlots.Lepidolite);
        slots[(int)GreisenStockworkZone.Spodumene] = compiled.GetSlotId(ProceduralMaterialSlots.Spodumene);
        slots[(int)GreisenStockworkZone.Pollucite] = compiled.GetSlotId(ProceduralMaterialSlots.Pollucite);
        slots[(int)GreisenStockworkZone.Beryl] = compiled.GetSlotId(ProceduralMaterialSlots.Beryl);
        slots[(int)GreisenStockworkZone.Topaz] = compiled.GetSlotId(ProceduralMaterialSlots.Topaz);
        int tourmalineSlot = compiled.GetSlotId(ProceduralMaterialSlots.Tourmaline);
        if (tourmalineSlot < 0) tourmalineSlot = compiled.GetSlotId(ProceduralMaterialSlots.Schorl);
        slots[(int)GreisenStockworkZone.Tourmaline] = tourmalineSlot;
        slots[(int)GreisenStockworkZone.Fluorite] = compiled.GetSlotId(ProceduralMaterialSlots.Fluorite);
        slots[(int)GreisenStockworkZone.Albite] = compiled.GetSlotId(ProceduralMaterialSlots.Albite);
        slots[(int)GreisenStockworkZone.Quartz] = compiled.GetSlotId(ProceduralMaterialSlots.Quartz);
        slots[(int)GreisenStockworkZone.Breccia] = compiled.GetSlotId(ProceduralMaterialSlots.Breccia);
        slots[(int)GreisenStockworkZone.Gossan] = compiled.GetSlotId(ProceduralMaterialSlots.Gossan);
        slots[(int)GreisenStockworkZone.Kaolinite] = compiled.GetSlotId(ProceduralMaterialSlots.Kaolinite);
        return slots;
    }

    internal void RealizePrabornaPyrolusiteCandidate(
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        CompiledProceduralDeposit compiled = candidate.Compiled;
        PrabornaPyrolusiteDefinition settings = compiled.Definition.Praborna;
        PrabornaPyrolusitePlan plan = candidate.PrabornaPlan;
        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;
        int worldHeight = serverApi!.World.BlockAccessor.MapSizeY;
        int minimumY = GameMath.Clamp(candidate.Instance.CenterY - settings.VerticalHalfHeight, 1, worldHeight - 2);
        int maximumPrototypeY = GameMath.Clamp(candidate.Instance.CenterY + settings.VerticalHalfHeight, 1, worldHeight - 2);

        int[] zoneSlots = BuildPrabornaZoneSlots(compiled);
        int blackGossanSlot = compiled.GetSlotId(ProceduralMaterialSlots.BlackGossan);
        int pyrolusiteSlot = compiled.GetSlotId(ProceduralMaterialSlots.Pyrolusite);

        for (int localX = 0; localX < ChunkSize; localX++)
        {
            int worldX = baseX + localX;
            for (int localZ = 0; localZ < ChunkSize; localZ++)
            {
                int worldZ = baseZ + localZ;
                int surfaceY = heightMap[localZ * ChunkSize + localX];
                int maximumY = Math.Min(surfaceY, maximumPrototypeY);

                for (int y = minimumY; y <= maximumY; y++)
                {
                    PrabornaPyrolusiteZone zone = plan.EvaluateZone(worldX, y, worldZ, surfaceY);
                    if (zone == PrabornaPyrolusiteZone.None) continue;

                    int chunkY = y / ChunkSize;
                    if ((uint)chunkY >= (uint)request.Chunks.Length) continue;
                    int localY = y % ChunkSize;
                    int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
                    IChunkBlocks data = request.Chunks[chunkY].Data;
                    int hostBlockId = data.GetBlockIdUnsafe(index3d);

                    int depth = GetSurfaceDepth(surfaceY, y);
                    int targetSlot = -1;

                    if (compiled.Definition.Weathering.Enabled && depth <= settings.WeatheringDepthMax)
                    {
                        if (depth <= 1)
                        {
                            targetSlot = blackGossanSlot >= 0 ? blackGossanSlot : pyrolusiteSlot;
                        }
                        else if (zone != PrabornaPyrolusiteZone.Quartz)
                        {
                            targetSlot = pyrolusiteSlot;
                        }
                        else
                        {
                            targetSlot = zoneSlots[(int)zone];
                        }
                    }
                    else
                    {
                        targetSlot = zoneSlots[(int)zone];
                    }

                    if (targetSlot < 0) continue;

                    bool isSoil = targetSlot == blackGossanSlot;
                    if (isSoil)
                    {
                        if (!CanPlaceWeatheredSoil(compiled, hostBlockId)) continue;
                    }
                    else if (!CanReplaceWithProceduralRock(compiled, hostBlockId))
                    {
                        continue;
                    }

                    int grade = SelectPrabornaGrade(
                        candidate.Instance.FeatureId,
                        plan.Sample(worldX, y, worldZ),
                        worldX,
                        y,
                        worldZ);
                    int placeBlockId = isSoil
                        ? compiled.ResolveWeatheredBlock(targetSlot, grade, hostBlockId, y < surfaceY)
                        : compiled.ResolveBlock(targetSlot, grade, hostBlockId);
                    if (placeBlockId == 0) continue;

                    data.SetBlockUnsafe(index3d, placeBlockId);
                    AdvancedGeology.Byproducts.ByproductSystem.RecordPlacement(request.Chunks[chunkY], index3d, placeBlockId, candidate.Instance.FeatureId, compiled);
                    data.SetFluid(index3d, 0);
                }
            }
        }
    }

    private static int[] BuildPrabornaZoneSlots(CompiledProceduralDeposit compiled)
    {
        var slots = new int[Enum.GetValues<PrabornaPyrolusiteZone>().Length];
        Array.Fill(slots, -1);
        slots[(int)PrabornaPyrolusiteZone.Pyrolusite] = compiled.GetSlotId(ProceduralMaterialSlots.Pyrolusite);
        slots[(int)PrabornaPyrolusiteZone.Braunite] = compiled.GetSlotId(ProceduralMaterialSlots.Braunite);
        slots[(int)PrabornaPyrolusiteZone.Rhodochrosite] = compiled.GetSlotId(ProceduralMaterialSlots.Rhodochrosite);
        slots[(int)PrabornaPyrolusiteZone.Spessartine] = compiled.GetSlotId(ProceduralMaterialSlots.Spessartine);
        slots[(int)PrabornaPyrolusiteZone.Quartz] = compiled.GetSlotId(ProceduralMaterialSlots.Quartz);
        slots[(int)PrabornaPyrolusiteZone.Hematite] = compiled.GetSlotId(ProceduralMaterialSlots.Hematite);
        slots[(int)PrabornaPyrolusiteZone.Breccia] = compiled.GetSlotId(ProceduralMaterialSlots.Breccia);
        slots[(int)PrabornaPyrolusiteZone.BlackGossan] = compiled.GetSlotId(ProceduralMaterialSlots.BlackGossan);
        return slots;
    }

    internal static OoliticIronstoneZone SelectOoliticIronstoneZone(
        ulong featureId,
        in OoliticIronstoneSample sample,
        int worldX,
        int worldY,
        int worldZ)
    {
        double grain = ProceduralDepositMath.CoordinateNoise(
            featureId,
            worldX,
            worldY,
            worldZ,
            0x4F4F4C495447524EUL);
        if (sample.Inside)
        {
            if (sample.IronPotential > 0.80)
            {
                if (grain > 0.82) return OoliticIronstoneZone.Hematite;
                if (grain > 0.36) return OoliticIronstoneZone.Limonite; // goethite/chamosite
                if (grain > 0.18) return OoliticIronstoneZone.Magnetite;
                return OoliticIronstoneZone.None;
            }
            if (sample.IronPotential > 0.64)
            {
                if (grain > 0.48) return OoliticIronstoneZone.Limonite; // goethite/chamosite
                if (grain > 0.25) return OoliticIronstoneZone.Siderite;
                return OoliticIronstoneZone.None;
            }
            if (grain > 0.82) return OoliticIronstoneZone.Hematite;
            if (grain > 0.48) return OoliticIronstoneZone.Siderite;
            return OoliticIronstoneZone.None;
        }

        return sample.ChannelField > 0.48 && grain > 0.72
            ? OoliticIronstoneZone.Breccia
            : OoliticIronstoneZone.None;
    }

    internal static int SelectOoliticIronstoneGrade(
        ulong featureId,
        in OoliticIronstoneSample sample,
        int worldX,
        int worldY,
        int worldZ)
    {
        double patch = ProceduralDepositMath.SmoothNoise2D(
            featureId,
            worldX / 9.0,
            worldZ / 9.0,
            GradeSalt);
        double grain = ProceduralDepositMath.CoordinateNoise(
            featureId,
            worldX / 6,
            worldY / 6,
            worldZ / 6,
            GradeSalt);
        double richness = 0.55 * sample.IronPotential + 0.30 * patch + 0.15 * grain;
        if (richness < 0.42) return 0;
        if (richness < 0.64) return 1;
        if (richness < 0.84) return 2;
        return 3;
    }

    /// <summary>
    /// Ore quality inside a greisen/lode system: richest in vein cores near the cupola roof,
    /// leaner in the halo and in distal crosscourses.
    /// </summary>
    internal static int SelectGreisenGrade(
        ulong featureId,
        in GreisenStockworkSample sample,
        int worldX,
        int worldY,
        int worldZ)
    {
        double patch = ProceduralDepositMath.SmoothNoise2D(
            featureId,
            worldX / 10.0,
            worldZ / 10.0,
            GradeSalt);
        double grain = ProceduralDepositMath.CoordinateNoise(
            featureId,
            worldX / 6,
            worldY / 6,
            worldZ / 6,
            GradeSalt);
        double centrality = 1.0 - Math.Clamp(sample.CenterRatio, 0.0, 1.0);
        double stacked = Math.Clamp((sample.InsideVeinCount - 1) / 2.0, 0.0, 1.0);
        double richness = 0.34 * patch + 0.16 * grain + 0.32 * centrality + 0.18 * stacked;
        if (richness < 0.34) return 0;
        if (richness < 0.52) return 1;
        if (richness < 0.70) return 2;
        return 3;
    }

    /// <summary>
    /// Ore quality in a carbonatite complex: richest in the feeder core and the cap veins,
    /// leaner outward through the ring dykes and sill.
    /// </summary>
    internal static int SelectCarbonatiteGrade(
        ulong featureId,
        in CarbonatiteComplexSample sample,
        int worldX,
        int worldY,
        int worldZ)
    {
        double patch = ProceduralDepositMath.SmoothNoise2D(
            featureId,
            worldX / 12.0,
            worldZ / 12.0,
            GradeSalt);
        double grain = ProceduralDepositMath.CoordinateNoise(
            featureId,
            worldX / 5,
            worldY / 5,
            worldZ / 5,
            GradeSalt);
        double centrality = 1.0 - Math.Clamp(sample.Radial, 0.0, 1.0);
        double structural = sample.IsCapVein ? 0.85 : (sample.IsRing || sample.IsDyke ? 0.45 : 0.30);
        double richness = 0.32 * patch + 0.16 * grain + 0.32 * centrality + 0.20 * structural;
        if (richness < 0.34) return 0;
        if (richness < 0.52) return 1;
        if (richness < 0.70) return 2;
        return 3;
    }

    /// <summary>
    /// Ore quality in a boudinaged metachert Mn deposit: richest in lens cores and the sigmoidal
    /// veins that drain them, leanest in the thin stratiform band.
    /// </summary>
    internal static int SelectPrabornaGrade(
        ulong featureId,
        in PrabornaPyrolusiteSample sample,
        int worldX,
        int worldY,
        int worldZ)
    {
        double patch = ProceduralDepositMath.SmoothNoise2D(
            featureId,
            worldX / 9.0,
            worldZ / 9.0,
            GradeSalt);
        double grain = ProceduralDepositMath.CoordinateNoise(
            featureId,
            worldX / 5,
            worldY / 5,
            worldZ / 5,
            GradeSalt);
        double centrality = 1.0 - Math.Clamp(sample.BestLensRadial, 0.0, 1.0);
        double structural = sample.InsideVein ? 0.75 : (sample.InStratiformBand ? 0.25 : 0.45);
        double richness = 0.32 * patch + 0.16 * grain + 0.34 * centrality + 0.18 * structural;
        if (richness < 0.34) return 0;
        if (richness < 0.52) return 1;
        if (richness < 0.70) return 2;
        return 3;
    }

    /// <summary>
    /// Ore quality in a metasomatic carbonate replacement body: richest in body cores and feeder
    /// veins, leaner in the alteration halo.
    /// </summary>
    internal static int SelectMetasomaticSideriteGrade(
        ulong featureId,
        in MetasomaticSideriteSample sample,
        int worldX,
        int worldY,
        int worldZ)
    {
        double patch = ProceduralDepositMath.SmoothNoise2D(
            featureId,
            worldX / 11.0,
            worldZ / 11.0,
            GradeSalt);
        double grain = ProceduralDepositMath.CoordinateNoise(
            featureId,
            worldX / 6,
            worldY / 6,
            worldZ / 6,
            GradeSalt);
        // Feeder veins are measured from their own centreline; replacement bodies from the body core.
        double centrality = sample.InsideVein
            ? 1.0 - Math.Clamp(sample.BestVeinScore, 0.0, 1.0)
            : 1.0 - Math.Clamp(sample.BestBodyRadial, 0.0, 1.0);
        double structural = sample.InsideVein ? 0.85 : (sample.InHalo ? 0.20 : 0.60);
        double richness = 0.32 * patch + 0.16 * grain + 0.34 * centrality + 0.18 * structural;
        if (richness < 0.34) return 0;
        if (richness < 0.52) return 1;
        if (richness < 0.70) return 2;
        return 3;
    }

    internal static SheetedPlateZone SelectSheetedPlateZone(
        ulong featureId,
        SheetedPlateDefinition settings,
        in SheetedPlateSample sample,
        int worldX,
        int worldY,
        int worldZ)
    {
        double grain = ProceduralDepositMath.CoordinateNoise(
            featureId,
            worldX,
            worldY,
            worldZ,
            0x534845455447524EUL);
        return SelectSheetedPlateZoneCore(settings, sample, grain, featureId, worldX, worldZ);
    }


    /// <summary>
    /// Ore quality across a massive sheet: richest along the sheet centre, patchy along strike.
    /// </summary>
    internal static int SelectSheetedPlateGrade(
        ulong featureId,
        in SheetedPlateSample sample,
        int worldX,
        int worldY,
        int worldZ)
    {
        double patch = ProceduralDepositMath.SmoothNoise2D(
            featureId,
            worldX / 11.0,
            worldZ / 11.0,
            GradeSalt);
        double vertical = ProceduralDepositMath.CoordinateNoise(
            featureId,
            worldX / 7,
            worldY / 7,
            worldZ / 7,
            GradeSalt);
        double centrality = 1.0 - Math.Clamp(sample.CenterRatio, 0, 1);
        double richness = 0.40 * patch + 0.18 * vertical + 0.42 * centrality;
        if (richness < 0.34) return 0;
        if (richness < 0.60) return 1;
        if (richness < 0.84) return 2;
        return 3;
    }

    internal static int GetKirunaWeatheredSlot(
        SurfaceWeatheringDefinition weathering,
        SheetedPlateZone zone,
        int depth,
        int kaoliniteSlot,
        int gossanSlot)
    {
        if (!weathering.Enabled) return -1;
        if (zone == SheetedPlateZone.Primary && depth == 1) return gossanSlot;
        if (zone is SheetedPlateZone.Gangue or SheetedPlateZone.RareGangue
            && depth is >= 1 and <= 3)
        {
            return kaoliniteSlot;
        }
        return -1;
    }

    /// <summary>
    /// Depth 2ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã¢â‚¬Å“8 martitization: fully hematitic at the shallow front, increasingly mottled
    /// downward, while depth 1 is reserved for the gossan soil product.
    /// </summary>
    internal static bool IsMartitized(
        ulong featureId,
        int depth,
        int worldX,
        int worldY,
        int worldZ)
    {
        if (depth is < 2 or > 8) return false;
        double shallowFraction = (8 - depth) / 6.0;
        double replacementChance = 0.18 + 0.72 * shallowFraction;
        return ProceduralDepositMath.CoordinateNoise(
            featureId,
            worldX,
            worldY,
            worldZ,
            0x4D415254495445UL) < replacementChance;
    }
    private static SheetedPlateZone SelectSheetedPlateZoneCore(
        SheetedPlateDefinition settings,
        in SheetedPlateSample sample,
        double grain,
        ulong featureId,
        int worldX,
        int worldZ)
    {
        if (sample.Inside)
        {
            if (sample.MemberKind == SheetedPlateMemberKind.Satellite)
            {
                if (sample.LocalY > 5 && grain > 1 - settings.SecondaryFraction)
                {
                    return SheetedPlateZone.Secondary;
                }
                return grain < settings.GangueFraction
                    ? SheetedPlateZone.Gangue
                    : SheetedPlateZone.Primary;
            }

            if (sample.CenterRatio > 0.70 && grain < settings.GangueFraction)
            {
                return SheetedPlateZone.Gangue;
            }
            if (sample.CenterRatio < 0.38 && grain < settings.RareGangueFraction)
            {
                return SheetedPlateZone.RareGangue;
            }
            if (sample.LocalY > 7 && grain > 1 - settings.SecondaryFraction)
            {
                return SheetedPlateZone.Secondary;
            }
            return SheetedPlateZone.Primary;
        }

        if (grain < settings.BrecciaFraction) return SheetedPlateZone.Breccia;

        // Sodic alteration selvage grading outward into silicification.
        double alteration = ProceduralDepositMath.SmoothNoise2D(
            featureId,
            worldX / 7.0,
            worldZ / 7.0,
            0x534845455448414CUL);
        return alteration < settings.AlbiteFraction ? SheetedPlateZone.Albite : SheetedPlateZone.Quartz;
    }

    internal void RealizeSplineCandidate(
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        CompiledProceduralDeposit compiled = candidate.Compiled;
        SplineNetworkDefinition settings = compiled.Definition.Spline;
        SplineNetworkPlan plan = candidate.SplinePlan;
        ulong featureId = candidate.Instance.FeatureId;
        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;
        int worldHeight = serverApi!.World.BlockAccessor.MapSizeY;
        int[] zoneSlots = BuildSplineZoneSlots(compiled);
        int redClaySlot = compiled.GetSlotId(ProceduralMaterialSlots.RedClay);
        int kaoliniteSlot = compiled.GetSlotId(ProceduralMaterialSlots.Kaolinite);

        for (int localX = 0; localX < ChunkSize; localX++)
        {
            int worldX = baseX + localX;
            for (int localZ = 0; localZ < ChunkSize; localZ++)
            {
                int worldZ = baseZ + localZ;
                if (!plan.TryGetColumn(worldX, worldZ, out SplineColumn column)) continue;

                int surfaceY = heightMap[localZ * ChunkSize + localX];
                int minimumY = GameMath.Clamp(column.MinY, 1, worldHeight - 2);
                int maximumY = GameMath.Clamp(Math.Min(column.MaxY, surfaceY), 1, worldHeight - 2);

                for (int y = minimumY; y <= maximumY; y++)
                {
                    SplineNetworkSample sample = plan.Sample(column, worldX, y, worldZ);
                    if (!sample.Inside) continue;

                    int chunkY = y / ChunkSize;
                    if ((uint)chunkY >= (uint)request.Chunks.Length) continue;
                    int localY = y % ChunkSize;
                    int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
                    IChunkBlocks data = request.Chunks[chunkY].Data;
                    int hostBlockId = data.GetBlockIdUnsafe(index3d);
                    SplineMaterialZone zone = SelectSplineZone(featureId, settings, sample, worldX, y, worldZ);
                    int grade = SelectSplineGrade(featureId, sample, worldX, y, worldZ);
                    int depth = GetSurfaceDepth(surfaceY, y);
                    int weatheredSlot = GetPegmatiteWeatheredSlot(
                        compiled.Definition.Weathering,
                        zone,
                        depth,
                        redClaySlot,
                        kaoliniteSlot);
                    if (weatheredSlot >= 0)
                    {
                        if (!CanPlaceWeatheredSoil(compiled, hostBlockId)) continue;
                    }
                    else if (!CanReplaceWithProceduralRock(compiled, hostBlockId))
                    {
                        continue;
                    }

                    int placeBlockId = weatheredSlot >= 0
                        ? compiled.ResolveWeatheredBlock(weatheredSlot, grade, hostBlockId, y < surfaceY)
                        : ResolveSplineBlock(compiled, zoneSlots, zone, grade, hostBlockId);
                    if (placeBlockId == 0) continue;


                    data.SetBlockUnsafe(index3d, placeBlockId);
                    AdvancedGeology.Byproducts.ByproductSystem.RecordPlacement(request.Chunks[chunkY], index3d, placeBlockId, candidate.Instance.FeatureId, compiled);
                    data.SetFluid(index3d, 0);
                }
            }
        }
    }

    private static int[] BuildSplineZoneSlots(CompiledProceduralDeposit compiled)
    {
        var slots = new int[SplineZoneCount];
        slots[(int)SplineMaterialZone.Wall] = compiled.GetSlotId(ProceduralMaterialSlots.Wall);
        slots[(int)SplineMaterialZone.Schorl] = compiled.GetSlotId(ProceduralMaterialSlots.Schorl);
        slots[(int)SplineMaterialZone.Orthoclase] = compiled.GetSlotId(ProceduralMaterialSlots.Orthoclase);
        slots[(int)SplineMaterialZone.Intermediate] = compiled.GetSlotId(ProceduralMaterialSlots.Intermediate);
        slots[(int)SplineMaterialZone.Core] = compiled.GetSlotId(ProceduralMaterialSlots.Core);

        slots[(int)SplineMaterialZone.Albite] = compiled.GetSlotId(ProceduralMaterialSlots.Albite);
        slots[(int)SplineMaterialZone.Lepidolite] = compiled.GetSlotId(ProceduralMaterialSlots.Lepidolite);
        slots[(int)SplineMaterialZone.Pollucite] = compiled.GetSlotId(ProceduralMaterialSlots.Pollucite);
        slots[(int)SplineMaterialZone.Spodumene] = compiled.GetSlotId(ProceduralMaterialSlots.Spodumene);
        slots[(int)SplineMaterialZone.Beryl] = compiled.GetSlotId(ProceduralMaterialSlots.Beryl);
        slots[(int)SplineMaterialZone.Cassiterite] = compiled.GetSlotId(ProceduralMaterialSlots.Cassiterite);
        slots[(int)SplineMaterialZone.Tantalite] = compiled.GetSlotId(ProceduralMaterialSlots.Tantalite);
        slots[(int)SplineMaterialZone.Columbite] = compiled.GetSlotId(ProceduralMaterialSlots.Columbite);
        return slots;
    }
    internal static int GetPegmatiteWeatheredSlot(
        SurfaceWeatheringDefinition weathering,
        SplineMaterialZone zone,
        int depth,
        int redClaySlot,
        int kaoliniteSlot)
    {
        if (!weathering.Enabled) return -1;
        if (zone is SplineMaterialZone.Orthoclase or SplineMaterialZone.Intermediate or SplineMaterialZone.Albite)
        {
            if (depth == 1) return redClaySlot;
            if (depth is >= 2 and <= 6) return kaoliniteSlot;
        }
        if (zone is SplineMaterialZone.Spodumene or SplineMaterialZone.Pollucite
            && depth is >= 1 and <= 6)
        {
            return kaoliniteSlot;
        }
        if (depth == 1
            && zone is SplineMaterialZone.Schorl
                or SplineMaterialZone.Lepidolite
                or SplineMaterialZone.Beryl
                or SplineMaterialZone.Cassiterite
                or SplineMaterialZone.Tantalite
                or SplineMaterialZone.Columbite)
        {
            return redClaySlot;
        }
        return -1;
    }

    /// <summary>
    /// Accessory and replacement zones are optional; an unresolved one degrades to its
    /// structural parent zone instead of leaving unfilled voxels inside the dike.
    /// </summary>
    private static int ResolveSplineBlock(
        CompiledProceduralDeposit compiled,
        int[] zoneSlots,
        SplineMaterialZone zone,
        int gradeIndex,
        int hostBlockId)
    {
        SplineMaterialZone current = zone;
        for (int step = 0; step < SplineZoneCount; step++)
        {
            int placed = compiled.ResolveBlock(zoneSlots[(int)current], gradeIndex, hostBlockId);
            if (placed != 0) return placed;

            SplineMaterialZone fallback = SplineZoneFallback(current);
            if (fallback == current) return 0;
            current = fallback;
        }
        return 0;
    }


    private static SplineMaterialZone SplineZoneFallback(SplineMaterialZone zone)
    {
        return zone switch
        {
            SplineMaterialZone.Beryl => SplineMaterialZone.Core,
            SplineMaterialZone.Core => SplineMaterialZone.Intermediate,
            SplineMaterialZone.Albite => SplineMaterialZone.Intermediate,
            SplineMaterialZone.Lepidolite => SplineMaterialZone.Intermediate,
            SplineMaterialZone.Pollucite => SplineMaterialZone.Intermediate,
            SplineMaterialZone.Spodumene => SplineMaterialZone.Intermediate,
            SplineMaterialZone.Cassiterite => SplineMaterialZone.Intermediate,
            SplineMaterialZone.Tantalite => SplineMaterialZone.Intermediate,
            SplineMaterialZone.Columbite => SplineMaterialZone.Intermediate,
            SplineMaterialZone.Intermediate => SplineMaterialZone.Orthoclase,
            SplineMaterialZone.Orthoclase => SplineMaterialZone.Wall,
            SplineMaterialZone.Schorl => SplineMaterialZone.Wall,
            _ => SplineMaterialZone.Wall
        };
    }

    /// <summary>
    /// Ore quality inside a pegmatite: richest toward the fractionated dike centre, modulated
    /// by a coherent field so grade forms patches rather than per-voxel static.
    /// </summary>
    internal static int SelectSplineGrade(
        ulong featureId,
        in SplineNetworkSample sample,
        int worldX,
        int worldY,
        int worldZ)
    {
        double patch = ProceduralDepositMath.SmoothNoise2D(
            featureId,
            worldX / 9.0,
            worldZ / 9.0,
            GradeSalt);
        double vertical = ProceduralDepositMath.CoordinateNoise(
            featureId,
            worldX / 6,
            worldY / 6,
            worldZ / 6,
            GradeSalt);
        double centrality = 1.0 - Math.Clamp(sample.ThicknessRatio, 0, 1);
        double richness = 0.45 * patch + 0.20 * vertical + 0.35 * centrality;
        if (richness < 0.36) return 0;
        if (richness < 0.62) return 1;
        if (richness < 0.85) return 2;
        return 3;
    }

    /// <summary>
    /// Pegmatite paragenesis based on the deposit model. Thickness is signed: positive is
    /// the hanging wall. Accessory phases use structured fields so they form laths and streaks
    /// rather than per-voxel static, and the coarse replacement bodies come from oriented pods.
    /// </summary>
    internal static SplineMaterialZone SelectSplineZone(
        ulong featureId,
        SplineNetworkDefinition settings,
        in SplineNetworkSample sample,
        int worldX,
        int worldY,
        int worldZ)
    {
        double signed = sample.SignedThickness;
        double width = sample.WidthRatio;
        double phase = ProceduralDepositMath.UnitDouble(featureId ^ LensSalt) * Math.PI * 2;

        // 1. Coarse replacement bodies, clipped to the intermediate zone of the sheet.
        if (sample.Body == SplineReplacementBody.Pollucite
            && signed > 0.12
            && signed < 0.80
            && width < 0.78)
        {
            return SplineMaterialZone.Pollucite;
        }
        if (sample.Body == SplineReplacementBody.Lepidolite
            && signed > 0.05
            && signed < 0.78
            && width < 0.82)
        {
            return SplineMaterialZone.Lepidolite;
        }

        // 2. Beryl on the hanging-wall core contact.
        if (signed > 0.22 && signed < 0.58 && width < 0.70)
        {
            double berylField = Math.Sin(worldX * 0.34 + 1.2 + phase) * Math.Sin(worldZ * 0.31 + 0.8 + phase);
            if (berylField > 1.0 - 2.0 * settings.BerylFraction) return SplineMaterialZone.Beryl;
        }

        // 3. Spodumene laths straddling the core/intermediate boundary.
        if (Math.Abs(signed - 0.08) < 0.28 && width < 0.68)
        {
            double lathField = Math.Sin(worldX * 0.42 + worldZ * 0.25 + phase)
                * Math.Cos(worldZ * 0.38 - worldX * 0.18 + phase);
            if (lathField > 1.0 - 2.0 * settings.SpodumeneFraction) return SplineMaterialZone.Spodumene;
        }

        // 4. Footwall oxide fields: cassiterite plus mixed tantalite and columbite.
        if (signed < -0.55 && signed > -0.96 && width < 0.88)
        {
            double oxideGrain = ProceduralDepositMath.CoordinateNoise(
                featureId,
                worldX,
                worldY,
                worldZ,
                AccessorySalt);
            if (oxideGrain < settings.CassiteriteFraction) return SplineMaterialZone.Cassiterite;
            if (oxideGrain < settings.CassiteriteFraction + settings.TantaliteFraction)
            {
                return SplineMaterialZone.Tantalite;
            }
            if (oxideGrain < settings.CassiteriteFraction
                + settings.TantaliteFraction
                + settings.ColumbiteFraction)
            {
                return SplineMaterialZone.Columbite;
            }
        }

        // 5. Cleavelandite/albite replacement selvage outside the quartz core.
        if (sample.ThicknessRatio > settings.CoreRatio
            && sample.ThicknessRatio <= settings.IntermediateRatio)
        {
            double albiteField = ProceduralDepositMath.SmoothNoise2D(
                featureId,
                worldX / 6.0,
                worldZ / 6.0,
                AlbiteSalt);
            if (albiteField > 1.0 - settings.AlbiteFraction) return SplineMaterialZone.Albite;
        }

        // 6. Massive quartz core, broken along strike by a smooth core field.
        if (sample.ThicknessRatio < settings.CoreRatio && width < 0.48)
        {
            double coreField = Math.Sin(worldX * 0.14 + 0.5 + phase) * Math.Cos(worldZ * 0.12 - 0.2 + phase);
            if (coreField > -0.35) return SplineMaterialZone.Core;
        }

        // 7. Intermediate K-feldspar zoning: early orthoclase along the outer
        // intermediate zone, grading inward to microcline/perthite.
        if (sample.ThicknessRatio < settings.IntermediateRatio && width < 0.82)
        {
            double normalizedIntermediate = (sample.ThicknessRatio - settings.CoreRatio)
                / Math.Max(0.01, settings.IntermediateRatio - settings.CoreRatio);
            double orthoclaseBand = ProceduralDepositMath.SmoothNoise2D(
                featureId,
                worldX / 11.0,
                worldZ / 11.0,
                AccessorySalt ^ 0x4F5254484F434C41UL);
            if (normalizedIntermediate > 0.48 && orthoclaseBand > 0.38)
            {
                return SplineMaterialZone.Orthoclase;
            }
            return SplineMaterialZone.Intermediate;
        }

        // 8. Border selvage: aplitic pegmatite speckled with schorl.
        double borderGrain = ProceduralDepositMath.CoordinateNoise(
            featureId,
            worldX,
            worldY,
            worldZ,
            SchorlSalt);
        return borderGrain > 1.0 - settings.SchorlFraction
            ? SplineMaterialZone.Schorl
            : SplineMaterialZone.Wall;
    }

    private void PlaceGossanSmears(
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        CompiledProceduralDeposit compiled = candidate.Compiled;
        EpithermalVeinDefinition settings = compiled.Definition.Epithermal;
        EpithermalVeinPlan plan = candidate.EpithermalPlan!;
        ulong featureId = candidate.Instance.FeatureId;
        int maximumHeight = Math.Max(settings.SmearHeightMin, settings.SmearHeightMax);
        if (maximumHeight <= 0 || settings.SmearSeedFraction <= 0) return;

        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;
        long planReachSquared = (long)settings.HorizontalRadius * settings.HorizontalRadius;

        for (int seedX = baseX - maximumHeight; seedX < baseX + ChunkSize + maximumHeight; seedX++)
        {
            for (int seedZ = baseZ - maximumHeight; seedZ < baseZ + ChunkSize + maximumHeight; seedZ++)
            {
                int seedOffsetX = seedX - candidate.Instance.CenterX;
                int seedOffsetZ = seedZ - candidate.Instance.CenterZ;
                if (seedOffsetX * seedOffsetX + seedOffsetZ * seedOffsetZ > planReachSquared) continue;
                if (!ProceduralDepositMath.IsSmearSeed(featureId, seedX, seedZ, settings.SmearSeedFraction)) continue;
                if (!IsGossanCapColumn(compiled, plan, settings, seedX, seedZ, out int seedSurfaceY)) continue;

                int height = ProceduralDepositMath.SmearHeight(
                    featureId,
                    seedX,
                    seedZ,
                    settings.SmearHeightMin,
                    maximumHeight);
                int apexY = seedSurfaceY + 1;

                for (int offsetX = -height; offsetX <= height; offsetX++)
                {
                    int worldX = seedX + offsetX;
                    int localX = worldX - baseX;
                    if (localX < 0 || localX >= ChunkSize) continue;

                    for (int offsetZ = -height; offsetZ <= height; offsetZ++)
                    {
                        int worldZ = seedZ + offsetZ;
                        int localZ = worldZ - baseZ;
                        if (localZ < 0 || localZ >= ChunkSize) continue;

                        int radius = Math.Max(Math.Abs(offsetX), Math.Abs(offsetZ));
                        int targetSurfaceY = heightMap[localZ * ChunkSize + localX];
                        if (!ProceduralDepositMath.SmearCovers(apexY, height, radius, targetSurfaceY)) continue;

                        PlaceSmearedGossanSoil(
                            compiled,
                            request,
                            localX,
                            localZ,
                            targetSurfaceY);
                    }
                }
            }
        }
    }

    private bool IsGossanCapColumn(
        CompiledProceduralDeposit compiled,
        EpithermalVeinPlan plan,
        EpithermalVeinDefinition settings,
        int worldX,
        int worldZ,
        out int surfaceY)
    {
        if (!TryGetSurfaceHeight(worldX, worldZ, out surfaceY)) return false;
        if (!plan.Sample(worldX, surfaceY, worldZ).Inside) return false;

        int lowestY = Math.Max(1, surfaceY - settings.GeyseriteCapMax - MaximumSoilDepth);
        var position = new BlockPos(worldX, surfaceY, worldZ);
        for (int y = surfaceY; y >= lowestY; y--)
        {
            position.Y = y;
            if (CanReplaceWithProceduralRock(compiled, blockAccessor!.GetBlock(position).BlockId)) return true;
        }
        return false;
    }

    private void PlaceSmearedGossanSoil(
        CompiledProceduralDeposit compiled,
        IChunkColumnGenerateRequest request,
        int localX,
        int localZ,
        int surfaceY)
    {
        if (surfaceY <= 0) return;
        int chunkY = surfaceY / ChunkSize;
        if ((uint)chunkY >= (uint)request.Chunks.Length) return;
        int localY = surfaceY % ChunkSize;
        int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
        IChunkBlocks data = request.Chunks[chunkY].Data;
        int existingBlockId = data.GetBlockIdUnsafe(index3d);
        if (!IsSoilSurface(existingBlockId) || compiled.IsExcludedGossanSurface(existingBlockId)) return;

        int gossanBlockId = compiled.GossanBlockId;
        if (gossanBlockId == 0) return;

        data.SetBlockUnsafe(index3d, gossanBlockId);
        AdvancedGeology.Byproducts.ByproductSystem.ClearPlacement(request.Chunks[chunkY], index3d);
        data.SetFluid(index3d, 0);
    }

    private bool IsSoilSurface(int blockId)
    {
        if ((uint)blockId >= (uint)serverApi!.World.Blocks.Count || IsProceduralOutput(blockId)) return false;
        return serverApi.World.Blocks[blockId]?.BlockMaterial == EnumBlockMaterial.Soil;
    }

    internal static EpithermalMaterialZone SelectEpithermalZone(
        ulong featureId,
        EpithermalVeinDefinition settings,
        in EpithermalVeinSample sample,
        int worldX,
        int worldY,
        int worldZ,
        int depth,
        out bool geyseritePipe)
    {
        double grain = ProceduralDepositMath.CoordinateNoise(
            featureId,
            worldX,
            worldY,
            worldZ,
            0x455049475241494EUL);
        geyseritePipe = depth <= settings.LeachedDepth
            && sample.CenterRatio <= settings.GeyseritePipeCenterRatio
            && ProceduralDepositMath.SmoothNoise2D(
                featureId,
                worldX / 5.0,
                worldZ / 5.0,
                0x5049504547455953UL) > 0.30;
        if (geyseritePipe) return EpithermalMaterialZone.Quartz;

        if (depth <= settings.LeachedDepth)
        {
            return EpithermalMaterialZone.Quartz;
        }
        if (depth <= settings.SupergeneDepth)
        {
            if (grain > 0.52) return EpithermalMaterialZone.NativeSilver;
            if (grain > 0.22) return EpithermalMaterialZone.Acanthite;
            return EpithermalMaterialZone.Quartz;
        }

        bool boilingHorizon = sample.LocalY >= settings.BoilingMinY
            && sample.LocalY <= settings.BoilingMaxY
            && sample.CenterRatio < 0.48;
        if (boilingHorizon && grain > 0.70) return EpithermalMaterialZone.Electrum;
        if (sample.CenterRatio < 0.58 && grain > 0.40) return EpithermalMaterialZone.Acanthite;
        return EpithermalMaterialZone.Quartz;
    }

    private bool ColumnHasReplaceableHost(
        CompiledProceduralDeposit compiled,
        IChunkColumnGenerateRequest request,
        int localX,
        int localZ,
        int surfaceY,
        int searchDepth)
    {
        int lowestY = Math.Max(1, surfaceY - searchDepth);
        for (int y = surfaceY; y >= lowestY; y--)
        {
            int chunkY = y / ChunkSize;
            if ((uint)chunkY >= (uint)request.Chunks.Length) continue;
            int localY = y % ChunkSize;
            int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
            if (CanReplaceWithProceduralRock(
                compiled,
                request.Chunks[chunkY].Data.GetBlockIdUnsafe(index3d)))
            {
                return true;
            }
        }
        return false;
    }

    private void PlaceEpithermalSurfaceCap(
        CompiledProceduralDeposit compiled,
        IChunkColumnGenerateRequest request,
        int localX,
        int localZ,
        int surfaceY,
        int geyseriteThickness)
    {
        if (compiled.GossanBlockId == 0 || compiled.GeyseriteBlockId == 0) return;

        PlaceGossanSoil(compiled, request, localX, localZ, surfaceY);
        for (int depth = 1; depth <= geyseriteThickness; depth++)
        {
            SetDirectSurfaceBlock(
                request,
                localX,
                localZ,
                surfaceY - depth,
                compiled.GeyseriteBlockId);
        }
    }

    private void PlaceGossanSoil(
        CompiledProceduralDeposit compiled,
        IChunkColumnGenerateRequest request,
        int localX,
        int localZ,
        int surfaceY)
    {
        if (surfaceY <= 0) return;
        int chunkY = surfaceY / ChunkSize;
        if ((uint)chunkY >= (uint)request.Chunks.Length) return;
        int localY = surfaceY % ChunkSize;
        int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
        IChunkBlocks data = request.Chunks[chunkY].Data;
        int existingBlockId = data.GetBlockIdUnsafe(index3d);
        if (!CanReplaceWithCap(existingBlockId) || compiled.IsExcludedGossanSurface(existingBlockId)) return;

        int gossanBlockId = compiled.GossanBlockId;
        if (gossanBlockId == 0) return;

        data.SetBlockUnsafe(index3d, gossanBlockId);
        AdvancedGeology.Byproducts.ByproductSystem.ClearPlacement(request.Chunks[chunkY], index3d);
        data.SetFluid(index3d, 0);
    }

    private void SetDirectSurfaceBlock(
        IChunkColumnGenerateRequest request,
        int localX,
        int localZ,
        int y,
        int placeBlockId)
    {
        if (y <= 0) return;
        int chunkY = y / ChunkSize;
        if ((uint)chunkY >= (uint)request.Chunks.Length) return;
        int localY = y % ChunkSize;
        int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
        IChunkBlocks data = request.Chunks[chunkY].Data;
        int existingBlockId = data.GetBlockIdUnsafe(index3d);
        if (!CanReplaceWithCap(existingBlockId)) return;

        data.SetBlockUnsafe(index3d, placeBlockId);
        AdvancedGeology.Byproducts.ByproductSystem.ClearPlacement(request.Chunks[chunkY], index3d);
        data.SetFluid(index3d, 0);
    }

    private bool CanReplaceWithCap(int blockId)
    {
        if ((uint)blockId >= (uint)serverApi!.World.Blocks.Count || IsProceduralOutput(blockId)) return false;
        Block? block = serverApi.World.Blocks[blockId];
        return IsWeatheringReplacementMaterial(block?.BlockMaterial, block?.Code?.Path);
    }

    internal static bool IsWeatheringReplacementMaterial(EnumBlockMaterial? material, string? path)
    {
        if (material is EnumBlockMaterial.Sand or EnumBlockMaterial.Gravel) return false;
        if (path?.StartsWith("muddygravel", StringComparison.Ordinal) == true) return false;
        return material is EnumBlockMaterial.Soil or EnumBlockMaterial.Stone or EnumBlockMaterial.Ore;
    }

    private bool CanReplaceWithProceduralRock(CompiledProceduralDeposit compiled, int blockId)
    {
        return AllowsProceduralRockReplacement(
            IsProceduralOutput(blockId),
            compiled.CanReplaceRock(blockId),
            compiled.IsNaturalRock(blockId));
    }

    internal static bool AllowsProceduralRockReplacement(
        bool proceduralOutput,
        bool replaceableHost,
        bool naturalRock)
    {
        return replaceableHost && (!proceduralOutput || naturalRock);
    }

    private bool IsProceduralOutput(int blockId)
    {
        return (uint)blockId < (uint)proceduralOutputBlocks.Length && proceduralOutputBlocks[blockId];
    }

    private void TryPlaceGossan(
        CompiledProceduralDeposit compiled,
        in ProceduralDepositInstance instance,
        IChunkColumnGenerateRequest request,
        int localX,
        int localZ,
        int worldX,
        int worldZ,
        int surfaceY,
        in ProceduralColumnSample bodyColumn)
    {
        ProceduralDepositDefinition definition = compiled.Definition;
        SupergeneDefinition supergene = definition.Supergene;
        if (!supergene.Enabled || supergene.GossanSoilDepth <= 0 || compiled.GossanBlockId == 0) return;

        bool sourceNearSurface = bodyColumn.Intersects
            && surfaceY >= bodyColumn.BaseY
            && surfaceY - bodyColumn.RoofY <= supergene.GossanSourceDepth;
        double density = sourceNearSurface ? 1.0 : GetBloomDensity(definition, instance, worldX, worldZ, surfaceY);
        if (density <= 0 || ProceduralDepositMath.CoordinateNoise(instance.FeatureId, worldX, surfaceY, worldZ, BloomSalt) > density)
        {
            return;
        }
        if (!ColumnHasReplaceableHost(
            compiled,
            request,
            localX,
            localZ,
            surfaceY,
            supergene.GossanSoilDepth + MaximumSoilDepth))
        {
            return;
        }
        int gossanSlot = compiled.GetSlotId(ProceduralMaterialSlots.Gossan);
        int remaining = supergene.GossanSoilDepth;
        for (int y = surfaceY; y > 0 && remaining > 0; y--)
        {
            int chunkY = y / ChunkSize;
            if ((uint)chunkY >= (uint)request.Chunks.Length) continue;
            int localY = y % ChunkSize;
            int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
            IChunkBlocks data = request.Chunks[chunkY].Data;
            int existingId = data.GetBlockIdUnsafe(index3d);
            Block? existing = serverApi!.World.Blocks[existingId];
            string? path = existing?.Code?.Path;
            if (path == null) break;

            bool replaceable = path.StartsWith("soil-", StringComparison.Ordinal)
                || path.StartsWith("rock-", StringComparison.Ordinal);
            if (!replaceable) break;

            int placeBlockId = compiled.ResolveWeatheredBlock(gossanSlot, 0, existingId, y < surfaceY);
            if (placeBlockId == 0) break;
            data.SetBlockUnsafe(index3d, placeBlockId);
            AdvancedGeology.Byproducts.ByproductSystem.ClearPlacement(request.Chunks[chunkY], index3d);
            data.SetFluid(index3d, 0);
            remaining--;
        }
    }

    private double GetBloomDensity(
        ProceduralDepositDefinition definition,
        in ProceduralDepositInstance instance,
        int worldX,
        int worldZ,
        int surfaceY)
    {
        double bloom = definition.Supergene.BloomRadius;
        if (bloom <= 0) return 0;

        double dx = worldX - instance.CenterX;
        double dz = worldZ - instance.CenterZ;
        double along = dx * instance.CosYaw + dz * instance.SinYaw;
        double across = -dx * instance.SinYaw + dz * instance.CosYaw;
        double bodyMetric = along * along / (definition.Geometry.RadiusX * definition.Geometry.RadiusX)
            + across * across / (definition.Geometry.RadiusZ * definition.Geometry.RadiusZ);
        if (bodyMetric <= 1.0) return 0;

        double bloomX = definition.Geometry.RadiusX + bloom;
        double bloomZ = definition.Geometry.RadiusZ + bloom;
        double bloomMetric = along * along / (bloomX * bloomX) + across * across / (bloomZ * bloomZ);
        if (bloomMetric > 1.0) return 0;

        double planeY = instance.CenterY + instance.GradientX * dx + instance.GradientZ * dz;
        if (surfaceY < planeY - definition.Geometry.RadiusY
            || surfaceY - planeY > definition.Supergene.GossanSourceDepth)
        {
            return 0;
        }

        double maximumRadius = Math.Max(definition.Geometry.RadiusX, definition.Geometry.RadiusZ);
        double fade = (1.0 - Math.Sqrt(bloomMetric)) * (maximumRadius + bloom) / bloom;
        return GameMath.Clamp(fade, 0.0, 1.0) * 0.65;
    }

    private int GetOxidationDepth(
        ProceduralDepositDefinition definition,
        ulong featureId,
        int worldX,
        int worldZ)
    {
        int variation = definition.Supergene.OxidationDepthNoise;
        if (variation <= 0) return Math.Max(0, definition.Supergene.OxidationDepth);
        double noise = ProceduralDepositMath.SmoothNoise2D(
            featureId,
            worldX / 8.0,
            worldZ / 8.0,
            WeatheringSalt);
        int offset = (int)Math.Round((noise * 2.0 - 1.0) * variation);
        return Math.Max(0, definition.Supergene.OxidationDepth + offset);
    }

    private static int GetSurfaceDepth(int surfaceY, int y)
    {
        return surfaceY - y + 1;
    }

    private bool CanPlaceWeatheredSoil(CompiledProceduralDeposit compiled, int blockId)
    {
        if ((uint)blockId >= (uint)serverApi!.World.Blocks.Count) return false;
        Block? block = serverApi.World.Blocks[blockId];
        if (!IsWeatheringReplacementMaterial(block?.BlockMaterial, block?.Code?.Path)) return false;
        return CanReplaceWithProceduralRock(compiled, blockId) || IsSoilSurface(blockId);
    }
    private bool TryFindSourceY(CompiledProceduralDeposit compiled, int worldX, int worldZ, out int sourceY)
    {
        SourceRockDefinition source = compiled.Definition.Source;
        if (!TryGetSurfaceHeight(worldX, worldZ, out int surfaceY)
            || !TryGetSurfaceDepthBand(
                surfaceY,
                source.SearchMinDepth,
                source.SearchMaxDepth,
                out int minY,
                out int maxY))
        {
            sourceY = 0;
            return false;
        }
        int currentTop = -1;
        int currentThickness = 0;
        var position = new BlockPos(worldX, maxY, worldZ);

        for (int y = maxY; y >= minY; y--)
        {
            position.Y = y;
            Block block = blockAccessor!.GetBlock(position);
            if (compiled.IsEligibleSource(block))
            {
                if (currentTop < 0) currentTop = y;
                currentThickness++;
                continue;
            }

            if (currentThickness >= source.MinimumThickness)
            {
                sourceY = currentTop;
                return true;
            }
            currentTop = -1;
            currentThickness = 0;
        }

        if (currentThickness >= source.MinimumThickness)
        {
            sourceY = currentTop;
            return true;
        }

        sourceY = 0;
        return false;
    }

    private bool TryFitTerrainPlane(
        int centerX,
        int centerZ,
        int radius,
        int spacing,
        out ProceduralTerrainPlane plane)
    {
        spacing = Math.Max(1, spacing);
        radius = Math.Max(spacing, radius);
        var accumulator = new ProceduralTerrainPlaneAccumulator();

        for (int dz = -radius; dz <= radius; dz += spacing)
        {
            for (int dx = -radius; dx <= radius; dx += spacing)
            {
                if (dx * dx + dz * dz > radius * radius) continue;
                if (!TryGetSurfaceHeight(centerX + dx, centerZ + dz, out int height))
                {
                    plane = default;
                    return false;
                }
                accumulator.Add(dx, dz, height);
            }
        }
        return accumulator.TrySolve(out plane);
    }

    private bool TrySelectIntrusionOriginHostY(
        CompiledProceduralDeposit compiled,
        ulong seed,
        int worldX,
        int worldZ,
        int minimumY,
        int maximumY,
        out int hostY)
    {
        if (maximumY < minimumY)
        {
            hostY = 0;
            return false;
        }

        int validHostCount = 0;
        var position = new BlockPos(worldX, minimumY, worldZ);
        for (int y = minimumY; y <= maximumY; y++)
        {
            position.Y = y;
            if (compiled.IsReplaceableHost(blockAccessor!.GetBlock(position).BlockId)) validHostCount++;
        }

        if (validHostCount == 0)
        {
            hostY = 0;
            return false;
        }

        int selectedIndex = Math.Min(
            validHostCount - 1,
            (int)(ProceduralDepositMath.UnitDouble(seed) * validHostCount));
        int currentIndex = 0;
        for (int y = minimumY; y <= maximumY; y++)
        {
            position.Y = y;
            if (!compiled.IsReplaceableHost(blockAccessor!.GetBlock(position).BlockId)) continue;
            if (currentIndex++ != selectedIndex) continue;
            hostY = y;
            return true;
        }

        hostY = 0;
        return false;
    }



    private bool MatchesClimate(
        ProceduralDepositDefinition definition,
        int centerX,
        int centerZ)
    {
        ProceduralClimateDefinition filter = definition.Climate;
        if (!filter.Enabled) return true;

        int regionSize = serverApi!.WorldManager.RegionSize;
        int regionX = ProceduralDepositMath.FloorDiv(centerX, regionSize);
        int regionZ = ProceduralDepositMath.FloorDiv(centerZ, regionSize);
        IMapRegion? region = serverApi.WorldManager.GetMapRegion(regionX, regionZ);
        if (region?.ClimateMap?.Data == null || region.ClimateMap.Size == 0) return false;
        int localX = ProceduralDepositMath.FloorMod(centerX, regionSize);
        int localZ = ProceduralDepositMath.FloorMod(centerZ, regionSize);
        int climate = region.ClimateMap.GetUnpaddedColorLerpedForNormalizedPos(
            (float)localX / regionSize,
            (float)localZ / regionSize);

        int rainRaw = (climate >> 8) & 0xff;
        int tempRaw = (climate >> 16) & 0xff;
        // Raw climate-map values: no sea-level or altitude adjustment.
        double rainfall = rainRaw / 255.0;
        double temperature = Climate.GetScaledAdjustedTemperatureFloat(tempRaw, 0);
        return MatchesClimate(filter, rainfall, temperature);
    }

    internal static bool MatchesClimate(
        ProceduralClimateDefinition filter,
        double rainfall,
        double temperature)
    {
        return !filter.Enabled
            || (rainfall >= filter.MinRain
                && rainfall <= filter.MaxRain
                && temperature >= filter.MinTemp
                && temperature <= filter.MaxTemp);
    }

    internal static bool HasValidClimateBounds(ProceduralClimateDefinition filter)
    {
        return filter.MinRain >= 0.0
            && filter.MaxRain <= 1.0
            && filter.MinRain <= filter.MaxRain
            && filter.MinTemp >= -100.0
            && filter.MaxTemp <= 100.0
            && filter.MinTemp <= filter.MaxTemp;
    }

    private bool TryGetSurfaceHeight(int worldX, int worldZ, out int height)
    {
        int chunkX = ProceduralDepositMath.FloorDiv(worldX, ChunkSize);
        int chunkZ = ProceduralDepositMath.FloorDiv(worldZ, ChunkSize);
        IMapChunk? mapChunk = blockAccessor!.GetMapChunk(chunkX, chunkZ);
        ushort[]? map = mapChunk?.WorldGenTerrainHeightMap;
        if (map == null)
        {
            height = 0;
            return false;
        }

        int localX = ProceduralDepositMath.FloorMod(worldX, ChunkSize);
        int localZ = ProceduralDepositMath.FloorMod(worldZ, ChunkSize);
        height = map[localZ * ChunkSize + localX];
        return true;
    }

    private static bool ValidateDefinition(
        ICoreServerAPI api,
        AssetLocation location,
        ProceduralDepositDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Code))
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit in {0} has no code", location);
            return false;
        }
        if (definition.Prospecting == null
            || definition.Placement == null
            || definition.Geometry == null
            || definition.Epithermal == null
            || definition.Spline == null
            || definition.SheetedPlate == null
            || definition.Oolitic == null
            || definition.TerrainOrientation == null
            || definition.Climate == null
            || definition.Source == null
            || definition.Supergene == null
            || definition.Weathering == null
            || definition.Palette == null
            || definition.Prospecting.MajorMinerals == null
            || definition.Source.EligibleRockVariants == null
            || definition.Palette.ReplaceableRockVariants == null
            || definition.Palette.GossanSurfaceExclusions == null)
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has null definition sections", definition.Code);
            return false;
        }
        if (!ProceduralDepositTemplateRegistry.TryGet(definition.Template, out IProceduralDepositTemplate template))
        {
            api.Logger.Error(
                "[AdvancedGeology] Procedural deposit {0} has unknown template {1}",
                definition.Code,
                definition.Template);
            return false;
        }
        if (!template.Validate(definition, out string templateError))
        {
            api.Logger.Error(
                "[AdvancedGeology] Procedural deposit {0}: {1}",
                definition.Code,
                templateError);
            return false;
        }
        if (!string.Equals(definition.OverlapPolicy, "higherPriorityWins", StringComparison.Ordinal))
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has invalid overlap policy", definition.Code);
            return false;
        }
        definition.Palette.MigrateLegacyMaterials();
        if (definition.Prospecting.MajorMinerals.Length == 0
            || definition.Prospecting.MajorMinerals.Any(string.IsNullOrWhiteSpace)
            || definition.Prospecting.MajorMinerals.Distinct(StringComparer.Ordinal).Count() != definition.Prospecting.MajorMinerals.Length
            || definition.Prospecting.SignalRadius < 32
            || definition.Prospecting.CenterShift < 0
            || definition.Prospecting.CenterShift > definition.Prospecting.SignalRadius * 2)
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has invalid major-mineral prospecting metadata", definition.Code);
            return false;
        }
        if (definition.Placement.CellSize < 32 || definition.Placement.Chance < 0 || definition.Placement.Chance > 1)
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has invalid placement settings", definition.Code);
            return false;
        }
        bool knownYMode = string.Equals(definition.Placement.YMode, "surfaceDepth", StringComparison.OrdinalIgnoreCase)
            || string.Equals(definition.Placement.YMode, "sourceRock", StringComparison.OrdinalIgnoreCase);
        if (!knownYMode
            || definition.Placement.MinDepth < 0
            || definition.Placement.MaxDepth < definition.Placement.MinDepth)
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has invalid vertical placement settings", definition.Code);
            return false;
        }

        if (definition.SurfaceNuggets.Chance is < 0 or > 1 || definition.SurfaceNuggets.MaxDepth < 0)
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has invalid surface nugget settings", definition.Code);
            return false;
        }

        if (!HasValidClimateBounds(definition.Climate))
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has invalid climate settings", definition.Code);
            return false;
        }

        if (definition.TerrainOrientation.Enabled
            && (definition.TerrainOrientation.SampleRadius < 1
                || definition.TerrainOrientation.SampleSpacing < 1
                || definition.TerrainOrientation.MaxAppliedDipDeg < definition.TerrainOrientation.MinAppliedDipDeg))
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has invalid terrain orientation settings", definition.Code);
            return false;
        }
        if (definition.Supergene.OxidationDepth < 0
            || definition.Supergene.OxidationDepthNoise < 0
            || definition.Supergene.EnrichmentThickness < 0
            || definition.Supergene.GossanSourceDepth < 0
            || definition.Supergene.GossanSoilDepth < 0
            || definition.Supergene.BloomRadius < 0)
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has negative supergene settings", definition.Code);
            return false;
        }

        if (definition.Weathering.GossanSmearSeedFraction is < 0 or > 1
            || definition.Weathering.GossanSmearHeightMin < 0
            || definition.Weathering.GossanSmearHeightMax < definition.Weathering.GossanSmearHeightMin)
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has invalid weathering smear settings", definition.Code);
            return false;
        }

        if (definition.Source.Enabled && !string.Equals(definition.Placement.YMode, "sourceRock", StringComparison.OrdinalIgnoreCase))
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} enables a source constraint without yMode=sourceRock", definition.Code);
            return false;
        }
        if (string.Equals(definition.Placement.YMode, "sourceRock", StringComparison.OrdinalIgnoreCase)
            && (!definition.Source.Enabled || definition.Source.EligibleRockVariants.Length == 0))
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} uses yMode=sourceRock without eligible source rocks", definition.Code);
            return false;
        }
        if (definition.Source.Enabled
            && (definition.Source.MinimumThickness < 1
                || definition.Source.SearchMinDepth < 0
                || definition.Source.SearchMaxDepth < definition.Source.SearchMinDepth))
        {
            api.Logger.Error("[AdvancedGeology] Procedural deposit {0} has invalid source-rock settings", definition.Code);
            return false;
        }

        if (definition.Source.Enabled && template.GetMaximumHorizontalReach(definition) > ChunkSize)
        {
            api.Logger.Error(
                "[AdvancedGeology] Procedural deposit {0} requires remote source-rock context; first-version limit is {1}",
                definition.Code,
                ChunkSize);
            return false;
        }
        if (definition.TerrainOrientation.Enabled)
        {
            int contextReach = template.GetMaximumHorizontalReach(definition)
                + definition.TerrainOrientation.SampleRadius;
            if (contextReach > ChunkSize)
            {
                api.Logger.Error(
                    "[AdvancedGeology] Procedural deposit {0} requires terrain context {1}; reliable worldgen limit is {2}",
                    definition.Code,
                    contextReach,
                    ChunkSize);
                return false;
            }
        }
        if (definition.TerrainOrientation.Enabled)
        {
            int diameterSamples = 2 * definition.TerrainOrientation.SampleRadius
                / definition.TerrainOrientation.SampleSpacing + 1;
            if (diameterSamples * diameterSamples > 4096)
            {
                api.Logger.Error(
                    "[AdvancedGeology] Procedural deposit {0} requests more than 4096 terrain-orientation samples",
                    definition.Code);
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Splits an ore block path of the form <c>ore-{grade}-{ore}-{rock}</c>.
    /// </summary>
    internal static bool TryParseGradedOrePath(string? path, out string ore, out string rock)
    {
        ore = string.Empty;
        rock = string.Empty;
        if (string.IsNullOrEmpty(path) || !path.StartsWith("ore-", StringComparison.Ordinal)) return false;

        string[] parts = path.Split('-');
        if (parts.Length != 4) return false;
        if (parts[1] is not ("poor" or "medium" or "rich" or "bountiful")) return false;
        if (parts[2].Length == 0 || parts[3].Length == 0) return false;

        ore = parts[2];
        rock = parts[3];
        return true;
    }

    /// <summary>
    /// Only copper, tin, bismuth, zinc, silver, and gold minerals seed surface bits.
    /// Eligibility is independent of smelting recipes, including IndustrialStory processing.
    /// </summary>
    internal static bool IsSurfaceNuggetOre(string? oreName)
    {
        return oreName is "nativecopper" or "malachite" or "azurite" or "chalcopyrite"
            or "tetrahedrite" or "bornite" or "chalcocite"
            or "cassiterite" or "stannite"
            or "bismuthinite" or "nativebismuth"
            or "sphalerite" or "smithsonite"
            or "nativesilver" or "acanthite"
            or "nativegold" or "electrum";
    }

    /// <summary>
    /// Ore must sit no deeper than the configured block count below the terrain surface.
    /// </summary>
    internal static bool SurfaceNuggetDepthAllowed(int surfaceY, int oreY, int maxDepth)
    {
        int depth = surfaceY - oreY;
        return depth >= 0 && depth <= maxDepth;
    }

    internal static bool ShouldSeedSurfaceNugget(ulong featureId, int worldX, int worldZ, double chance)
    {
        if (chance <= 0) return false;
        if (chance >= 1) return true;
        return ProceduralDepositMath.CoordinateNoise(featureId, worldX, 0, worldZ, SurfaceNuggetSalt) < chance;
    }

    /// <summary>
    /// Resolves the loose surface block for an explicitly eligible ore, or 0 if unavailable.
    /// </summary>
    private int GetSurfaceNuggetBlockId(int oreBlockId)
    {
        if (surfaceNuggetBlocks.TryGetValue(oreBlockId, out int cached)) return cached;

        int resolved = 0;
        Block? ore = (uint)oreBlockId < (uint)serverApi!.World.Blocks.Count
            ? serverApi.World.Blocks[oreBlockId]
            : null;
        if (ore?.Code != null
            && TryParseGradedOrePath(ore.Code.Path, out string oreName, out string rock)
            && IsSurfaceNuggetOre(oreName))
        {
            Block? loose = serverApi.World.GetBlock(
                new AssetLocation(ore.Code.Domain, $"looseores-{oreName}-{rock}-free"));
            if (loose?.Code != null) resolved = loose.BlockId;
        }

        surfaceNuggetBlocks[oreBlockId] = resolved;
        return resolved;
    }

    /// <summary>
    /// Places loose surface bits above explicitly eligible shallow ore, mirroring the vanilla
    /// surface indicator within the configured depth of the surface.
    /// </summary>
    private void PlaceSurfaceNuggets(
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ,
        List<DepositCandidate> candidates)
    {
        if (candidates.Count == 0) return;

        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;
        int worldHeight = serverApi!.World.BlockAccessor.MapSizeY;
        Span<bool> seeded = stackalloc bool[ChunkSize * ChunkSize];

        foreach (DepositCandidate candidate in candidates)
        {
            SurfaceNuggetDefinition settings = candidate.Compiled.Definition.SurfaceNuggets;
            if (!settings.Enabled || settings.Chance <= 0 || settings.MaxDepth < 0) continue;

            int reach = candidate.Compiled.MaximumHorizontalReach;
            int fromX = Math.Max(0, candidate.Instance.CenterX - reach - baseX);
            int toX = Math.Min(ChunkSize - 1, candidate.Instance.CenterX + reach - baseX);
            int fromZ = Math.Max(0, candidate.Instance.CenterZ - reach - baseZ);
            int toZ = Math.Min(ChunkSize - 1, candidate.Instance.CenterZ + reach - baseZ);

            for (int localX = fromX; localX <= toX; localX++)
            {
                int worldX = baseX + localX;
                for (int localZ = fromZ; localZ <= toZ; localZ++)
                {
                    int columnIndex = localZ * ChunkSize + localX;
                    if (seeded[columnIndex]) continue;

                    int worldZ = baseZ + localZ;
                    if (!ShouldSeedSurfaceNugget(candidate.Instance.FeatureId, worldX, worldZ, settings.Chance))
                    {
                        continue;
                    }

                    int surfaceY = heightMap[columnIndex];
                    if (surfaceY <= 0 || surfaceY >= worldHeight - 2) continue;

                    int nuggetBlockId = FindShallowSurfaceNugget(
                        request,
                        localX,
                        localZ,
                        surfaceY,
                        settings.MaxDepth);
                    if (nuggetBlockId == 0) continue;

                    if (TryPlaceSurfaceNugget(request, localX, localZ, surfaceY, nuggetBlockId))
                    {
                        seeded[columnIndex] = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Returns the loose block for the shallowest eligible procedural ore inside the depth window.
    /// </summary>
    private int FindShallowSurfaceNugget(
        IChunkColumnGenerateRequest request,
        int localX,
        int localZ,
        int surfaceY,
        int maxDepth)
    {
        int lowestY = Math.Max(1, surfaceY - maxDepth);
        for (int y = surfaceY; y >= lowestY; y--)
        {
            if (!SurfaceNuggetDepthAllowed(surfaceY, y, maxDepth)) break;

            int chunkY = y / ChunkSize;
            if ((uint)chunkY >= (uint)request.Chunks.Length) continue;
            int index3d = ((y % ChunkSize) * ChunkSize + localZ) * ChunkSize + localX;
            int blockId = request.Chunks[chunkY].Data.GetBlockIdUnsafe(index3d);
            if (blockId == 0 || !IsProceduralOutput(blockId)) continue;

            int nuggetBlockId = GetSurfaceNuggetBlockId(blockId);
            if (nuggetBlockId != 0) return nuggetBlockId;
        }

        return 0;
    }

    private bool TryPlaceSurfaceNugget(
        IChunkColumnGenerateRequest request,
        int localX,
        int localZ,
        int surfaceY,
        int nuggetBlockId)
    {
        int supportChunkY = surfaceY / ChunkSize;
        int placeY = surfaceY + 1;
        int placeChunkY = placeY / ChunkSize;
        if ((uint)supportChunkY >= (uint)request.Chunks.Length) return false;
        if ((uint)placeChunkY >= (uint)request.Chunks.Length) return false;

        int supportIndex = ((surfaceY % ChunkSize) * ChunkSize + localZ) * ChunkSize + localX;
        IChunkBlocks supportData = request.Chunks[supportChunkY].Data;
        if (!IsSurfaceNuggetSupport(supportData.GetBlockIdUnsafe(supportIndex))) return false;

        int placeIndex = ((placeY % ChunkSize) * ChunkSize + localZ) * ChunkSize + localX;
        IChunkBlocks placeData = request.Chunks[placeChunkY].Data;
        if (placeData.GetBlockIdUnsafe(placeIndex) != 0) return false;
        if (placeData.GetFluid(placeIndex) != 0) return false;

        placeData.SetBlockUnsafe(placeIndex, nuggetBlockId);
        placeData.SetFluid(placeIndex, 0);
        return true;
    }

    private bool IsSurfaceNuggetSupport(int blockId)
    {
        if (blockId == 0 || (uint)blockId >= (uint)serverApi!.World.Blocks.Count) return false;
        EnumBlockMaterial? material = serverApi.World.Blocks[blockId]?.BlockMaterial;
        return material is EnumBlockMaterial.Soil
            or EnumBlockMaterial.Stone
            or EnumBlockMaterial.Gravel
            or EnumBlockMaterial.Sand
            or EnumBlockMaterial.Ore;
    }


    private readonly record struct LoadedDefinition(
        AssetLocation Location,
        ProceduralDepositDefinition Definition);

    private readonly record struct PlanKey(ulong FeatureId, int CenterY);
}
