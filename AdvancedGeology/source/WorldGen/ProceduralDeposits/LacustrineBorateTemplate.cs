using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using Vintagestory.API.Server;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace AdvancedGeology.WorldGen.ProceduralDeposits;

public enum LacustrineBorateZone
{
    None = 0,
    Borax,
    Kernite,
    Ulexite,
    Colemanite,
    Gypsum,
    Trona,
    ClayParting,
    TuffMarker,
    HostClaystone,
    HostMarl,
    AlumKalinite,
    AlumAlunogen,
    AlumHalotrichite,
    AlumAlunite,
    AlumJarosite
}

[JsonObject(MemberSerialization.OptIn)]
public sealed class LacustrineBorateDefinition
{
    [JsonProperty]
    public string Family { get; set; } = "borate";

    // Model extent is 72x60. Lenses reach radiusAlong 20 from a centre offset of up to 9, so the
    // reach must contain the widest lens in the stack.
    [JsonProperty]
    public int HorizontalRadius { get; set; } = 34;

    [JsonProperty]
    public int VerticalHalfHeight { get; set; } = 26;

    [JsonProperty]
    public double StrikeDegMin { get; set; } = 0.0;

    [JsonProperty]
    public double StrikeDegMax { get; set; } = 180.0;

    [JsonProperty]
    public double DipDegMin { get; set; } = 0.0;

    [JsonProperty]
    public double DipDegMax { get; set; } = 4.0;

    /// <summary>
    /// Fraction of the brine that is sodium-rich rather than calcium-rich. Higher values push the
    /// zoning toward borax and kernite at the expense of ulexite and colemanite.
    /// </summary>
    [JsonProperty]
    public double SodiumBiasMin { get; set; } = 0.35;

    [JsonProperty]
    public double SodiumBiasMax { get; set; } = 0.8;

    [JsonProperty]
    public int LensCountMin { get; set; } = 1;

    [JsonProperty]
    public int LensCountMax { get; set; } = 3;

    [JsonProperty]
    public double FirstLevelMin { get; set; } = -12.0;

    [JsonProperty]
    public double FirstLevelMax { get; set; } = -9.0;

    [JsonProperty]
    public double LevelStepMin { get; set; } = 4.5;

    [JsonProperty]
    public double LevelStepMax { get; set; } = 7.0;

    [JsonProperty]
    public double LensCenterAlongSpread { get; set; } = 6.0;

    [JsonProperty]
    public double LensCenterAlongDrift { get; set; } = 3.0;

    [JsonProperty]
    public double LensCenterAcrossMin { get; set; } = -3.0;

    [JsonProperty]
    public double LensCenterAcrossMax { get; set; } = 5.0;

    [JsonProperty]
    public double LensRadiusAlongMin { get; set; } = 13.0;

    [JsonProperty]
    public double LensRadiusAlongMax { get; set; } = 20.0;

    [JsonProperty]
    public double LensRadiusAcrossMin { get; set; } = 8.0;

    [JsonProperty]
    public double LensRadiusAcrossMax { get; set; } = 14.0;

    [JsonProperty]
    public double LensHalfThicknessMin { get; set; } = 1.6;

    [JsonProperty]
    public double LensHalfThicknessMax { get; set; } = 3.0;

    [JsonProperty]
    public double PartingChance { get; set; } = 0.65;

    /// <summary>Stratigraphic top of the lake claystone package (Model <c>hostType</c>).</summary>
    [JsonProperty]
    public double ClaystoneTop { get; set; } = 8.0;

    /// <summary>Stratigraphic base of the lake claystone package.</summary>
    [JsonProperty]
    public double ClaystoneBase { get; set; } = -17.0;

    /// <summary>Stratigraphic position of the marker tuff bed inside the claystone.</summary>
    [JsonProperty]
    public double TuffMarkerLevel { get; set; } = -2.0;
}

/// <summary>
/// Result of classifying one voxel against a <see cref="LacustrineBoratePlan"/>.
/// </summary>
public readonly record struct LacustrineBorateSample(
    LacustrineBorateZone Zone,
    bool InLens = false,
    double LensFootprint = 0.0,
    double LensRatio = 0.0,
    double Density = 1.0);

