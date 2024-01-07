/*
  This file is part of ppather.

    PPather is free software: you can redistribute it and/or modify
    it under the terms of the GNU Lesser General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    PPather is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Lesser General Public License for more details.

    You should have received a copy of the GNU Lesser General Public License
    along with ppather.  If not, see <http://www.gnu.org/licenses/>.

    Copyright Pontus Borg 2008

 */

using System;
using System.Numerics;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using SharedLib.Data;
using System.Collections;
using PPather.Extensions;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using StormDll;
using System.Buffers;
using System.Linq;

namespace Wmo;

internal static class ChunkReader
{
    public const float TILESIZE = 533.33333f;
    public const float ZEROPOINT = 32.0f * TILESIZE;
    public const float CHUNKSIZE = TILESIZE / 16.0f;
    public const float UNITSIZE = CHUNKSIZE / 8.0f;

    public const uint MWMO = 0b_01001101_01010111_01001101_01001111;
    public const uint MODF = 0b_01001101_01001111_01000100_01000110;
    public const uint MAIN = 0b_01001101_01000001_01001001_01001110;
    public const uint MPHD = 0b_01001101_01010000_01001000_01000100;
    public const uint MVER = 0b_01001101_01010110_01000101_01010010;
    public const uint MOGI = 0b_01001101_01001111_01000111_01001001;
    public const uint MOHD = 0b_01001101_01001111_01001000_01000100;
    public const uint MODN = 0b_01001101_01001111_01000100_01001110;
    public const uint MODS = 0b_01001101_01001111_01000100_01010011;
    public const uint MODD = 0b_01001101_01001111_01000100_01000100;
    public const uint MOPY = 0b_01001101_01001111_01010000_01011001;
    public const uint MOVI = 0b_01001101_01001111_01010110_01001001;
    public const uint MOVT = 0b_01001101_01001111_01010110_01010100;
    public const uint MCIN = 0b_01001101_01000011_01001001_01001110;
    public const uint MMDX = 0b_01001101_01001101_01000100_01011000;
    public const uint MDDF = 0b_01001101_01000100_01000100_01000110;
    public const uint MCNR = 0b_01001101_01000011_01001110_01010010;
    public const uint MCVT = 0b_01001101_01000011_01010110_01010100;
    public const uint MCLQ = 0b_01001101_01000011_01001100_01010001;
    public const uint MH2O = 0b_01001101_01001000_00110010_01001111;

    [SkipLocalsInit]
    public static string ExtractString(ReadOnlySpan<byte> buf, int off)
    {
        const byte nullTerminator = 0;
        int length = buf[off..].IndexOf(nullTerminator);
        if (length == -1 || length > buf.Length)
        {
            length = buf.Length;
        }

        return Encoding.ASCII.GetString(buf.Slice(off, length));
    }

    // NOTE:
    // The caller is responsible to Return the array to the ArrayPool
    public static string[] ExtractFileNames(BinaryReader file, uint size)
    {
        var bytePooler = ArrayPool<byte>.Shared;
        byte[] byteBuffer = bytePooler.Rent((int)size);

        Span<byte> span = byteBuffer.AsSpan(0, (int)size);
        file.Read(span);

        const byte nullTerminator = 0;
        int count = span.Count(nullTerminator);

        var stringPooler = ArrayPool<string>.Shared;
        string[] names = stringPooler.Rent(count);

        int i = 0;
        int offset = 0;
        while (offset < size)
        {
            string s = ExtractString(span, offset);
            offset += s.Length + 1;

            names[i++] = s;
        }

        bytePooler.Return(byteBuffer);

        return names;
    }
}

public sealed class WMOManager : Manager<WMO>
{
    private readonly ArchiveSet archive;
    private readonly ModelManager modelmanager;

    public WMOManager(ArchiveSet archive, ModelManager modelmanager)
    {
        this.archive = archive;
        this.modelmanager = modelmanager;
    }

    public override bool Load(string path, out WMO t)
    {
        t = new()
        {
            fileName = path
        };

        WmoRootFile.Load(archive, path, t, modelmanager);

        ReadOnlySpan<char> part = path[..^4].AsSpan();

        for (int i = 0; i < t.groups.Length; i++)
        {
            string name = string.Format("{0}_{1,3:000}.wmo", part.ToString(), i);
            WmoGroupFile.Load(archive, name, t.groups[i]);
        }
        return true;
    }
}

public readonly struct WMOInstance
{
    public readonly WMO wmo;
    public readonly int id;
    public readonly Vector3 pos, pos2, pos3;
    public readonly Vector3 dir;
    public readonly int d2; //d3
    public readonly int doodadset;

    public WMOInstance(BinaryReader file, WMO wmo)
    {
        // read X bytes from file
        this.wmo = wmo;

        id = file.ReadInt32();

        pos = file.ReadVector3();
        dir = file.ReadVector3();
        pos2 = file.ReadVector3();
        pos3 = file.ReadVector3();

        d2 = file.ReadInt32();
        doodadset = file.ReadInt16();
        //_ = file.ReadInt16();
        file.BaseStream.Seek(sizeof(Int16), SeekOrigin.Current);
    }
}

public struct DoodadSet
{
    public uint firstInstance;
    public uint nInstances;
}

public sealed class WMO
{
    public string fileName;
    public WMOGroup[] groups;

    //int nTextures, nGroups, nP, nLight nX;
    public Vector3 v1, v2; // bounding box

    public byte[] MODNraw;
    public uint nModels;
    public uint nDoodads;
    public uint nDoodadSets;

    public DoodadSet[] doodads;
    public ModelInstance[] doodadInstances;
}

public abstract class Manager<T>
{
    private readonly Dictionary<string, T> items;

    public Manager()
    {
        items = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
    }

    public abstract bool Load(string path, out T t);

    public void Clear()
    {
        items.Clear();
    }

