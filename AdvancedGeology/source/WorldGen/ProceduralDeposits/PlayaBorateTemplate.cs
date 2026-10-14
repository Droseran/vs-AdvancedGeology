using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using Vintagestory.API.Server;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace AdvancedGeology.WorldGen.ProceduralDeposits;

public enum PlayaBorateZone
{
    None = 0,
    BoraxCrust,
    UlexiteNodule,
    ColemaniteNodule,
    GypsumCrust
}

[JsonObject(MemberSerialization.OptIn)]
public sealed class PlayaBorateDefinition
{
    // Model extent is 72x60. The playa ellipse reaches 30 along x and 24 along z, and the
    // alluvial fan extends beyond playaRadiusX * 0.72 + 8.
    [JsonProperty]
    public int HorizontalRadius { get; set; } = 36;

    [JsonProperty]
    public int VerticalHalfHeight { get; set; } = 18;

    [JsonProperty]
    public double PlayaRadiusXMin { get; set; } = 22.0;

    [JsonProperty]
    public double PlayaRadiusXMax { get; set; } = 30.0;

    [JsonProperty]
    public double PlayaRadiusZMin { get; set; } = 16.0;

    [JsonProperty]
    public double PlayaRadiusZMax { get; set; } = 24.0;

    [JsonProperty]
    public double PlayaCenterSpread { get; set; } = 5.0;

    [JsonProperty]
    public double FanWidthMin { get; set; } = 12.0;

    [JsonProperty]
    public double FanWidthMax { get; set; } = 20.0;

    [JsonProperty]
    public double SodiumBiasMin { get; set; } = 0.3;

    [JsonProperty]
    public double SodiumBiasMax { get; set; } = 0.8;

    [JsonProperty]
    public int NoduleCountMin { get; set; } = 85;

    [JsonProperty]
    public int NoduleCountMax { get; set; } = 130;

    [JsonProperty]
    public double NoduleDepthMin { get; set; } = 0.6;

    [JsonProperty]
    public double NoduleDepthMax { get; set; } = 7.5;

    [JsonProperty]
    public double NoduleRadiusMin { get; set; } = 1.3;

    [JsonProperty]
    public double NoduleRadiusMax { get; set; } = 2.7;

    [JsonProperty]
    public double NoduleSquashMin { get; set; } = 0.45;

    [JsonProperty]
    public double NoduleSquashMax { get; set; } = 0.8;

    [JsonProperty]
    public double CalciumJitter { get; set; } = 0.25;

    [JsonProperty]
    public double ColemaniteThreshold { get; set; } = 0.45;
}

/// <summary>
/// Result of classifying one voxel in a playa evaporite basin.
/// </summary>
public readonly record struct PlayaBorateSample(
    PlayaBorateZone Zone,
    double PlayaFactor = 0.0,
    double Depth = 0.0,
    bool InNodule = false);

/// <summary>
/// Borax, ulexite, colemanite, and gypsum form a shallow evaporite crust and depth-zoned nodules within a warped playa basin truncated by an alluvial fan.
/// </summary>
internal sealed class PlayaBoratePlan
{
    private const ulong PlanSalt = 0x504C41594142524FUL; // "PLAYABRO"

    // Basin limits, measured down from the column's top rock.
    private const double CrustDepthLimit = 1.6;
    private const double CrustPlayaMinimum = 0.22;
    private const double NoduleMinimumPlaya = 0.1;
    internal const int PlayaFillDepthLimit = 11;
    private const double FanSuppressionThreshold = 0.12;

    private readonly ulong featureId;
    private readonly int originX;
    private readonly int originY;
    private readonly int originZ;

    private readonly double playaRadiusX;
    private readonly double playaRadiusZ;
    private readonly double playaCx;
    private readonly double playaCz;
    private readonly double fanSide;
    private readonly double fanWidth;
    private readonly double sodiumBias;
    private readonly double seed;

    private readonly Nodule[] nodules;

    public int OriginX => originX;
    public int OriginY => originY;
    public int OriginZ => originZ;
    public double Seed => seed;
    public double SodiumBias => sodiumBias;
    public double PlayaRadiusX => playaRadiusX;
    public double PlayaRadiusZ => playaRadiusZ;
    public int NoduleCount => nodules.Length;