/// <summary>
/// Borax, kernite, ulexite, colemanite, gypsum, trona, clay, and tuff form gently dipping evaporite lenses within a marl-bounded lake claystone package.
/// </summary>
internal sealed class LacustrineBoratePlan
{
    private const ulong PlanSalt = 0x4C41435542524154UL; // "LACUBRAT"

    // Local model floor: y < -30 is below the modelled basin (GRID_Y / 2).
    private const double FloorY = -30.0;
    internal const double BeddingWarpAmplitude = 0.8;

    private readonly int originX;
    private readonly int originY;
    private readonly int originZ;

    private readonly double cosStrike;
    private readonly double sinStrike;
    private readonly double tanDip;
    private readonly double sodiumBias;
    private readonly double seed;
    private readonly double claystoneTop;
    private readonly double claystoneBase;
    private readonly double tuffMarkerLevel;
    private readonly string settingsFamily;

    private readonly BorateLens[] lenses;

    public int OriginX => originX;
    public int OriginY => originY;
    public int OriginZ => originZ;
    public double Seed => seed;
    public double SodiumBias => sodiumBias;
    public int LensCount => lenses.Length;

    private LacustrineBoratePlan(
        in ProceduralDepositInstance instance,
        string family,
        double strikeDeg,
        double dipDeg,
        double sodiumBias,
        double seed,
        double claystoneTop,
        double claystoneBase,
        double tuffMarkerLevel,
        BorateLens[] lenses)
    {
        originX = instance.CenterX;
        originY = instance.CenterY;
        originZ = instance.CenterZ;
        double strikeRad = strikeDeg * Math.PI / 180.0;
        cosStrike = Math.Cos(strikeRad);
        sinStrike = Math.Sin(strikeRad);
        tanDip = Math.Tan(dipDeg * Math.PI / 180.0);
        this.sodiumBias = sodiumBias;
        this.seed = seed;
        this.claystoneTop = claystoneTop;
        this.claystoneBase = claystoneBase;
        this.tuffMarkerLevel = tuffMarkerLevel;
        this.lenses = lenses;
        settingsFamily = family;
    }

    /// <summary>
    /// Builds the deposit geometry in deterministic parameter order.
    /// </summary>
    public static LacustrineBoratePlan Create(
        in ProceduralDepositInstance instance,
        LacustrineBorateDefinition settings)
    {
        var random = new ProceduralDepositRandom(instance.FeatureId ^ PlanSalt);

        double strikeDeg = random.Range(settings.StrikeDegMin, settings.StrikeDegMax);
        double dipDeg = random.Range(settings.DipDegMin, settings.DipDegMax);
        double sodiumBias = random.Range(settings.SodiumBiasMin, settings.SodiumBiasMax);
        // Spring side: the inflow margin the lenses are offset toward.
        double springSide = random.Range(0.0, 1.0) < 0.5 ? -1.0 : 1.0;
        double seed = random.Range(0.0, 100.0);

        int count = random.NextInt(settings.LensCountMin, settings.LensCountMax);
    // Fewer lenses are individually wider, keeping total footprint roughly fixed.
        double scale = Math.Sqrt(2.0 / count);

        var lenses = new BorateLens[count];
        double level = random.Range(settings.FirstLevelMin, settings.FirstLevelMax);
        for (int i = 0; i < count; i++)
        {
            double centerAlong = random.Range(
                    -settings.LensCenterAlongSpread,
                    settings.LensCenterAlongSpread)
                + i * random.Range(-settings.LensCenterAlongDrift, settings.LensCenterAlongDrift);
            double centerAcross = springSide
                * random.Range(settings.LensCenterAcrossMin, settings.LensCenterAcrossMax);
            lenses[i] = new BorateLens(
                centerAlong,
                centerAcross,
                level,
                random.Range(settings.LensRadiusAlongMin, settings.LensRadiusAlongMax) * scale,
                random.Range(settings.LensRadiusAcrossMin, settings.LensRadiusAcrossMax) * scale,
                random.Range(settings.LensHalfThicknessMin, settings.LensHalfThicknessMax),
                seed + i * 23.0,
                random.Range(0.0, 1.0) < settings.PartingChance);
            level += random.Range(settings.LevelStepMin, settings.LevelStepMax);
        }

        return new LacustrineBoratePlan(
            instance,
            settings.Family,
            strikeDeg,
            dipDeg,
            sodiumBias,
            seed,
            settings.ClaystoneTop,
            settings.ClaystoneBase,
            settings.TuffMarkerLevel,
            lenses);
    }