    public T AddAndLoadIfNeeded(string path)
    {
        if (!items.TryGetValue(path, out T t) && Load(path, out t))
        {
            items[path] = t;
        }
        return t;
    }
}

public sealed class ModelManager : Manager<Model>
{
    private readonly ArchiveSet archive;

    public ModelManager(ArchiveSet archive)
    {
        this.archive = archive;
    }

    public override bool Load(string path, out Model t)
    {
        // change .mdx to .m2
        if (Path.GetExtension(path).Equals(".mdx", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.ChangeExtension(path, ".m2");
        }

        t = ModelFile.Read(archive, path);
        return true;
    }
}

public readonly struct ModelInstance
{
    public readonly Model model;
    public readonly Vector3 pos;
    public readonly Vector3 dir;
    public readonly float w;
    public readonly float scale;

    public ModelInstance(BinaryReader file, Model model)
    {
        this.model = model;
        //_ = file.ReadUInt32(); // uint d1
        file.BaseStream.Seek(sizeof(UInt32), SeekOrigin.Current);

        pos = file.ReadVector3();
        dir = file.ReadVector3();

        w = 0;
        scale = file.ReadUInt32() / 1024.0f;
    }

    public ModelInstance(Model m, Vector3 pos, Vector3 dir, float sc, float w)
    {
        this.model = m;
        this.pos = pos;
        this.dir = dir;
        this.scale = sc;
        this.w = w;
    }
}

public readonly struct Model
{
    public readonly string fileName;
    public readonly float[] vertices;           // 3 per vertex
    public readonly float[] boundingVertices;   // 3 per vertex
    public readonly ushort[] boundingTriangles;

    public Model(string fileName, float[] vertices, ushort[] boundingTriangles, float[] boundingVertices)
    {
        this.fileName = fileName;
        this.vertices = vertices;
        this.boundingTriangles = boundingTriangles;
        this.boundingVertices = boundingVertices;
    }
}

public static class ModelFile
{
    public static Model Read(ArchiveSet archive, string fileName)
    {
        using MpqFileStream mpq = archive.GetStream(fileName);

        var pooler = ArrayPool<byte>.Shared;
        byte[] buffer = pooler.Rent((int)mpq.Length);
        mpq.ReadAllBytesTo(buffer);

        using MemoryStream stream = new(buffer, 0, (int)mpq.Length, false);
        using BinaryReader file = new(stream);

        // UPDATED FOR WOTLK 17.10.2008 by toblakai
        // SOURCE: http://www.madx.dk/wowdev/wiki/index.php?title=M2/WotLK

        //_ = file.ReadChars(4);
        //_ = file.ReadUInt32(); // (including \0);
        //                       // check that we have the new known WOTLK Magic 0x80100000
        //                       //PPather.Debug("M2 HEADER VERSION: 0x{0:x8}",
        //                       //    (uint) (version >> 24) | ((version << 8) & 0x00FF0000) | ((version >> 8) & 0x0000FF00) | (version << 24));
        //_ = file.ReadUInt32(); // (including \0);
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32(); // ? always 0, 1 or 3 (mostly 0);
        //_ = file.ReadUInt32(); //  - number of global sequences;
        //_ = file.ReadUInt32(); //  - offset to global sequences;
        //_ = file.ReadUInt32(); //  - number of animation sequences;
        //_ = file.ReadUInt32(); //  - offset to animation sequences;
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32(); // Mapping of global IDs to the entries in the Animation sequences block.
        //                       // NOT IN WOTLK uint nD=file.ReadUInt32(); //  - always 201 or 203 depending on WoW client version;
        //                       // NOT IN WOTLK uint ofsD=file.ReadUInt32();
        //_ = file.ReadUInt32(); //  - number of bones;
        //_ = file.ReadUInt32(); //  - offset to bones;
        //_ = file.ReadUInt32(); //  - bone lookup table;
        //_ = file.ReadUInt32();
        file.BaseStream.Seek((sizeof(byte) * 4) + (sizeof(UInt32) * 14), SeekOrigin.Current);

        uint nVertices = file.ReadUInt32(); //  - number of vertices;
        uint ofsVertices = file.ReadUInt32(); //  - offset to vertices;

        //_ = file.ReadUInt32(); //  - number of views (LOD versions?) 4 for every model;
        //                       // NOT IN WOTLK (now in .skins) uint ofsViews=file.ReadUInt32(); //  - offset to views;
        //_ = file.ReadUInt32(); //  - number of color definitions;
        //_ = file.ReadUInt32(); //  - offset to color definitions;
        //_ = file.ReadUInt32(); //  - number of textures;
        //_ = file.ReadUInt32(); //  - offset to texture definitions;
        //_ = file.ReadUInt32(); //  - number of transparency definitions;
        //_ = file.ReadUInt32(); //  - offset to transparency definitions;
        //                       // NOT IN WOTLK uint nTexAnims = file.ReadUInt32(); //  - number of texture animations;
        //                       // NOT IN WOTLK uint ofsTexAnims = file.ReadUInt32(); //  - offset to texture animations;
        //_ = file.ReadUInt32(); //  - always 0;
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32(); //  - number of blending mode definitions;
        //_ = file.ReadUInt32(); //  - offset to blending mode definitions;
        //_ = file.ReadUInt32(); //  - bone lookup table;
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32(); //  - number of texture lookup table entries;
        //_ = file.ReadUInt32(); //  - offset to texture lookup table;
        //_ = file.ReadUInt32(); //  - texture unit definitions?;
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32(); //  - number of transparency lookup table entries;
        //_ = file.ReadUInt32(); //  - offset to transparency lookup table;
        //_ = file.ReadUInt32(); //  - number of texture animation lookup table entries;
        //_ = file.ReadUInt32(); //  - offset to texture animation lookup table;

        //float[] theFloats = new float[14]; // Noone knows. Meeh, they are here.
        //for (int i = 0; i < 14; i++)
        //    file.ReadSingle();
        file.BaseStream.Seek((sizeof(UInt32) * 23) + (sizeof(Single) * 14), SeekOrigin.Current);

        uint nBoundingTriangles = file.ReadUInt32();
        uint ofsBoundingTriangles = file.ReadUInt32();
        uint nBoundingVertices = file.ReadUInt32();
        uint ofsBoundingVertices = file.ReadUInt32();

        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32(); //  - number of lights;
        //_ = file.ReadUInt32(); //  - offset to lights;
        //_ = file.ReadUInt32(); //  - number of cameras;
        //_ = file.ReadUInt32(); //  - offset to cameras;
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32();
        //_ = file.ReadUInt32(); //  - number of ribbon emitters;
        //_ = file.ReadUInt32(); //  - offset to ribbon emitters;
        //_ = file.ReadUInt32(); //  - number of particle emitters;
        //_ = file.ReadUInt32(); //  - offset to particle emitters;
        file.BaseStream.Seek(sizeof(UInt32) * 18, SeekOrigin.Current);

        pooler.Return(buffer);

        return new(
            fileName,
            ReadVertices(file, nVertices, ofsVertices),
            ReadBoundingTriangles(file, nBoundingTriangles, ofsBoundingTriangles),
            ReadBoundingVertices(file, nBoundingVertices, ofsBoundingVertices));
    }