    public int UlexiteNoduleCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < nodules.Length; i++)
            {
                if (nodules[i].Zone == PlayaBorateZone.UlexiteNodule) count++;
            }

            return count;
        }
    }

    public int ColemaniteNoduleCount => nodules.Length - UlexiteNoduleCount;

    private PlayaBoratePlan(
        ulong featureId,
        in ProceduralDepositInstance instance,
        double playaRadiusX,
        double playaRadiusZ,
        double playaCx,
        double playaCz,
        double fanSide,
        double fanWidth,
        double sodiumBias,
        double seed,
        Nodule[] nodules)
    {
        this.featureId = featureId;
        originX = instance.CenterX;
        originY = instance.CenterY;
        originZ = instance.CenterZ;
        this.playaRadiusX = playaRadiusX;
        this.playaRadiusZ = playaRadiusZ;
        this.playaCx = playaCx;
        this.playaCz = playaCz;
        this.fanSide = fanSide;
        this.fanWidth = fanWidth;
        this.sodiumBias = sodiumBias;
        this.seed = seed;
        this.nodules = nodules;
    }

    /// <summary>
    /// Builds the deposit geometry in deterministic parameter order.
    /// </summary>
    public static PlayaBoratePlan Create(
        in ProceduralDepositInstance instance,
        PlayaBorateDefinition settings)
    {
        var random = new ProceduralDepositRandom(instance.FeatureId ^ PlanSalt);

        double seed = random.Range(0.0, 100.0);
        double playaRadiusX = random.Range(settings.PlayaRadiusXMin, settings.PlayaRadiusXMax);
        double playaRadiusZ = random.Range(settings.PlayaRadiusZMin, settings.PlayaRadiusZMax);
        double playaCx = random.Range(-settings.PlayaCenterSpread, settings.PlayaCenterSpread);
        double playaCz = random.Range(-settings.PlayaCenterSpread, settings.PlayaCenterSpread);
        double fanSide = random.Range(0.0, 1.0) < 0.5 ? -1.0 : 1.0;
        double fanWidth = random.Range(settings.FanWidthMin, settings.FanWidthMax);
        double sodiumBias = random.Range(settings.SodiumBiasMin, settings.SodiumBiasMax);

        int count = random.NextInt(settings.NoduleCountMin, settings.NoduleCountMax);
        var nodules = new Nodule[count];
        for (int i = 0; i < count; i++)
        {
            double ang = random.Range(0.0, 1.0) * Math.PI * 2.0;
            double bias = Math.Pow(random.Range(0.0, 1.0), 0.55);
            double rx = Math.Cos(ang) * bias * playaRadiusX * 0.9;
            double rz = Math.Sin(ang) * bias * playaRadiusZ * 0.9;
            double depthBelow = random.Range(settings.NoduleDepthMin, settings.NoduleDepthMax);
            double calcium = depthBelow / settings.NoduleDepthMax
                + random.Range(-settings.CalciumJitter, settings.CalciumJitter)
                - sodiumBias * 0.25;

            nodules[i] = new Nodule(
                playaCx + rx,
                playaCz + rz,
                depthBelow,
                random.Range(settings.NoduleRadiusMin, settings.NoduleRadiusMax),
                random.Range(settings.NoduleSquashMin, settings.NoduleSquashMax),
                calcium > settings.ColemaniteThreshold
                    ? PlayaBorateZone.ColemaniteNodule
                    : PlayaBorateZone.UlexiteNodule);
        }

        return new PlayaBoratePlan(
            instance.FeatureId,
            instance,
            playaRadiusX,
            playaRadiusZ,
            playaCx,
            playaCz,
            fanSide,
            fanWidth,
            sodiumBias,
            seed,
            nodules);
    }

    /// <summary><c>playaFactor</c>: warped elliptical basin membership.</summary>
    public double PlayaFactor(double x, double z)
    {
        double u = (x - playaCx) / playaRadiusX;
        double v = (z - playaCz) / playaRadiusZ;
        double warp = Math.Sin(u * 3.6 + seed) * 0.06 + Math.Cos(v * 3.1 - seed * 0.4) * 0.06;
        return Clamp(1.0 - (Math.Sqrt(u * u + v * v) + warp), 0.0, 1.0);
    }

    /// <summary><c>fanFactor</c>: the alluvial fan entering from one basin margin.</summary>
    public double FanFactor(double x, double z)
    {
        double across = x * fanSide;
        double along = z;
        double edge = playaRadiusX * 0.72;
        double reach = Clamp((across - edge) / 8.0, 0.0, 1.0);
        double lateral = Clamp(1.0 - Math.Abs(along) / fanWidth, 0.0, 1.0);
        return reach * lateral;
    }

    public bool CoversColumn(int worldX, int worldZ)
    {
        double x = worldX - originX;
        double z = worldZ - originZ;
        return FanFactor(x, z) <= FanSuppressionThreshold && PlayaFactor(x, z) > 0.0;
    }

    /// <summary>
    /// Classifies a voxel against the playa. <paramref name="datumY"/> is the top natural-rock Y of the column.
    /// </summary>
    public PlayaBorateSample Evaluate(int worldX, int worldY, int worldZ, int datumY)
    {
        if (worldY > datumY) return default;

        double x = worldX - originX;
        double z = worldZ - originZ;

        if (FanFactor(x, z) > FanSuppressionThreshold) return default;

        double playa = PlayaFactor(x, z);
        double depth = datumY - worldY;

        if (playa <= 0.0) return default;
        if (depth > PlayaFillDepthLimit) return default;

        double crustNoise = Math.Sin(x * 0.24 + seed) + 0.7 * Math.Cos(z * 0.21 - seed * 0.5);
        double patch = Math.Sin(x * 0.62 - seed * 0.7) * Math.Cos(z * 0.55 + seed * 0.4);

        if (depth <= CrustDepthLimit && playa > CrustPlayaMinimum && patch > -0.15)
        {
            if (crustNoise > 0.6 - sodiumBias * 0.6)
            {
                return new PlayaBorateSample(PlayaBorateZone.BoraxCrust, playa, depth, false);
            }

            if (crustNoise < -0.85)
            {
                return new PlayaBorateSample(PlayaBorateZone.GypsumCrust, playa, depth, false);
            }
        }

        PlayaBorateZone nodule = SampleNodule(x, depth, z);
        if (nodule != PlayaBorateZone.None && playa > NoduleMinimumPlaya)
        {
            return new PlayaBorateSample(nodule, playa, depth, true);
        }

        return new PlayaBorateSample(PlayaBorateZone.None, playa, depth, false);
    }

    /// <summary><c>sampleNodule</c>: squashed spheroidal borate nodules.</summary>
    private PlayaBorateZone SampleNodule(double x, double depth, double z)
    {
        for (int i = 0; i < nodules.Length; i++)
        {
            Nodule n = nodules[i];
            double dx = (x - n.X) / n.Radius;
            double dz = (z - n.Z) / n.Radius;
            double dy = (n.DepthBelow - depth) / (n.Radius * n.Squash);
            if (dx * dx + dy * dy + dz * dz <= 1.0) return n.Zone;
        }

        return PlayaBorateZone.None;
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

    private readonly record struct Nodule(
        double X,
        double Z,
        double DepthBelow,
        double Radius,
        double Squash,
        PlayaBorateZone Zone);
}

