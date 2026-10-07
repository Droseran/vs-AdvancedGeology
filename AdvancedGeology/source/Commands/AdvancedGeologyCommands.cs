using System.Collections.Generic;
using AdvancedGeology.WorldGen.ProceduralDeposits;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AdvancedGeology;

/// <summary>
/// Server test commands. `/ag delrock &lt;rocktype&gt;` clears one natural rock variant from the
/// caller's chunk and the eight chunks around it, exposing buried deposit geometry in place.
/// </summary>
internal static class AdvancedGeologyCommands
{
    public static void Register(ICoreServerAPI api)
    {
        api.ChatCommands
            .GetOrCreate("ag")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("delrock")
            .WithDescription("Deletes every block of one rock variant within a one chunk radius, e.g. /ag delrock sandstone")
            .WithArgs(api.ChatCommands.Parsers.Word("rocktype"))
            .HandleWith(args => DeleteRock(api, args))
            .EndSubCommand()
            .BeginSubCommand("retrotinsilver")
            .WithDescription("Adds silver to untagged galena in a sphere around you at a known Bolivian tin vein. Radius 1-64 blocks; loaded chunks only.")
            .RequiresPlayer()
            .WithArgs(api.ChatCommands.Parsers.Int("radius"))
            .HandleWith(args => RetrofitTinSilver(api, args))
            .EndSubCommand();
    }

    private static TextCommandResult RetrofitTinSilver(ICoreServerAPI api, TextCommandCallingArgs args)
    {
        int radius = (int)args[0];
        if (radius < 1 || radius > 64) return TextCommandResult.Error("Radius must be 1-64 blocks.");
        CompiledProceduralDeposit? compiled = Byproducts.ByproductSystem.GetBolivianSilverDeposit();
        if (compiled == null) return TextCommandResult.Error("Bolivian tin silver rules are not loaded. Nothing changed.");
        BlockPos center = args.Caller.Pos.AsBlockPos;
        int size = GlobalConstants.ChunkSize;
        int minY = System.Math.Max(1, center.Y - radius);
        int maxY = System.Math.Min(api.World.BlockAccessor.MapSizeY - 1, center.Y + radius);
        var chunks = new Dictionary<(int X, int Y, int Z), IWorldChunk>();
        var pos = new BlockPos(0, 0, 0, center.dimension);
        for (int cx = ProceduralDepositMath.FloorDiv(center.X - radius, size); cx <= ProceduralDepositMath.FloorDiv(center.X + radius, size); cx++)
        for (int cz = ProceduralDepositMath.FloorDiv(center.Z - radius, size); cz <= ProceduralDepositMath.FloorDiv(center.Z + radius, size); cz++)
        for (int cy = minY / size; cy <= maxY / size; cy++)
        {
            pos.Set(cx * size, cy * size, cz * size);
            IWorldChunk? chunk = api.World.BlockAccessor.GetChunkAtBlockPos(pos);
            if (chunk is not IServerChunk) return TextCommandResult.Error("Entire selected area must be loaded; move closer or reduce the radius. Nothing changed.");
            chunks.Add((cx, cy, cz), chunk);
        }
        int added = 0;
        var changed = new HashSet<IWorldChunk>();
        for (int x = center.X - radius; x <= center.X + radius; x++)
        for (int z = center.Z - radius; z <= center.Z + radius; z++)
        for (int y = minY; y <= maxY; y++)
        {
            int dx = x - center.X, dy = y - center.Y, dz = z - center.Z;
            if (dx * dx + dy * dy + dz * dz > radius * radius) continue;
            IWorldChunk chunk = chunks[(ProceduralDepositMath.FloorDiv(x, size), y / size, ProceduralDepositMath.FloorDiv(z, size))];
            int index = ((y % size) * size + ProceduralDepositMath.FloorMod(z, size)) * size + ProceduralDepositMath.FloorMod(x, size);
            int blockId = chunk.Data.GetBlockIdUnsafe(index);
            if (!Byproducts.ByproductSystem.TryRetrofitBolivianGalena(chunk, index, blockId, compiled)) continue;
            added++;
            changed.Add(chunk);
        }
        foreach (IWorldChunk chunk in changed) Byproducts.ByproductSystem.FlushChunk(chunk);
        return TextCommandResult.Success($"Added Bolivian silver provenance to {added} galena block(s) within {radius} blocks. Existing provenance and all blocks were preserved.");
    }

    private static TextCommandResult DeleteRock(ICoreServerAPI api, TextCommandCallingArgs args)
    {
        string requested = ((string)args[0]).Trim().ToLowerInvariant();
        if (requested.Length == 0) return TextCommandResult.Error("Specify a rock variant, e.g. /ag delrock sandstone");

        var targets = new HashSet<int>();
        foreach (Block? block in api.World.Blocks)
        {
            if (block?.Code == null) continue;
            if (!CompiledProceduralDeposit.TryGetRockVariant(block, out string variant)) continue;
            if (variant.Equals(requested, System.StringComparison.OrdinalIgnoreCase)) targets.Add(block.BlockId);
        }

        if (targets.Count == 0) return TextCommandResult.Error($"No rock variant named '{requested}' is registered");

        int chunkSize = GlobalConstants.ChunkSize;
        BlockPos center = (args.Caller.Pos ?? api.World.DefaultSpawnPosition.XYZ).AsBlockPos;
        int minX = (center.X / chunkSize - 1) * chunkSize;
        int minZ = (center.Z / chunkSize - 1) * chunkSize;
        int maxX = minX + 3 * chunkSize - 1;
        int maxZ = minZ + 3 * chunkSize - 1;
        int maxY = api.World.BlockAccessor.MapSizeY - 1;

        IBulkBlockAccessor accessor = api.World.BulkBlockAccessor;
        var position = new BlockPos(minX, 1, minZ, center.dimension);
        int removed = 0;

        for (int x = minX; x <= maxX; x++)
        {
            position.X = x;
            for (int z = minZ; z <= maxZ; z++)
            {
                position.Z = z;
                for (int y = 1; y <= maxY; y++)
                {
                    position.Y = y;
                    if (!targets.Contains(accessor.GetBlockId(position))) continue;

                    accessor.SetBlock(0, position);
                    removed++;
                }
            }
        }

        accessor.Commit();
        return TextCommandResult.Success(
            $"Removed {removed} {requested} block(s) from chunks X {minX / chunkSize}-{maxX / chunkSize}, Z {minZ / chunkSize}-{maxZ / chunkSize}");
    }
}