    private static float[] ReadBoundingVertices(BinaryReader file, uint nVertices, uint ofsVertices)
    {
        if (nVertices == 0)
            return Array.Empty<float>();

        file.BaseStream.Seek(ofsVertices, SeekOrigin.Begin);
        float[] vertices = new float[nVertices * 3];

        file.Read(MemoryMarshal.Cast<float, byte>(vertices.AsSpan()));

        return vertices;
    }

    private static ushort[] ReadBoundingTriangles(BinaryReader file, uint nTriangles, uint ofsTriangles)
    {
        if (nTriangles == 0)
            return Array.Empty<ushort>();

        file.BaseStream.Seek(ofsTriangles, SeekOrigin.Begin);

        ushort[] triangles = new ushort[nTriangles];
        file.Read(MemoryMarshal.Cast<ushort, byte>(triangles.AsSpan()));

        return triangles;
    }

    private static float[] ReadVertices(BinaryReader file, uint nVertices, uint ofcVertices)
    {
        float[] vertices = new float[nVertices * 3];

        file.BaseStream.Seek(ofcVertices, SeekOrigin.Begin);
        for (int i = 0; i < nVertices; i++)
        {
            Span<float> span = vertices.AsSpan(i * 3, 3);
            file.Read(MemoryMarshal.Cast<float, byte>(span));

            //_ = file.ReadUInt32();  // bone weights
            //_ = file.ReadUInt32();  // bone indices

            //_ = file.ReadSingle(); // normal *3
            //_ = file.ReadSingle();
            //_ = file.ReadSingle();

            //_ = file.ReadSingle(); // texture coordinates
            //_ = file.ReadSingle();

            //_ = file.ReadSingle(); // some crap
            //_ = file.ReadSingle();
            file.BaseStream.Seek((sizeof(UInt32) * 2) + (sizeof(Single) * 7), SeekOrigin.Current);
        }
        return vertices;
    }
}

public sealed class WMOGroup
{
    public uint nameStart, nameStart2;
    public uint flags;
    public Vector3 v1;
    public Vector3 v2;
    public UInt16 batchesA;
    public UInt16 batchesB;
    public UInt16 batchesC;
    public UInt16 batchesD;
    public UInt16 portalStart;
    public UInt16 portalCount;
    public uint id;

    public uint nVertices;
    public float[] vertices; // 3 per vertex

    public uint nTriangles;
    public UInt16[] triangles; // 3 per triangle
    public UInt16[] materials;  // 1 per triangle

    public const UInt16 MAT_FLAG_NOCAMCOLLIDE = 0x001;
    public const UInt16 MAT_FLAG_DETAIL = 0x002;
    public const UInt16 MAT_FLAG_COLLISION = 0x004;
    public const UInt16 MAT_FLAG_HINT = 0x008;
    public const UInt16 MAT_FLAG_RENDER = 0x010;
    public const UInt16 MAT_FLAG_COLLIDE_HIT = 0x020;
}

internal sealed class WDT
{
    public const int SIZE = 64;

    public readonly BitArray maps = new(SIZE * SIZE);
    public readonly MapTile[] maptiles = new MapTile[SIZE * SIZE];
    public readonly BitArray loaded = new(SIZE * SIZE);
    public WMOInstance[] gwmois = Array.Empty<WMOInstance>();
}

internal sealed class WDTFile
{
    private readonly ILogger logger;
    private readonly WMOManager wmomanager;
    private readonly ModelManager modelmanager;
    private readonly WDT wdt;
    private readonly ArchiveSet archive;

    private readonly string pathName;

    public bool loaded;