internal sealed class PlayaBorateProceduralTemplate : IProceduralDepositTemplate
{
    private static readonly string[] RequiredSlots =
    {
        ProceduralMaterialSlots.Borax,
        ProceduralMaterialSlots.Gypsum
    };

    public string Code => "playaBorate";
    public IReadOnlyList<string> RequiredMaterialSlots => RequiredSlots;
    public bool RequiresPlan => true;

    public int GetMaximumHorizontalReach(ProceduralDepositDefinition definition)
    {
        return definition.PlayaBorate.HorizontalRadius;
    }

    public object CreatePlan(in ProceduralDepositInstance instance, ProceduralDepositDefinition definition)
    {
        return PlayaBoratePlan.Create(instance, definition.PlayaBorate);
    }

    public bool Validate(ProceduralDepositDefinition definition, out string error)
    {
        PlayaBorateDefinition settings = definition.PlayaBorate;
        bool valid = settings.HorizontalRadius >= 16
            && settings.VerticalHalfHeight >= 8
            && settings.PlayaRadiusXMin > 0.0
            && settings.PlayaRadiusXMax >= settings.PlayaRadiusXMin
            && settings.PlayaRadiusZMin > 0.0
            && settings.PlayaRadiusZMax >= settings.PlayaRadiusZMin
            && settings.FanWidthMin > 0.0
            && settings.FanWidthMax >= settings.FanWidthMin
            && settings.SodiumBiasMin >= 0.0
            && settings.SodiumBiasMax >= settings.SodiumBiasMin
            && settings.NoduleCountMin >= 0
            && settings.NoduleCountMax >= settings.NoduleCountMin
            && settings.NoduleDepthMin > 0.0
            && settings.NoduleDepthMax >= settings.NoduleDepthMin
            && settings.NoduleRadiusMin > 0.0
            && settings.NoduleRadiusMax >= settings.NoduleRadiusMin
            && settings.NoduleSquashMin > 0.0
            && settings.NoduleSquashMax >= settings.NoduleSquashMin
            && settings.CalciumJitter >= 0.0;
        error = valid ? string.Empty : "invalid playa borate settings";
        return valid;
    }

