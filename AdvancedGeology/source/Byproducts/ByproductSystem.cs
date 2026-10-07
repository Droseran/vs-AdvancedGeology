using AdvancedGeology.WorldGen.ProceduralDeposits;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AdvancedGeology.Byproducts;

/// <summary>
/// Stores sparse, server-only provenance for procedurally placed ore carrying configured byproducts.
/// The rule table remains in compiled definitions; chunk records contain only stable identities.
/// </summary>
public static class ByproductSystem
{
    private const string ModDataKey = "advancedgeology:byproduct-provenance";
    private const uint Magic = 0x42504741; // AGPB in little-endian byte order.
    private const byte FormatVersion = 1;
    private const int HeaderSize = sizeof(uint) + sizeof(byte) + sizeof(int);
    private const int RecordSize = sizeof(int) * 3 + sizeof(ulong) * 2;
    private const int MaximumEntries = GlobalConstants.ChunkSize * GlobalConstants.ChunkSize * GlobalConstants.ChunkSize;
    // Fractions are relative to the ore item's metal units. Nonmetal ItemOre uses one unit.
    private const double NoiseScale = 96.0;


    private static readonly object RegistryLock = new();
    private static readonly Dictionary<ulong, CompiledProceduralDeposit> DefinitionsByHash = new();
    private static ConditionalWeakTable<IWorldChunk, ProvenanceCache> caches = new();

    /// <summary>Clears world-specific compiled definition registrations during world transitions.</summary>
    public static void Initialize()
    {
        lock (RegistryLock) DefinitionsByHash.Clear();
        caches = new ConditionalWeakTable<IWorldChunk, ProvenanceCache>();
    }

    /// <summary>Clears world-specific compiled definition registrations during shutdown.</summary>
    public static void Dispose()
    {
        lock (RegistryLock) DefinitionsByHash.Clear();
        caches = new ConditionalWeakTable<IWorldChunk, ProvenanceCache>();
    }