    public WDTFile(ArchiveSet archive, float mapId, WDT wdt, WMOManager wmomanager, ModelManager modelmanager, ILogger logger)
    {
        this.logger = logger;
        this.pathName = ContinentDB.IdToName[mapId];

        this.wdt = wdt;
        this.wmomanager = wmomanager;
        this.modelmanager = modelmanager;
        this.archive = archive;

        string wdtfile = Path.Join("World", "Maps", pathName, pathName + ".wdt");
        using MpqFileStream mpq = archive.GetStream(wdtfile);

        var pooler = ArrayPool<byte>.Shared;
        byte[] buffer = pooler.Rent((int)mpq.Length);
        mpq.ReadAllBytesTo(buffer);

        using MemoryStream stream = new(buffer, 0, (int)mpq.Length, false);
        using BinaryReader file = new(stream);

        string[] gwmos = Array.Empty<string>();

        do
        {
            uint type = file.ReadUInt32();
            uint size = file.ReadUInt32();
            long curpos = file.BaseStream.Position;

            switch (type)
            {
                case ChunkReader.MVER:
                    break;
                case ChunkReader.MPHD:
                    break;
                case ChunkReader.MODF:
                    HandleMODF(file, wdt, gwmos, wmomanager, size);
                    break;
                case ChunkReader.MWMO when size != 0:
                    gwmos = ChunkReader.ExtractFileNames(file, size);
                    break;
                case ChunkReader.MAIN:
                    HandleMAIN(file, size);
                    break;
                default:
                    //logger.LogWarning($"WDT Unknown {type} - {file.BaseStream.Length} - {curpos} - {size}");
                    break;
            }
            file.BaseStream.Seek(Math.Min(curpos + size, file.BaseStream.Length), SeekOrigin.Begin);
        } while (!file.EOF());

        if (gwmos.Length != 0)
            ArrayPool<string>.Shared.Return(gwmos);

        pooler.Return(buffer);

        loaded = true;
    }

    public void LoadMapTile(int x, int y, int index)
    {
        if (!wdt.maps[index])
            return;

        string filename = Path.Join("World", "Maps", pathName, $"{pathName}_{x}_{y}.adt");
        if (logger.IsEnabled(LogLevel.Trace))
            logger.LogTrace($"Reading adt: {filename}");

        wdt.maptiles[index] = MapTileFile.Read(archive, filename, wmomanager, modelmanager);
        wdt.loaded[index] = true;
    }

    private static void HandleMODF(BinaryReader file, WDT wdt, Span<string> gwmos, WMOManager wmomanager, uint size)
    {
        // global wmo instance data
        int gnWMO = (int)size / 64;
        wdt.gwmois = new WMOInstance[gnWMO];

        for (uint i = 0; i < gnWMO; i++)
        {
            int id = file.ReadInt32();
            string path = gwmos[id];

            WMO wmo = wmomanager.AddAndLoadIfNeeded(path);
            wdt.gwmois[i] = new(file, wmo);
        }
    }

    private void HandleMAIN(BinaryReader file, uint size)
    {
        // global map objects
        for (int index = 0; index < WDT.SIZE * WDT.SIZE; index++)
        {
            wdt.maps[index] = file.ReadInt32() != 0;
            //file.ReadInt32(); // kasta
            file.BaseStream.Seek(sizeof(Int32), SeekOrigin.Current);
        }
    }
}

internal readonly struct MapChunk
{
    public readonly float xbase, ybase, zbase;
    public readonly uint areaID;
    public readonly bool haswater;
    public readonly bool hasholes;
    public readonly uint holes;
    //public float waterlevel;

    //  0   1   2   3   4   5   6   7   8
    //    9  10  11  12  13  14  15  16
    // 17  18  19  20  21  22  23  24  25
    // ...
    public readonly float[] vertices;

    public readonly float water_height1;
    public readonly float water_height2;
    public readonly float[] water_height;
    public readonly byte[] water_flags;


    private static readonly int[] holetab_h = new int[] { 0x1111, 0x2222, 0x4444, 0x8888 };
    private static readonly int[] holetab_v = new int[] { 0x000F, 0x00F0, 0x0F00, 0xF000 };

    // 0 ..3, 0 ..3
    public bool isHole(int i, int j)
    {
        if (!hasholes)
            return false;
        i /= 2;
        j /= 2;

        return i <= 3 && j <= 3 && (holes & holetab_h[i] & holetab_v[j]) != 0;
    }


    public MapChunk(float xbase, float ybase, float zbase,
        uint areaID, bool haswater, uint holes, float[] vertices,
        float water_height1, float water_height2, float[] water_height, byte[] water_flags)
    {
        this.xbase = xbase;
        this.ybase = ybase;
        this.zbase = zbase;
        this.areaID = areaID;
        this.haswater = haswater;
        this.holes = holes;
        this.hasholes = holes != 0;
        this.vertices = vertices;

        this.water_height1 = water_height1;
        this.water_height2 = water_height2;
        this.water_height = water_height;
        this.water_flags = water_flags;
    }
}

internal readonly struct MapTile
{
    public const int SIZE = 16;

    public readonly ModelInstance[] modelis;
    public readonly WMOInstance[] wmois;

    public readonly MapChunk[] chunks;
    public readonly BitArray hasChunk;

    public MapTile(ModelInstance[] modelis, WMOInstance[] wmois, MapChunk[] chunks, BitArray hasChunk)
    {
        this.modelis = modelis;
        this.wmois = wmois;
        this.chunks = chunks;
        this.hasChunk = hasChunk;
    }
}

internal static class MapTileFile // adt file
{
    private static readonly MH2OData1 eMH2OData1 = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    public static ref readonly MH2OData1 EmptyMH2OData1 => ref eMH2OData1;

    private static readonly LiquidData eLiquidData = new(0, 0, 0, EmptyMH2OData1, Array.Empty<float>(), Array.Empty<byte>());
    public static ref readonly LiquidData EmptyLiquidData => ref eLiquidData;