    /// <summary><c>getStratY</c>: bedding-parallel stratigraphic coordinate.</summary>
    public double GetStratY(double x, double y, double z)
    {
        double along = x * cosStrike - z * sinStrike;
        double across = x * sinStrike + z * cosStrike;
        return y - tanDip * across - Math.Sin(along * 0.055 + seed) * BeddingWarpAmplitude;
    }

    public LacustrineBorateSample Evaluate(int worldX, int worldY, int worldZ, int surfaceY = int.MaxValue)
    {
        if (worldY > surfaceY) return default;

        double x = worldX - originX;
        double y = worldY - originY;
        double z = worldZ - originZ;
        if (y < FloorY) return default;

        double along = x * cosStrike - z * sinStrike;
        double across = x * sinStrike + z * cosStrike;
        double stratY = y - tanDip * across - Math.Sin(along * 0.055 + seed) * BeddingWarpAmplitude;

        if (Math.Abs(stratY - tuffMarkerLevel) < 0.3) return default;
        if (stratY <= claystoneBase || stratY >= claystoneTop) return default;

        LensSample? best = null;
        for (int i = 0; i < lenses.Length; i++)
        {
            LensSample sample = SampleLens(along, across, stratY, lenses[i]);
            if (sample.Inside && (!best.HasValue || sample.Footprint < best.Value.Footprint))
            {
                best = sample;
            }
        }

        if (!best.HasValue) return default;

        LensSample lens = best.Value;

        // A thin clay parting splits most lenses at mid-thickness.
        if (lens.HasParting
            && lens.Ratio < 0.08
            && Math.Sin(along * 0.18 + lens.Phase) < -0.15)
        {
            return new LacustrineBorateSample(
                LacustrineBorateZone.ClayParting,
                true,
                lens.Footprint,
                lens.Ratio);
        }

        if (string.Equals(settingsFamily, "alum", StringComparison.OrdinalIgnoreCase))
        {
            double alumFacies = Math.Sin(along * 0.13 + lens.Phase)
                + 0.45 * Math.Cos(across * 0.18 - lens.Phase * 0.4);
            double acidity = lens.Footprint + 0.22 * alumFacies - sodiumBias * 0.3;

            if (lens.Ratio > 0.7 && alumFacies > 0.92)
                return new LacustrineBorateSample(LacustrineBorateZone.Gypsum, true, lens.Footprint, lens.Ratio);
            if (lens.Ratio > 0.66 && alumFacies < -0.95)
                return new LacustrineBorateSample(LacustrineBorateZone.AlumJarosite, true, lens.Footprint, lens.Ratio);
            if (acidity < 0.50)
                return new LacustrineBorateSample(
                    stratY < lens.Level ? LacustrineBorateZone.AlumKalinite : LacustrineBorateZone.AlumAlunogen,
                    true, lens.Footprint, lens.Ratio,
                    stratY < lens.Level ? 1.0 : 0.75);
            if (acidity < 0.84)
                return new LacustrineBorateSample(LacustrineBorateZone.AlumHalotrichite, true, lens.Footprint, lens.Ratio, 0.50);
            return new LacustrineBorateSample(LacustrineBorateZone.AlumAlunite, true, lens.Footprint, lens.Ratio, 0.35);
        }

        double facies = Math.Sin(along * 0.13 + lens.Phase)
            + 0.45 * Math.Cos(across * 0.18 - lens.Phase * 0.4);
        double calcium = lens.Footprint + 0.22 * facies - sodiumBias * 0.3;

        // Lens tops and bases carry sulfate or carbonate rather than borate.
        if (lens.Ratio > 0.7 && facies > 0.92)
        {
            return new LacustrineBorateSample(
                LacustrineBorateZone.Gypsum,
                true,
                lens.Footprint,
                lens.Ratio);
        }
        if (lens.Ratio > 0.66 && facies < -0.95)
        {
            return new LacustrineBorateSample(
                LacustrineBorateZone.Trona,
                true,
                lens.Footprint,
                lens.Ratio);
        }

        if (calcium < 0.58)
        {
            // Deepest-burial cores recrystallize to kernite.
            if (stratY < lens.Level - 0.12 * lens.HalfThickness && lens.Footprint < 0.62)
            {
                return new LacustrineBorateSample(
                    LacustrineBorateZone.Kernite,
                    true,
                    lens.Footprint,
                    lens.Ratio);
            }
            return new LacustrineBorateSample(
                LacustrineBorateZone.Borax,
                true,
                lens.Footprint,
                lens.Ratio);
        }

        if (calcium < 0.88)
        {
            return new LacustrineBorateSample(
                LacustrineBorateZone.Ulexite,
                true,
                lens.Footprint,
                lens.Ratio);
        }

        return new LacustrineBorateSample(
            LacustrineBorateZone.Colemanite,
            true,
            lens.Footprint,
            lens.Ratio);
    }