    internal static bool Register(CompiledProceduralDeposit compiled, out string error)
    {
        lock (RegistryLock)
        {
            if (DefinitionsByHash.TryGetValue(compiled.CodeHash, out CompiledProceduralDeposit? existing)
                && !string.Equals(existing.Definition.Code, compiled.Definition.Code, StringComparison.Ordinal))
            {
                error = $"deposit codes '{existing.Definition.Code}' and '{compiled.Definition.Code}' have the same byproduct identity hash";
                return false;
            }
            DefinitionsByHash[compiled.CodeHash] = compiled;
        }
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Clears stale provenance at this local chunk index, then records it only when the placed block
    /// unambiguously belongs to a configured ore slot. Intended to follow every procedural block write.
    /// </summary>
    internal static void RecordPlacement(
        IWorldChunk chunk,
        int index3d,
        int blockId,
        ulong featureId,
        CompiledProceduralDeposit compiled)
    {
        if (chunk is not IServerChunk serverChunk || (uint)index3d >= MaximumEntries) return;
        int slotId = -1;
        bool configured = compiled.HasByproducts
            && compiled.TryGetByproductSlotForBlock(blockId, out slotId);
        if (!configured
            && !caches.TryGetValue(chunk, out _)
            && serverChunk.GetServerModdata(ModDataKey) is not { Length: > 0 })
        {
            return;
        }

        ProvenanceCache cache = GetCache(chunk, serverChunk);
        lock (cache)
        {
            bool removed = cache.Entries.Remove(index3d);
            if (!configured)
            {
                cache.Dirty |= removed;
                return;
            }

            cache.Valid = true;
            cache.Entries[index3d] = new ProvenanceRecord(blockId, featureId, compiled.CodeHash, slotId);
            cache.Dirty = true;
        }
    }

    /// <summary>Serializes pending worldgen provenance once after all writes to this chunk complete.</summary>
    public static void FlushChunk(IWorldChunk chunk)
    {
        if (chunk is not IServerChunk serverChunk || !caches.TryGetValue(chunk, out ProvenanceCache? cache))
            return;

        lock (cache)
        {
            if (!cache.Dirty) return;
            Persist(serverChunk, cache.Entries);
            cache.Dirty = false;
        }
    }

    internal static CompiledProceduralDeposit? GetBolivianSilverDeposit()
    {
        lock (RegistryLock)
        {
            DefinitionsByHash.TryGetValue(ProceduralDepositMath.HashString("bolivian-porphyry-tin"), out var compiled);
            return compiled != null && compiled.TryGetByproductRule(compiled.GetSlotId(ProceduralMaterialSlots.Galena), out _)
                ? compiled : null;
        }
    }

    /// <summary>Adds missing Bolivian galena provenance without overwriting existing deposit records.</summary>
    internal static bool TryRetrofitBolivianGalena(IWorldChunk chunk, int index3d, int blockId, CompiledProceduralDeposit compiled)
    {
        if (chunk is not IServerChunk serverChunk || (uint)index3d >= MaximumEntries
            || !compiled.TryGetByproductSlotForBlock(blockId, out int slot)
            || slot != compiled.GetSlotId(ProceduralMaterialSlots.Galena)) return false;
        ProvenanceCache cache = GetCache(chunk, serverChunk);
        lock (cache)
        {
            if (!cache.Valid || cache.Entries.ContainsKey(index3d)) return false;
            cache.Entries.Add(index3d, new ProvenanceRecord(blockId, 0, compiled.CodeHash, slot));
            cache.Dirty = true;
            return true;
        }
    }

    /// <summary>Clears provenance when a caller overwrites a local chunk index without a compiled deposit.</summary>
    public static void ClearPlacement(IWorldChunk chunk, int index3d)
    {
        if (chunk is not IServerChunk serverChunk || (uint)index3d >= MaximumEntries) return;
        if (!caches.TryGetValue(chunk, out _)
            && serverChunk.GetServerModdata(ModDataKey) is not { Length: > 0 })
        {
            return;
        }

        ProvenanceCache cache = GetCache(chunk, serverChunk);
        lock (cache)
        {
            cache.Dirty |= cache.Entries.Remove(index3d);
        }
    }

    /// <summary>
    /// Returns configured trace units per ore item. Ore without a valid deposit record and
    /// unconfigured slots carry no composition; noise varies configured ranges spatially.
    /// </summary>
    public static bool TryGetComposition(
        IWorldAccessor world,
        BlockPos pos,
        int expectedBlockId,
        ItemStack oreStack,
        out IReadOnlyDictionary<string, double> composition)
    {
        composition = EmptyComposition.Instance;
        if (world == null || pos == null || oreStack?.Collectible == null || expectedBlockId <= 0
            || world.BlockAccessor.GetBlockId(pos) != expectedBlockId) return false;

        int oreUnits = oreStack.ItemAttributes?["metalUnits"].AsInt(0) ?? 0;
        if (oreUnits <= 0) oreUnits = 1;

        CompiledByproductRule? rule = null;
        ProvenanceRecord record = default;
        IWorldChunk? chunk = world.BlockAccessor.GetChunkAtBlockPos(pos);
        if (chunk is IServerChunk serverChunk
            && (caches.TryGetValue(chunk, out _)
                || serverChunk.GetServerModdata(ModDataKey) is { Length: > 0 }))
        {
            ProvenanceCache cache = GetCache(chunk, serverChunk);
            lock (cache)
            {
                cache.Entries.TryGetValue(ToLocalIndex(pos), out record);
            }
            if (record.BlockId == expectedBlockId)
            {
                CompiledProceduralDeposit? compiled;
                lock (RegistryLock) DefinitionsByHash.TryGetValue(record.CodeHash, out compiled);
                if (compiled != null
                    && compiled.TryGetByproductSlotForBlock(record.BlockId, out int slot)
                    && slot == record.SlotId
                    && compiled.TryGetByproductRule(slot, out CompiledByproductRule resolved)) rule = resolved;
            }
        }
        if (rule == null) return false;
        composition = GetComposition(unchecked((ulong)world.Seed), pos.X, pos.Z, oreUnits, rule);
        return composition.Count > 0;
    }

    /// <summary>Computes contained units using one smooth, world-seeded field per element.</summary>
    internal static IReadOnlyDictionary<string, double> GetComposition(
        ulong worldSeed, int x, int z, int oreUnits, CompiledByproductRule rule)
    {
        var result = new Dictionary<string, double>(rule.Elements.Count, StringComparer.Ordinal);
        double noiseX = x / NoiseScale;
        double noiseZ = z / NoiseScale;
        foreach ((string element, double[] range) in rule.Elements)
        {
            double noise = ProceduralDepositMath.SmoothNoise2D(worldSeed, noiseX, noiseZ,
                ProceduralDepositMath.HashString(element));
            result[element] = oreUnits * (range[0] + (range[1] - range[0]) * noise);
        }
        return result;
    }

    /// <summary>Removes provenance for a block position if its server chunk is loaded.</summary>
    public static void Remove(IWorldAccessor world, BlockPos pos)
    {
        if (world == null || pos == null) return;
        IWorldChunk? chunk = world.BlockAccessor.GetChunkAtBlockPos(pos);
        if (chunk is not IServerChunk serverChunk) return;
        if (!caches.TryGetValue(chunk, out _)
            && serverChunk.GetServerModdata(ModDataKey) is not { Length: > 0 }) return;

        ProvenanceCache cache = GetCache(chunk, serverChunk);
        lock (cache)
        {
            if (!cache.Entries.Remove(ToLocalIndex(pos))) return;
            Persist(serverChunk, cache.Entries);
        }
    }

    private static int ToLocalIndex(BlockPos pos)
    {
        int size = GlobalConstants.ChunkSize;
        int localX = FloorMod(pos.X, size);
        int localY = FloorMod(pos.InternalY, size);
        int localZ = FloorMod(pos.Z, size);
        return ((localY * size) + localZ) * size + localX;
    }

    private static int FloorMod(int value, int divisor)
    {
        int result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static ProvenanceCache GetCache(IWorldChunk chunk, IServerChunk serverChunk) =>
        caches.GetValue(chunk, _ => Deserialize(serverChunk.GetServerModdata(ModDataKey)));

    private static ProvenanceCache Deserialize(byte[]? data)
    {
        var entries = new Dictionary<int, ProvenanceRecord>();
        if (data == null || data.Length == 0) return new ProvenanceCache(entries, true);
        if (data.Length < HeaderSize) return new ProvenanceCache(entries, false);

        ReadOnlySpan<byte> input = data;
        if (BinaryPrimitives.ReadUInt32LittleEndian(input) != Magic || input[sizeof(uint)] != FormatVersion)
        {
            return new ProvenanceCache(entries, false);
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(input[(sizeof(uint) + sizeof(byte))..]);
        if (count < 0 || count > MaximumEntries || data.Length != HeaderSize + count * RecordSize)
        {
            return new ProvenanceCache(entries, false);
        }

        int offset = HeaderSize;
        for (int i = 0; i < count; i++, offset += RecordSize)
        {
            int index3d = BinaryPrimitives.ReadInt32LittleEndian(input[offset..]);
            int blockId = BinaryPrimitives.ReadInt32LittleEndian(input[(offset + 4)..]);
            ulong featureId = BinaryPrimitives.ReadUInt64LittleEndian(input[(offset + 8)..]);
            ulong codeHash = BinaryPrimitives.ReadUInt64LittleEndian(input[(offset + 16)..]);
            int slotId = BinaryPrimitives.ReadInt32LittleEndian(input[(offset + 24)..]);
            if ((uint)index3d >= MaximumEntries || blockId <= 0 || slotId < 0
                || !entries.TryAdd(index3d, new ProvenanceRecord(blockId, featureId, codeHash, slotId)))
            {
                return new ProvenanceCache(new Dictionary<int, ProvenanceRecord>(), false);
            }
        }
        return new ProvenanceCache(entries, true);
    }

    private static void Persist(IServerChunk chunk, Dictionary<int, ProvenanceRecord> entries)
    {
        if (entries.Count == 0)
        {
            chunk.SetServerModdata(ModDataKey, Array.Empty<byte>());
            chunk.MarkModified();
            return;
        }

        byte[] data = new byte[HeaderSize + entries.Count * RecordSize];
        Span<byte> output = data;
        BinaryPrimitives.WriteUInt32LittleEndian(output, Magic);
        output[sizeof(uint)] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(output[(sizeof(uint) + sizeof(byte))..], entries.Count);

        int offset = HeaderSize;
        foreach ((int index3d, ProvenanceRecord record) in entries.OrderBy(entry => entry.Key))
        {
            BinaryPrimitives.WriteInt32LittleEndian(output[offset..], index3d);
            BinaryPrimitives.WriteInt32LittleEndian(output[(offset + 4)..], record.BlockId);
            BinaryPrimitives.WriteUInt64LittleEndian(output[(offset + 8)..], record.FeatureId);
            BinaryPrimitives.WriteUInt64LittleEndian(output[(offset + 16)..], record.CodeHash);
            BinaryPrimitives.WriteInt32LittleEndian(output[(offset + 24)..], record.SlotId);
            offset += RecordSize;
        }
        chunk.SetServerModdata(ModDataKey, data);
        chunk.MarkModified();
    }

    private readonly record struct ProvenanceRecord(int BlockId, ulong FeatureId, ulong CodeHash, int SlotId);

    private sealed class ProvenanceCache(Dictionary<int, ProvenanceRecord> entries, bool valid)
    {
        public Dictionary<int, ProvenanceRecord> Entries { get; } = entries;
        public bool Valid { get; set; } = valid;
        public bool Dirty { get; set; }
    }

    private sealed class EmptyComposition : IReadOnlyDictionary<string, double>
    {
        public static readonly EmptyComposition Instance = new();
        public int Count => 0;
        public IEnumerable<string> Keys => Array.Empty<string>();
        public IEnumerable<double> Values => Array.Empty<double>();
        public double this[string key] => throw new KeyNotFoundException();
        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out double value) { value = default; return false; }
        public IEnumerator<KeyValuePair<string, double>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, double>>)Array.Empty<KeyValuePair<string, double>>()).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