    public static MapTile Read(ArchiveSet archive, string name, WMOManager wmomanager, ModelManager modelmanager)
    {
        LiquidData[] LiquidDataChunk = Array.Empty<LiquidData>();
        Span<int> mcnk_offsets = stackalloc int[MapTile.SIZE * MapTile.SIZE];
        Span<int> mcnk_sizes = stackalloc int[MapTile.SIZE * MapTile.SIZE];

        string[] models = Array.Empty<string>();
        string[] wmos = Array.Empty<string>();

        WMOInstance[] wmois = Array.Empty<WMOInstance>();
        ModelInstance[] modelis = Array.Empty<ModelInstance>();

        MapChunk[] chunks = new MapChunk[MapTile.SIZE * MapTile.SIZE];
        BitArray hasChunk = new(chunks.Length);

        using MpqFileStream mpq = archive.GetStream(name);

        var pooler = ArrayPool<byte>.Shared;
        byte[] buffer = pooler.Rent((int)mpq.Length);
        mpq.ReadAllBytesTo(buffer);

        using MemoryStream stream = new(buffer, 0, (int)mpq.Length, false);
        using BinaryReader file = new(stream);

        do
        {
            uint type = file.ReadUInt32();
            uint size = file.ReadUInt32();
            long curpos = file.BaseStream.Position;

            switch (type)
            {
                case ChunkReader.MCIN:
                    HandleMCIN(file, mcnk_offsets, mcnk_sizes);
                    break;
                case ChunkReader.MMDX when size != 0:
                    models = ChunkReader.ExtractFileNames(file, size);
                    break;
                case ChunkReader.MWMO when size != 0:
                    wmos = ChunkReader.ExtractFileNames(file, size);
                    break;
                case ChunkReader.MDDF:
                    HandleMDDF(file, modelmanager, models, size, out modelis);
                    break;
                case ChunkReader.MODF:
                    HandleMODF(file, wmos, wmomanager, size, out wmois);
                    break;
                case ChunkReader.MH2O:
                    HandleMH2O(file, out LiquidDataChunk);
                    break;
            }

            file.BaseStream.Seek(Math.Min(curpos + size, file.BaseStream.Length), SeekOrigin.Begin);
        } while (!file.EOF());

        if (wmos.Length != 0)
            ArrayPool<string>.Shared.Return(wmos);

        if (models.Length != 0)
            ArrayPool<string>.Shared.Return(models);

        pooler.Return(buffer);

        for (int index = 0; index < MapTile.SIZE * MapTile.SIZE; index++)
        {
            int off = mcnk_offsets[index];
            file.BaseStream.Seek(off, SeekOrigin.Begin);

            chunks[index] = ReadMapChunk(file, LiquidDataChunk.Length > 0 ? LiquidDataChunk[index] : EmptyLiquidData);
            hasChunk[index] = true;
        }

        return new(modelis, wmois, chunks, hasChunk);
    }

    public readonly struct LiquidData
    {
        public const int SIZE = 256;
        public const int HEIGHT_SIZE = 9;
        public const int FLAG_SIZE = 8;

        public readonly uint offsetData1;
        public readonly int used;
        public readonly uint offsetData2;

        public readonly MH2OData1 data1;

        public readonly float[] water_height;
        public readonly byte[] water_flags;

        public LiquidData(uint offsetData1, int used, uint offsetData2,
            MH2OData1 data1, float[] water_height, byte[] water_flags)
        {
            this.offsetData1 = offsetData1;
            this.used = used;
            this.offsetData2 = offsetData2;
            this.data1 = data1;

            this.water_height = water_height;
            this.water_flags = water_flags;
        }
    }

    public readonly struct MH2OData1
    {
        public readonly UInt16 flags;   //0x1 might mean there is a height map @ data2b ??
        public readonly UInt16 type;    //0 = normal/lake, 1 = lava, 2 = ocean
        public readonly float heightLevel1;
        public readonly float heightLevel2;
        public readonly byte xOffset;
        public readonly byte yOffset;
        public readonly byte Width;
        public readonly byte Height;
        public readonly uint offsetData2a;
        public readonly uint offsetData2b;

        public MH2OData1(UInt16 flags, UInt16 type, float heightLevel1,
            float heightLevel2, byte xOffet, byte yOffset, byte width, byte height,
            uint offsetData2a, uint offsetData2b)
        {
            this.flags = flags;
            this.type = type;
            this.heightLevel1 = heightLevel1;
            this.heightLevel2 = heightLevel2;
            this.xOffset = xOffet;
            this.yOffset = yOffset;
            this.Width = width;
            this.Height = height;
            this.offsetData2a = offsetData2a;
            this.offsetData2b = offsetData2b;
        }
    }

    private static void HandleMH2O(BinaryReader file, out LiquidData[] liquidData)
    {
        liquidData = new LiquidData[LiquidData.SIZE];

        long chunkStart = file.BaseStream.Position;
        for (int i = 0; i < LiquidData.SIZE; i++)
        {
            uint offsetData1 = file.ReadUInt32();
            int used = file.ReadInt32();
            uint offsetData2 = file.ReadUInt32();
            MH2OData1 data1 = EmptyMH2OData1;

            if (offsetData1 != 0)
            {
                long lastPos = file.BaseStream.Position;

                file.BaseStream.Seek(chunkStart + offsetData1, SeekOrigin.Begin);
                data1 = new MH2OData1
                (
                    file.ReadUInt16(),
                    file.ReadUInt16(),
                    file.ReadSingle(),
                    file.ReadSingle(),
                    file.ReadByte(),
                    file.ReadByte(),
                    file.ReadByte(),
                    file.ReadByte(),
                    file.ReadUInt32(),
                    file.ReadUInt32()
                );

                file.BaseStream.Seek(lastPos, SeekOrigin.Begin);
            }

            float[] water_height = new float[LiquidData.HEIGHT_SIZE * LiquidData.HEIGHT_SIZE];
            byte[] water_flags = new byte[LiquidData.FLAG_SIZE * LiquidData.FLAG_SIZE];

            if ((used & 1) == 1 && offsetData1 != 0 && data1.offsetData2b != 0 && (data1.flags & 1) == 1)
            {
                long lastPos = file.BaseStream.Position;
                file.BaseStream.Seek(chunkStart + data1.offsetData2b, SeekOrigin.Begin);

                for (int x = data1.xOffset; x <= data1.xOffset + data1.Width; x++)
                {
                    for (int y = data1.yOffset; y <= data1.yOffset + data1.Height; y++)
                    {
                        int index = y * LiquidData.HEIGHT_SIZE + x;
                        water_height[index] = file.ReadSingle();
                    }
                }

                for (int x = data1.xOffset; x < data1.xOffset + data1.Width; x++)
                {
                    for (int y = data1.yOffset; y < data1.yOffset + data1.Height; y++)
                    {
                        int index = y * LiquidData.FLAG_SIZE + x;
                        water_flags[index] = file.ReadByte();
                    }
                }

                file.BaseStream.Seek(lastPos, SeekOrigin.Begin);
            }

            liquidData[i] = new
            (
                offsetData1,
                used,
                offsetData2,
                data1,
                water_height,
                water_flags
            );
        }
    }