    /// <summary>
    /// <c>sampleLens</c>: warped elliptical footprint with a tapering bed thickness.
    /// </summary>
    private static LensSample SampleLens(double along, double across, double stratY, in BorateLens lens)
    {
        double u = (along - lens.CenterAlong) / lens.RadiusAlong;
        double v = (across - lens.CenterAcross) / lens.RadiusAcross;
        double warp = Math.Sin(u * 4.0 + lens.Phase) * Math.Cos(v * 3.0) * 0.08;
        double footprint = Math.Sqrt(u * u + v * v) + warp;
        double taper = Clamp(1.0 - footprint * footprint, 0.0, 1.0);
        double half = 0.35 + lens.HalfThickness * taper;
        double distance = Math.Abs(stratY - lens.Level);

        return new LensSample(
            footprint <= 1.03 && distance <= half,
            footprint,
            distance / Math.Max(0.35, half),
            lens.Level,
            lens.HalfThickness,
            lens.Phase,
            lens.HasParting);
    }

    /// <summary>Deterministic spatial hash.</summary>
    internal static double Hash3D(double x, double y, double z)
    {
        double n = Math.Sin(x * 12.9898 + y * 78.233 + z * 37.719) * 43758.5453;
        return n - Math.Floor(n);
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private readonly record struct LensSample(
        bool Inside,
        double Footprint,
        double Ratio,
        double Level,
        double HalfThickness,
        double Phase,
        bool HasParting);

    private readonly record struct BorateLens(
        double CenterAlong,
        double CenterAcross,
        double Level,
        double RadiusAlong,
        double RadiusAcross,
        double HalfThickness,
        double Phase,
        bool HasParting);
}

internal sealed class LacustrineBorateProceduralTemplate : IProceduralDepositTemplate
{
    private static readonly string[] RequiredSlots =
    {
        ProceduralMaterialSlots.Borax,
        ProceduralMaterialSlots.Kernite,
        ProceduralMaterialSlots.Trona,
        ProceduralMaterialSlots.Gypsum
    };

    public string Code => "lacustrineBorate";
    public IReadOnlyList<string> RequiredMaterialSlots => RequiredSlots;
    public bool RequiresPlan => true;

    public int GetMaximumHorizontalReach(ProceduralDepositDefinition definition)
    {
        return definition.LacustrineBorate.HorizontalRadius;
    }

    public object CreatePlan(in ProceduralDepositInstance instance, ProceduralDepositDefinition definition)
    {
        return LacustrineBoratePlan.Create(instance, definition.LacustrineBorate);
    }

    /// <summary>Highest point of the claystone unit above the deposit centre.</summary>
    internal static int GetUnitTopReach(LacustrineBorateDefinition settings)
    {
        return (int)Math.Ceiling(settings.ClaystoneTop
            + LacustrineBoratePlan.BeddingWarpAmplitude
            + Math.Tan(settings.DipDegMax * Math.PI / 180.0) * settings.HorizontalRadius);
    }