    public void Realize(
        ProceduralDepositWorldGenSystem system,
        in DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        system.RealizePlayaBorateCandidate(candidate, request, baseX, baseZ);
    }
}

public sealed partial class ProceduralDepositWorldGenSystem
{
    internal void RealizePlayaBorateCandidate(
        DepositCandidate candidate,
        IChunkColumnGenerateRequest request,
        int baseX,
        int baseZ)
    {
        CompiledProceduralDeposit compiled = candidate.Compiled;
        var plan = (PlayaBoratePlan)candidate.Plan!;
        ushort[] heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;

        int[] zoneSlots = BuildPlayaBorateZoneSlots(compiled);

        for (int localX = 0; localX < ChunkSize; localX++)
        {
            int worldX = baseX + localX;
            for (int localZ = 0; localZ < ChunkSize; localZ++)
            {
                int worldZ = baseZ + localZ;
                int surfaceY = heightMap[localZ * ChunkSize + localX];
                if (!plan.CoversColumn(worldX, worldZ)) continue;
                if (!TryFindTopNaturalRock(compiled, request, localX, localZ, surfaceY, out int datumY)) continue;
                int minimumY = Math.Max(1, datumY - PlayaBoratePlan.PlayaFillDepthLimit);

                for (int y = minimumY; y <= datumY; y++)
                {
                    PlayaBorateSample sample = plan.Evaluate(worldX, y, worldZ, datumY);
                    if (sample.Zone == PlayaBorateZone.None) continue;

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

    private const int TopRockSearchDepth = 32;

    private static bool TryFindTopNaturalRock(
        CompiledProceduralDeposit compiled,
        IChunkColumnGenerateRequest request,
        int localX,
        int localZ,
        int surfaceY,
        out int rockY)
    {
        int lowestY = Math.Max(1, surfaceY - TopRockSearchDepth);
        for (int y = surfaceY; y >= lowestY; y--)
        {
            int chunkY = y / ChunkSize;
            if ((uint)chunkY >= (uint)request.Chunks.Length) continue;
            int index3d = (((y % ChunkSize) * ChunkSize) + localZ) * ChunkSize + localX;
            if (!compiled.IsNaturalRock(request.Chunks[chunkY].Data.GetBlockIdUnsafe(index3d))) continue;
            rockY = y;
            return true;
        }

        rockY = 0;
        return false;
    }

    private static int[] BuildPlayaBorateZoneSlots(CompiledProceduralDeposit compiled)
    {
        var slots = new int[Enum.GetValues<PlayaBorateZone>().Length];
        Array.Fill(slots, -1);

        // Borax, ulexite and colemanite are all the same mineable borax block.
        int boraxSlot = compiled.GetSlotId(ProceduralMaterialSlots.Borax);
        slots[(int)PlayaBorateZone.BoraxCrust] = boraxSlot;
        slots[(int)PlayaBorateZone.UlexiteNodule] = boraxSlot;
        slots[(int)PlayaBorateZone.ColemaniteNodule] = boraxSlot;

        slots[(int)PlayaBorateZone.GypsumCrust] = compiled.GetSlotId(ProceduralMaterialSlots.Gypsum);
        return slots;
    }
}