    private static void HandleMCIN(BinaryReader file, Span<int> mcnk_offsets, Span<int> mcnk_sizes)
    {
        for (int i = 0; i < mcnk_offsets.Length; i++)
        {
            mcnk_offsets[i] = file.ReadInt32();
            mcnk_sizes[i] = file.ReadInt32();
            //file.ReadInt32(); // crap
            //file.ReadInt32();// crap
            file.BaseStream.Seek(sizeof(Int32) * 2, SeekOrigin.Current);
        }
    }

    private static void HandleMDDF(BinaryReader file, ModelManager modelmanager, Span<string> models, uint size, out ModelInstance[] modelis)
    {
        int nMDX = (int)size / 36;

        modelis = new ModelInstance[nMDX];
        for (int i = 0; i < nMDX; i++)
        {
            int id = file.ReadInt32();

            string path = models[id];
            Model model = modelmanager.AddAndLoadIfNeeded(path);
            modelis[i] = new(file, model);
        }
    }

    private static void HandleMODF(BinaryReader file, Span<string> wmos, WMOManager wmomanager, uint size, out WMOInstance[] wmois)
    {
        int nWMO = (int)size / 64;
        wmois = new WMOInstance[nWMO];

        for (int i = 0; i < nWMO; i++)
        {
            int id = file.ReadInt32();
            WMO wmo = wmomanager.AddAndLoadIfNeeded(wmos[id]);

            wmois[i] = new(file, wmo);
        }
    }

    /* MapChunk */

    private static MapChunk ReadMapChunk(BinaryReader file, in LiquidData liquidData)
    {
        // Read away Magic and size
        //_ = file.ReadUInt32(); // uint crap_head
        //_ = file.ReadUInt32(); // uint crap_size

        // Each map chunk has 9x9 vertices,
        // and in between them 8x8 additional vertices, several texture layers, normal vectors, a shadow map, etc.

        //_ = file.ReadUInt32(); // uint flags
        //_ = file.ReadUInt32(); // uint ix
        //_ = file.ReadUInt32(); // uint iy
        //_ = file.ReadUInt32(); // uint nLayers
        //_ = file.ReadUInt32(); // uint nDoodadRefs
        //_ = file.ReadUInt32(); // uint ofsHeight
        //_ = file.ReadUInt32(); // uint ofsNormal
        //_ = file.ReadUInt32(); // uint ofsLayer
        //_ = file.ReadUInt32(); // uint ofsRefs
        //_ = file.ReadUInt32(); // uint ofsAlpha
        //_ = file.ReadUInt32(); // uint sizeAlpha
        //_ = file.ReadUInt32(); // uint ofsShadow
        //_ = file.ReadUInt32(); // uint sizeShadow

        file.BaseStream.Seek(sizeof(UInt32) * 15, SeekOrigin.Current);

        uint areaID = file.ReadUInt32();
        //_ = file.ReadUInt32(); // uint nMapObjRefs
        file.BaseStream.Seek(sizeof(UInt32), SeekOrigin.Current);
        uint holes = file.ReadUInt32();

        //_ = file.ReadUInt16(); // ushort s1
        //_ = file.ReadUInt16(); // ushort s2

        //_ = file.ReadUInt32(); // uint d1
        //_ = file.ReadUInt32(); // uint d2
        //_ = file.ReadUInt32(); // uint d3
        //_ = file.ReadUInt32(); // uint predTex
        //_ = file.ReadUInt32(); // uint nEffectDoodad
        //_ = file.ReadUInt32(); // uint ofsSndEmitters 
        //_ = file.ReadUInt32(); // uint nSndEmitters
        //_ = file.ReadUInt32(); // uint ofsLiquid

        file.BaseStream.Seek((sizeof(UInt16) * 2) + (sizeof(UInt32) * 8), SeekOrigin.Current);

        uint sizeLiquid = file.ReadUInt32();
        float zpos = file.ReadSingle();
        float xpos = file.ReadSingle();
        float ypos = file.ReadSingle();

        //_ = file.ReadUInt32(); // uint textureId
        //_ = file.ReadUInt32(); // uint props 
        //_ = file.ReadUInt32(); // uint effectId

        file.BaseStream.Seek(sizeof(UInt32) * 3, SeekOrigin.Current);

        float xbase = -xpos + ChunkReader.ZEROPOINT;
        float ybase = ypos;
        float zbase = -zpos + ChunkReader.ZEROPOINT;

        float[] vertices = new float[3 * ((9 * 9) + (8 * 8))];

        bool haswater = false;
        float water_height1 = 0;
        float water_height2 = 0;
        float[] water_height = new float[LiquidData.HEIGHT_SIZE * LiquidData.HEIGHT_SIZE];
        byte[] water_flags = new byte[LiquidData.FLAG_SIZE * LiquidData.FLAG_SIZE];

        //logger.WriteLine("  " + zpos + " " + xpos + " " + ypos);
        do
        {
            uint type = file.ReadUInt32();
            uint size = file.ReadUInt32();
            long curpos = file.BaseStream.Position;

            if (type == ChunkReader.MCNR)
            {
                size = 0x1C0; // WTF
            }

            if (type == ChunkReader.MCVT)
            {
                HandleChunkMCVT(file, xbase, ybase, zbase, vertices);
            }
            else if (type == ChunkReader.MCLQ)
            {
                /* Some .adt-files are still using the old MCLQ chunks. Far from all though.
                * And those which use the MH2O chunk does not use these MCLQ chunks */
                size = sizeLiquid;
                if (sizeLiquid != 8)
                {
                    haswater = true;
                    HandleChunkMCLQ(file, out water_height1, out water_height2, water_height, water_flags);
                }
            }

            file.BaseStream.Seek(Math.Min(curpos + size, file.BaseStream.Length), SeekOrigin.Begin);
        } while (!file.EOF());

        //set liquid info from the MH2O chunk since the old MCLQ is no more
        if (liquidData.offsetData1 != 0)
        {
            haswater = (liquidData.used & 1) == 1;

            water_height1 = liquidData.data1.heightLevel1;
            water_height2 = liquidData.data1.heightLevel2;

            //TODO: set height map and flags, very important
            water_height = liquidData.water_height;
            water_flags = liquidData.water_flags;
        }

        return new(xbase, ybase, zbase,
            areaID, haswater, holes,
            vertices, water_height1, water_height2,
            water_height, water_flags);
    }