    internal static bool ValidateSettings(ProceduralDepositDefinition definition, out string error)
    {
        LacustrineBorateDefinition settings = definition.LacustrineBorate;
        bool valid = (string.Equals(settings.Family, "borate", StringComparison.OrdinalIgnoreCase)
                || string.Equals(settings.Family, "alum", StringComparison.OrdinalIgnoreCase))
            && settings.HorizontalRadius >= 16
            && settings.VerticalHalfHeight >= 8
            && settings.StrikeDegMax >= settings.StrikeDegMin
            && settings.DipDegMin >= 0.0
            && settings.DipDegMax >= settings.DipDegMin
            && settings.DipDegMax < 90.0
            && settings.SodiumBiasMin >= 0.0
            && settings.SodiumBiasMax >= settings.SodiumBiasMin
            && settings.LensCountMin >= 1
            && settings.LensCountMax >= settings.LensCountMin
            && settings.FirstLevelMax >= settings.FirstLevelMin
            && settings.LevelStepMin > 0.0
            && settings.LevelStepMax >= settings.LevelStepMin
            && settings.LensCenterAlongSpread >= 0.0
            && settings.LensCenterAlongDrift >= 0.0
            && settings.LensCenterAcrossMax >= settings.LensCenterAcrossMin
            && settings.LensRadiusAlongMin > 0.0
            && settings.LensRadiusAlongMax >= settings.LensRadiusAlongMin
            && settings.LensRadiusAcrossMin > 0.0
            && settings.LensRadiusAcrossMax >= settings.LensRadiusAcrossMin
            && settings.LensHalfThicknessMin > 0.0
            && settings.LensHalfThicknessMax >= settings.LensHalfThicknessMin
            && settings.PartingChance >= 0.0
            && settings.PartingChance <= 1.0
            && settings.ClaystoneTop > settings.ClaystoneBase
            && settings.TuffMarkerLevel > settings.ClaystoneBase
            && settings.TuffMarkerLevel < settings.ClaystoneTop;
        if (!valid)
        {
            error = "invalid lacustrine evaporite settings";
            return false;
        }

        int topReach = GetUnitTopReach(settings);
        if (definition.Placement.MinDepth < topReach)
        {
            error = $"lacustrine evaporite placement.minDepth must be at least {topReach} to bury the claystone unit";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool Validate(ProceduralDepositDefinition definition, out string error)
    {
        return ValidateSettings(definition, out error)
            && string.Equals(definition.LacustrineBorate.Family, "borate", StringComparison.OrdinalIgnoreCase);
    }

    public void Realize(
        ProceduralDepositWorldGenSystem system,
        in DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        system.RealizeLacustrineBorateCandidate(candidate, request, baseX, baseZ);
    }
}

internal sealed class LacustrineAlumProceduralTemplate : IProceduralDepositTemplate
{
    private static readonly string[] RequiredSlots =
    {
        ProceduralMaterialSlots.Alum,
        ProceduralMaterialSlots.Alunite,
        ProceduralMaterialSlots.Gypsum
    };

    public string Code => "lacustrineAlum";
    public IReadOnlyList<string> RequiredMaterialSlots => RequiredSlots;
    public bool RequiresPlan => true;
    public int GetMaximumHorizontalReach(ProceduralDepositDefinition definition) => definition.LacustrineBorate.HorizontalRadius;
    public object CreatePlan(in ProceduralDepositInstance instance, ProceduralDepositDefinition definition) =>
        LacustrineBoratePlan.Create(instance, definition.LacustrineBorate);
    public bool Validate(ProceduralDepositDefinition definition, out string error)
    {
        return LacustrineBorateProceduralTemplate.ValidateSettings(definition, out error)
            && string.Equals(definition.LacustrineBorate.Family, "alum", StringComparison.OrdinalIgnoreCase);
    }
    public void Realize(ProceduralDepositWorldGenSystem system, in DepositCandidate candidate, IChunkColumnGenerateRequest request, int baseX, int baseZ) =>
        system.RealizeLacustrineBorateCandidate(candidate, request, baseX, baseZ);
}

public sealed partial class ProceduralDepositWorldGenSystem
{
    internal void RealizeLacustrineBorateCandidate(
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        CompiledProceduralDeposit compiled = candidate.Compiled;
        LacustrineBorateDefinition settings = compiled.Definition.LacustrineBorate;
        var plan = (LacustrineBoratePlan)candidate.Plan!;
        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;
        int worldHeight = serverApi!.World.BlockAccessor.MapSizeY;
        int minimumY = GameMath.Clamp(candidate.Instance.CenterY - settings.VerticalHalfHeight, 1, worldHeight - 2);
        int maximumPrototypeY = GameMath.Clamp(candidate.Instance.CenterY + settings.VerticalHalfHeight, 1, worldHeight - 2);

        int[] zoneSlots = BuildLacustrineBorateZoneSlots(compiled);

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
                    LacustrineBorateSample sample = plan.Evaluate(worldX, y, worldZ, surfaceY);
                    if (sample.Zone == LacustrineBorateZone.None) continue;
                    if (sample.Density < 1.0
                        && ProceduralDepositMath.CoordinateNoise(
                            candidate.Instance.FeatureId,
                            worldX,
                            y,
                            worldZ,
                            0x414C554D44454E53UL) >= sample.Density)
                    {
                        continue;
                    }

                    int targetSlot = zoneSlots[(int)sample.Zone];
                    if (targetSlot < 0) continue;

                    int chunkY = y / ChunkSize;
                    if ((uint)chunkY >= (uint)request.Chunks.Length) continue;
                    int localY = y % ChunkSize;
                    int index3d = ((localY * ChunkSize) + localZ) * ChunkSize + localX;
                    IChunkBlocks data = request.Chunks[chunkY].Data;
                    int hostBlockId = data.GetBlockIdUnsafe(index3d);

                    if (!CanReplaceWithProceduralRock(compiled, hostBlockId)) continue;

                    int placeBlockId = compiled.ResolveBlock(targetSlot, hostBlockId);
                    if (placeBlockId == 0 || placeBlockId == hostBlockId) continue;

                    data.SetBlockUnsafe(index3d, placeBlockId);
                    AdvancedGeology.Byproducts.ByproductSystem.RecordPlacement(request.Chunks[chunkY], index3d, placeBlockId, candidate.Instance.FeatureId, compiled);
                    data.SetFluid(index3d, 0);
                }
            }
        }
    }