    private static void HandleChunkMCVT(BinaryReader file, float xbase, float ybase, float zbase, float[] vertices)
    {
        int index = 0;
        for (int j = 0; j < 17; j++)
        {
            for (int i = 0; i < ((j % 2 != 0) ? 8 : 9); i++)
            {
                float y = file.ReadSingle();
                float x = i * ChunkReader.UNITSIZE;
                float z = j * 0.5f * ChunkReader.UNITSIZE;

                if (j % 2 != 0)
                {
                    x += ChunkReader.UNITSIZE * 0.5f;
                }

                vertices[index++] = xbase + x;
                vertices[index++] = ybase + y;
                vertices[index++] = zbase + z;
            }
        }
    }

    private static void HandleChunkMCLQ(BinaryReader file, out float water_height1, out float water_height2, float[] water_height, byte[] water_flags)
    {
        water_height1 = file.ReadSingle();
        water_height2 = file.ReadSingle();

        for (int i = 0; i < LiquidData.HEIGHT_SIZE * LiquidData.HEIGHT_SIZE; i++)
        {
            _ = file.ReadUInt32();
            water_height[i] = file.ReadSingle();
        }

        for (int i = 0; i < LiquidData.FLAG_SIZE * LiquidData.FLAG_SIZE; i++)
        {
            water_flags[i] = file.ReadByte();
        }
    }
}

internal static class WmoRootFile
{
    public static void Load(ArchiveSet archive, string name, WMO wmo, ModelManager modelmanager)
    {
        using MpqFileStream mpq = archive.GetStream(name);

        var pooler = ArrayPool<byte>.Shared;
        byte[] buffer = pooler.Rent((int)mpq.Length);
        mpq.ReadAllBytesTo(buffer);

        using MemoryStream stream = new(buffer, 0, (int)mpq.Length, false);
        using BinaryReader file = new(stream);

        do
        {
            uint type = file.ReadUInt32();
            uint size = file.ReadUInt32();
            long curpos = file.BaseStream.Position;

            switch (type)
            {
                case ChunkReader.MOHD:
                    HandleMOHD(file, wmo, size);
                    break;
                case ChunkReader.MOGI:
                    HandleMOGI(file, wmo, size);
                    break;
                case ChunkReader.MODS:
                    HandleMODS(file, wmo);
                    break;
                case ChunkReader.MODD:
                    HandleMODD(file, wmo, modelmanager, size);
                    break;
                case ChunkReader.MODN:
                    HandleMODN(file, wmo, size);
                    break;
            }

            file.BaseStream.Seek(Math.Min(curpos + size, file.BaseStream.Length), SeekOrigin.Begin);
        } while (!file.EOF());

        pooler.Return(buffer);
    }

    private static void HandleMOHD(BinaryReader file, WMO wmo, uint size)
    {
        //file.ReadUInt32(); // uint nTextures
        file.BaseStream.Seek(sizeof(UInt32), SeekOrigin.Current);

        uint nGroups = file.ReadUInt32();
        //file.ReadUInt32(); // uint nP
        //file.ReadUInt32(); // uint nLights
        file.BaseStream.Seek(sizeof(UInt32) * 2, SeekOrigin.Current);

        wmo.nModels = file.ReadUInt32();
        wmo.nDoodads = file.ReadUInt32();
        wmo.nDoodadSets = file.ReadUInt32();

        //file.ReadUInt32(); //uint col
        //file.ReadUInt32(); //uint nX
        file.BaseStream.Seek(sizeof(UInt32) * 2, SeekOrigin.Current);

        wmo.v1 = file.ReadVector3();
        wmo.v2 = file.ReadVector3();

        wmo.groups = new WMOGroup[nGroups];
    }

    private static void HandleMODS(BinaryReader file, WMO wmo)
    {
        wmo.doodads = new DoodadSet[wmo.nDoodadSets];
        for (int i = 0; i < wmo.nDoodadSets; i++)
        {
            //file.ReadBytes(20); // byte[] name
            file.BaseStream.Seek(20, SeekOrigin.Current);

            wmo.doodads[i].firstInstance = file.ReadUInt32();
            wmo.doodads[i].nInstances = file.ReadUInt32();
            //file.ReadUInt32();
            file.BaseStream.Seek(sizeof(UInt32), SeekOrigin.Current);
        }
    }