    private static int[] BuildLacustrineBorateZoneSlots(CompiledProceduralDeposit compiled)
    {
        var slots = new int[Enum.GetValues<LacustrineBorateZone>().Length];
        Array.Fill(slots, -1);

        if (string.Equals(compiled.Definition.LacustrineBorate.Family, "alum", StringComparison.OrdinalIgnoreCase))
        {
            int alumSlot = compiled.GetSlotId(ProceduralMaterialSlots.Alum);
            slots[(int)LacustrineBorateZone.AlumKalinite] = alumSlot;
            slots[(int)LacustrineBorateZone.AlumAlunogen] = alumSlot;
            slots[(int)LacustrineBorateZone.AlumHalotrichite] = alumSlot;
            slots[(int)LacustrineBorateZone.AlumAlunite] = compiled.GetSlotId(ProceduralMaterialSlots.Alunite);
        }
        else
        {
            int boraxSlot = compiled.GetSlotId(ProceduralMaterialSlots.Borax);
            slots[(int)LacustrineBorateZone.Borax] = boraxSlot;
            slots[(int)LacustrineBorateZone.Ulexite] = boraxSlot;
            slots[(int)LacustrineBorateZone.Colemanite] = boraxSlot;
            slots[(int)LacustrineBorateZone.Kernite] = compiled.GetSlotId(ProceduralMaterialSlots.Kernite);
            slots[(int)LacustrineBorateZone.Trona] = compiled.GetSlotId(ProceduralMaterialSlots.Trona);
        }

        slots[(int)LacustrineBorateZone.Gypsum] = compiled.GetSlotId(ProceduralMaterialSlots.Gypsum);
        slots[(int)LacustrineBorateZone.ClayParting] = compiled.GetSlotId(ProceduralMaterialSlots.ClayParting);
        return slots;
    }
}