    private static void HandleMODD(BinaryReader file, WMO wmo, ModelManager modelmanager, uint size)
    {
        // 40 bytes per doodad instance, nDoodads entries.
        // While WMOs and models (M2s) in a map tile are rotated along the axes,
        //  doodads within a WMO are oriented using quaternions! Hooray for consistency!
        /*
        0x00 	uint32 		Offset to the start of the model's filename in the MODN chunk.
        0x04 	3 * float 	Position (X,Z,-Y)
        0x10 	float 		W component of the orientation quaternion
        0x14 	3 * float 	X, Y, Z components of the orientaton quaternion
        0x20 	float 		Scale factor
        0x24 	4 * uint8 	(B,G,R,A) color. Unknown. It is often (0,0,0,255). (something to do with lighting maybe?)
		*/

        uint sets = size / 0x28;
        wmo.doodadInstances = new ModelInstance[wmo.nDoodads];

        for (int i = 0; i < sets; i++)
        {
            uint nameOffsetInMODN = file.ReadUInt32(); // 0x00

            Vector3 pos = file.ReadVector3_XZY();

            float quatw = file.ReadSingle(); // 0x10
            Vector3 dir = file.ReadVector3();

            float scale = file.ReadSingle(); // 0x20

            //file.ReadUInt32(); // lighning crap 0x24
            file.BaseStream.Seek(sizeof(UInt32), SeekOrigin.Current);

            string name = ChunkReader.ExtractString(wmo.MODNraw, (int)nameOffsetInMODN);
            Model m = modelmanager.AddAndLoadIfNeeded(name);

            ModelInstance mi = new(m, pos, dir, scale, quatw);
            wmo.doodadInstances[i] = mi;
        }
    }

    private static void HandleMODN(BinaryReader file, WMO wmo, uint size)
    {
        // List of filenames for M2 (mdx) models that appear in this WMO.
        wmo.MODNraw = file.ReadBytes((int)size);
    }

    private static void HandleMOGI(BinaryReader file, WMO wmo, uint size)
    {
        for (int i = 0; i < wmo.groups.Length; i++)
        {
            WMOGroup g = new();
            wmo.groups[i] = g;

            g.flags = file.ReadUInt32();

            g.v1 = file.ReadVector3();
            g.v2 = file.ReadVector3();

            //_ = file.ReadUInt32(); // uint nameOfs
            file.BaseStream.Seek(sizeof(UInt32), SeekOrigin.Current);
        }
    }
}

internal static class WmoGroupFile
{
    public static void Load(ArchiveSet archive, string name, WMOGroup g)
    {
        MpqFileStream mpq;

        try
        {
            mpq = archive.GetStream(name);
        }
        catch
        {
            // Possible files which were not found
            // lichking.mpq - world\wmo\northrend\buildings\forsaken\nd_forsaken_apothecary_004.wmo
            return;
        }

        var pooler = ArrayPool<byte>.Shared;
        byte[] buffer = pooler.Rent((int)mpq.Length);
        mpq.ReadAllBytesTo(buffer);

        using MemoryStream stream = new(buffer, 0, (int)mpq.Length, false);
        using BinaryReader file = new(stream);

        file.BaseStream.Seek(0x14, SeekOrigin.Begin);
        HandleMOGP(file, g, 11);

        file.BaseStream.Seek(0x58, SeekOrigin.Begin);// first chunk

        do
        {
            uint type = file.ReadUInt32();
            uint size = file.ReadUInt32();
            long curpos = file.BaseStream.Position;
            switch (type)
            {
                case ChunkReader.MOPY:
                    HandleMOPY(file, g, size);
                    break;
                case ChunkReader.MOVI:
                    HandleMOVI(file, g, size);
                    break;
                case ChunkReader.MOVT:
                    HandleMOVT(file, g, size);
                    break;
            }

            file.BaseStream.Seek(Math.Min(curpos + size, file.BaseStream.Length), SeekOrigin.Begin);
        } while (!file.EOF());

        pooler.Return(buffer);
        mpq.Dispose();
    }

    private static void HandleMOPY(BinaryReader file, WMOGroup g, uint size)
    {
        g.nTriangles = size / 2;
        // materials
        /*
        *  0x01 - inside small houses and paths leading indoors
		*  0x02 - ???
		*  0x04 - set on indoor things and ruins
		*  0x08 - ???
		*  0x10 - ???
		*  0x20 - Always set?
		*  0x40 - sometimes set-
		*  0x80 - ??? never set
		*/

        g.materials = new ushort[g.nTriangles];
        file.Read(MemoryMarshal.Cast<ushort, byte>(g.materials.AsSpan()));
    }

    private static void HandleMOVI(BinaryReader file, WMOGroup g, uint size)
    {
        g.triangles = new ushort[g.nTriangles * 3];
        file.Read(MemoryMarshal.Cast<ushort, byte>(g.triangles.AsSpan()));
    }

    private static void HandleMOVT(BinaryReader file, WMOGroup g, uint size)
    {
        // let's hope it's padded to 12 bytes, not 16...

        g.nVertices = size / 12;
        g.vertices = new float[g.nVertices * 3];

        file.Read(MemoryMarshal.Cast<float, byte>(g.vertices.AsSpan()));
    }

    private static void HandleMOGP(BinaryReader file, WMOGroup g, uint size)
    {
        g.nameStart = file.ReadUInt32();
        g.nameStart2 = file.ReadUInt32();
        g.flags = file.ReadUInt32();

        g.v1 = file.ReadVector3();
        g.v2 = file.ReadVector3();

        g.portalStart = file.ReadUInt16();
        g.portalCount = file.ReadUInt16();
        g.batchesA = file.ReadUInt16();
        g.batchesB = file.ReadUInt16();
        g.batchesC = file.ReadUInt16();
        g.batchesD = file.ReadUInt16();

        //file.ReadUInt32(); // uint fogCrap
        //file.ReadUInt32(); // uint unknown1
        file.BaseStream.Seek(sizeof(UInt32) * 2, SeekOrigin.Current);

        g.id = file.ReadUInt32();

        //file.ReadUInt32(); // uint unknown2
        //file.ReadUInt32(); // uint unknown3
        file.BaseStream.Seek(sizeof(UInt32) * 2, SeekOrigin.Current);
    }
}