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
using static System.MathF;
using Wmo;
using System.IO;
using Microsoft.Extensions.Logging;
using PPather.Triangles.Data;
using static Wmo.MapTileFile;
using PPather;
using System.Runtime.CompilerServices;
using System.Collections.Generic;

namespace WowTriangles;

public sealed class MPQTriangleSupplier
{
    private readonly ILogger logger;
    private readonly StormDll.ArchiveSet archive;
    private readonly ModelManager modelmanager;
    private readonly WMOManager wmomanager;
    private readonly WDT wdt;
    private readonly WDTFile wdtf;

    private readonly float mapId;

    public MPQTriangleSupplier(ILogger logger, DataConfig dataConfig, float mapId)
    {
        this.logger = logger;
        this.mapId = mapId;

        archive = new StormDll.ArchiveSet(logger, GetArchiveNames(dataConfig));
        modelmanager = new ModelManager(archive);
        wmomanager = new WMOManager(archive, modelmanager);

        wdt = new WDT();
        wdtf = new WDTFile(archive, mapId, wdt, wmomanager, modelmanager, logger);

        // TODO: move this to WDTFile
        if (!wdtf.loaded)
        {
            wdt = null; // bad
            throw new Exception("Failed to set continent to: " + mapId);
        }
    }

    public void Clear()
    {
        archive.Close();
        modelmanager.Clear();
        wmomanager.Clear();
    }

    public static string[] GetArchiveNames(DataConfig dataConfig)
    {
        return Directory.GetFiles(dataConfig.MPQ);
    }

    [SkipLocalsInit]
    private void GetChunkData(TriangleCollection triangles, int chunk_x, int chunk_y)
    {
        if (triangles == null || wdtf == null || wdt == null)
            return;
        if (chunk_x < 0 || chunk_y < 0)
            return;
        if (chunk_x > 63 || chunk_y > 63)
            return;

        int index = chunk_y * WDT.SIZE + chunk_x;
        wdtf.LoadMapTile(chunk_x, chunk_y, index);

        MapTile mapTile = wdt.maptiles[index];
        if (!wdt.loaded[index])
            return;

        // Map tiles
        for (int i = 0; i < MapTile.SIZE * MapTile.SIZE; i++)
        {
            if (mapTile.hasChunk[i])
                AddTriangles(triangles, mapTile.chunks[i]);
        }

        // Map Tile - World objects
        for (int i = 0; i < mapTile.wmois.Length; i++)
        {
            WMOInstance wi = mapTile.wmois[i];
            AddTriangles(triangles, wi);
        }

        for (int i = 0; i < mapTile.modelis.Length; i++)
        {
            AddTriangles(triangles, mapTile.modelis[i]);
        }

        wdt.loaded[index] = false;
    }

    [SkipLocalsInit]
    private static void GetChunkCoord(float x, float y, out int chunk_x, out int chunk_y)
    {
        float xOffset = ChunkReader.ZEROPOINT - y;
        float yOffset = ChunkReader.ZEROPOINT - x;

        chunk_x = (int)Round(xOffset / ChunkReader.TILESIZE) - 1;
        chunk_y = (int)Round(yOffset / ChunkReader.TILESIZE) - 1;
    }

    [SkipLocalsInit]
    public void GetTriangles(TriangleCollection tc, float min_x, float min_y, float max_x, float max_y)
    {
        for (int i = 0; i < wdt.gwmois.Length; i++)
        {
            AddTriangles(tc, wdt.gwmois[i]);
        }

        for (float x = min_x; x < max_x; x += ChunkReader.TILESIZE)
        {
            for (float y = min_y; y < max_y; y += ChunkReader.TILESIZE)
            {
                GetChunkCoord(x, y, out int chunk_x, out int chunk_y);
                GetChunkData(tc, chunk_x, chunk_y);
            }
        }
    }

    [SkipLocalsInit]
    private static void AddTriangles(TriangleCollection tc, MapChunk c)
    {
        Span<int> vertices = stackalloc int[9 * 9];
        Span<int> verticesMid = stackalloc int[8 * 8];

        for (int row = 0; row < 9; row++)
        {
            for (int col = 0; col < 9; col++)
            {
                ChunkGetCoordForPoint(c, row, col, out float x, out float y, out float z);
                int index = tc.AddVertex(x, y, z);
                vertices[row * 9 + col] = index;
            }
        }

        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                ChunkGetCoordForMiddlePoint(c, row, col, out float x, out float y, out float z);
                int index = tc.AddVertex(x, y, z);
                verticesMid[row * 8 + col] = index;
            }
        }

        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                if (!c.isHole(col, row))
                {
                    int v0 = vertices[row * 9 + col];
                    int v1 = vertices[(row + 1) * 9 + col];
                    int v2 = vertices[(row + 1) * 9 + col + 1];
                    int v3 = vertices[row * 9 + col + 1];
                    int vMid = verticesMid[row * 8 + col];

                    tc.AddTriangle(v0, v1, vMid, TriangleType.Terrain);
                    tc.AddTriangle(v1, v2, vMid, TriangleType.Terrain);
                    tc.AddTriangle(v2, v3, vMid, TriangleType.Terrain);
                    tc.AddTriangle(v3, v0, vMid, TriangleType.Terrain);
                }
            }
        }

        if (c.haswater)
        {
            // paint the water
            for (int row = 0; row < LiquidData.HEIGHT_SIZE; row++)
            {
                for (int col = 0; col < LiquidData.HEIGHT_SIZE; col++)
                {
                    int ii = row * LiquidData.HEIGHT_SIZE + col;

                    ChunkGetCoordForPoint(c, row, col, out float x, out float y, out float z);
                    float height = c.water_height[ii]; // - 1.5f //why this here
                    int index = tc.AddVertex(x, y, height);

                    vertices[row * LiquidData.HEIGHT_SIZE + col] = index;
                }
            }

            for (int row = 0; row < LiquidData.FLAG_SIZE; row++)
            {
                for (int col = 0; col < LiquidData.FLAG_SIZE; col++)
                {
                    int ii = row * LiquidData.FLAG_SIZE + col;

                    if (c.water_flags[ii] == 0xf)
                        continue;

                    int v0 = vertices[row * LiquidData.HEIGHT_SIZE + col];
                    int v1 = vertices[(row + 1) * LiquidData.HEIGHT_SIZE + col];
                    int v2 = vertices[(row + 1) * LiquidData.HEIGHT_SIZE + col + 1];
                    int v3 = vertices[row * LiquidData.HEIGHT_SIZE + col + 1];

                    tc.AddTriangle(v0, v1, v3, TriangleType.Water);
                    tc.AddTriangle(v1, v2, v3, TriangleType.Water);
                }
            }
        }
    }

    [SkipLocalsInit]
    private static void AddTriangles(TriangleCollection tc, WMOInstance wi)
    {
        float dx = wi.pos.X;
        float dy = wi.pos.Y;
        float dz = wi.pos.Z;

        float dir_x = wi.dir.Z;
        float dir_y = wi.dir.Y - 90;
        float dir_z = -wi.dir.X;

        int maxVertices = 0;
        WMO wmo = wi.wmo;
        for (int gi = 0; gi < wmo.groups.Length; gi++)
        {
            WMOGroup g = wmo.groups[gi];
            maxVertices = Math.Max(maxVertices, (int)g.nVertices);
        }

        Span<int> vertices = stackalloc int[maxVertices];

        for (int gi = 0; gi < wmo.groups.Length; gi++)
        {
            WMOGroup g = wmo.groups[gi];

            float minx = float.MaxValue;
            float miny = float.MaxValue;
            float minz = float.MaxValue;

            float maxx = float.MinValue;
            float maxy = float.MinValue;
            float maxz = float.MinValue;

            for (int i = 0; i < g.nVertices; i++)
            {
                int off = i * 3;

                float x = g.vertices[off];
                float y = g.vertices[off + 2];
                float z = g.vertices[off + 1];

                Rotate(z, y, dir_x, out z, out y);
                Rotate(x, y, dir_z, out x, out y);
                Rotate(x, z, dir_y, out x, out z);

                float xx = x + dx;
                float yy = y + dy;
                float zz = -z + dz;

                float finalx = ChunkReader.ZEROPOINT - zz;
                float finaly = ChunkReader.ZEROPOINT - xx;
                float finalz = yy;

                vertices[i] = tc.AddVertex(finalx, finaly, finalz);

                if (finalx < minx) { minx = finalx; }
                if (finaly < miny) { miny = finalx; }
                if (finalz < minz) { minz = finalx; }

                if (finalx > maxx) { maxx = finalx; }
                if (finaly > maxy) { maxy = finalx; }
                if (finalz > maxz) { maxz = finalx; }
            }

            for (int i = 0; i < g.nTriangles; i++)
            {
                //if ((g.materials[i] & 0x1000) != 0)
                {
                    int off = i * 3;
                    int i0 = vertices[g.triangles[off]];
                    int i1 = vertices[g.triangles[off + 1]];
                    int i2 = vertices[g.triangles[off + 2]];

                    tc.AddTriangle(i0, i1, i2, TriangleType.Object);
                    //if(t != -1) s.SetTriangleExtra(t, g.materials[0], 0, 0);
                }
            }
        }

        /*
        int doodadset = wi.doodadset;
        if (doodadset < wmo.nDoodadSets)
        {
            uint firstDoodad = wmo.doodads[doodadset].firstInstance;
            uint nDoodads = wmo.doodads[doodadset].nInstances;

            for (uint i = 0; i < nDoodads; i++)
            {
                uint d = firstDoodad + i;
                ModelInstance mi = wmo.doodadInstances[d];
                if (mi != null)
                {
                    //logger.WriteLine("I got model " + mi.model.fileName + " at " + mi.pos);
                    //AddTrianglesGroupDoodads(s, mi, wi.dir, wi.pos, 0.0f); // DOes not work :(
                }
            }
        }
        */
    }

    private static void AddTrianglesGroupDoodads(TriangleCollection s, ModelInstance mi, Vector3 world_dir, Vector3 world_off, float rot)
    {
        float dx = mi.pos.X;
        float dy = mi.pos.Y;
        float dz = mi.pos.Z;

        Rotate(dx, dz, rot + 90f, out dx, out dz);

        dx += world_off.X;
        dy += world_off.Y;
        dz += world_off.Z;

        Quaternion q;
        q.X = mi.dir.Z;
        q.Y = mi.dir.X;
        q.Z = mi.dir.Y;
        q.W = mi.w;

        Matrix4x4 rotMatrix = Matrix4x4.CreateFromQuaternion(q);

        Model m = mi.model;

        if (m.boundingTriangles == null)
        {
            return;
        }

        int nBoundingVertices = m.boundingVertices.Length / 3;

        Span<int> vertices = stackalloc int[nBoundingVertices];

        for (int i = 0; i < nBoundingVertices; i++)
        {
            int off = i * 3;
            float x = m.boundingVertices[off];
            float y = m.boundingVertices[off + 2];
            float z = m.boundingVertices[off + 1];
            x *= mi.scale;
            y *= mi.scale;
            z *= -mi.scale;

            Vector3 pos = new(x, y, z);
            Vector3 new_pos = Vector3.Transform(pos, rotMatrix);
            x = pos.X;
            y = pos.Y;
            z = pos.Z;

            float dir_x = world_dir.Z;
            float dir_y = world_dir.Y - 90;
            float dir_z = -world_dir.X;

            Rotate(z, y, dir_x, out z, out y);
            Rotate(x, y, dir_z, out x, out y);
            Rotate(x, z, dir_y, out x, out z);

            float xx = x + dx;
            float yy = y + dy;
            float zz = -z + dz;

            float finalx = ChunkReader.ZEROPOINT - zz;
            float finaly = ChunkReader.ZEROPOINT - xx;
            float finalz = yy;
            vertices[i] = s.AddVertex(finalx, finaly, finalz);
        }

        int nBoundingTriangles = m.boundingTriangles.Length / 3;
        for (uint i = 0; i < nBoundingTriangles; i++)
        {
            uint off = i * 3;
            int v0 = vertices[m.boundingTriangles[off]];
            int v1 = vertices[m.boundingTriangles[off + 1]];
            int v2 = vertices[m.boundingTriangles[off + 2]];
            s.AddTriangle(v0, v2, v1, TriangleType.Model);
        }
    }

    [SkipLocalsInit]
    private static void AddTriangles(TriangleCollection s, ModelInstance mi)
    {
        float dx = mi.pos.X;
        float dy = mi.pos.Y;
        float dz = mi.pos.Z;

        float dir_x = mi.dir.Z;
        float dir_y = mi.dir.Y - 90; // -90 is correct!
        float dir_z = -mi.dir.X;

        Model m = mi.model;

        if (m.boundingTriangles == null)
        {
            return;

            // /cry no bouding info, revert to normal vertives
            /*
				ModelView mv = m.view[0]; // View number 1 ?!?!
				if (mv == null) return;
				int[] vertices = new int[m.vertices.Length / 3];
				for (uint i = 0; i < m.vertices.Length / 3; i++)
				{
					float x = m.vertices[i * 3];
					float y = m.vertices[i * 3 + 2];
					float z = m.vertices[i * 3 + 1];
					x *= mi.sc;
					y *= mi.sc;
					z *= mi.sc;

					rotate(y, z, dir_x, out y, out z);
					rotate(x, y, dir_z, out x, out y);
					rotate(x, z, dir_y, out x, out z);

					float xx = x + dx;
					float yy = y + dy;
					float zz = -z + dz;

					float finalx = ChunkReader.ZEROPOINT - zz;
					float finaly = ChunkReader.ZEROPOINT - xx;
					float finalz = yy;

					vertices[i] = s.AddVertex(finalx, finaly, finalz);
				}

				for (int i = 0; i < mv.triangleList.Length / 3; i++)
				{
					int off = i * 3;
					UInt16 vi0 = mv.triangleList[off];
					UInt16 vi1 = mv.triangleList[off + 1];
					UInt16 vi2 = mv.triangleList[off + 2];

					int ind0 = mv.indexList[vi0];
					int ind1 = mv.indexList[vi1];
					int ind2 = mv.indexList[vi2];

					int v0 = vertices[ind0];
					int v1 = vertices[ind1];
					int v2 = vertices[ind2];
					s.AddTriangle(v0, v1, v2, ChunkedTriangleCollection.TriangleFlagModel);
				}
				*/
        }

        int nBoundingVertices = m.boundingVertices.Length / 3;

        Span<int> vertices = stackalloc int[nBoundingVertices];

        for (int i = 0; i < nBoundingVertices; i++)
        {
            int off = i * 3;
            float x = m.boundingVertices[off];
            float y = m.boundingVertices[off + 2];
            float z = m.boundingVertices[off + 1];

            Rotate(z, y, dir_x, out z, out y);
            Rotate(x, y, dir_z, out x, out y);
            Rotate(x, z, dir_y, out x, out z);

            x *= mi.scale;
            y *= mi.scale;
            z *= mi.scale;

            float xx = x + dx;
            float yy = y + dy;
            float zz = -z + dz;

            float finalx = ChunkReader.ZEROPOINT - zz;
            float finaly = ChunkReader.ZEROPOINT - xx;
            float finalz = yy;

            vertices[i] = s.AddVertex(finalx, finaly, finalz);
        }

        int nBoundingTriangles = m.boundingTriangles.Length / 3;
        for (uint i = 0; i < nBoundingTriangles; i++)
        {
            uint off = i * 3;
            int v0 = vertices[m.boundingTriangles[off]];
            int v1 = vertices[m.boundingTriangles[off + 1]];
            int v2 = vertices[m.boundingTriangles[off + 2]];
            s.AddTriangle(v0, v1, v2, TriangleType.Model);
        }
    }

    [SkipLocalsInit]
    private static void ChunkGetCoordForPoint(MapChunk c, int row, int col,
                                      out float x, out float y, out float z)
    {
        int off = ((row * 17) + col) * 3;
        x = ChunkReader.ZEROPOINT - c.vertices[off + 2];
        y = ChunkReader.ZEROPOINT - c.vertices[off];
        z = c.vertices[off + 1];
    }

    [SkipLocalsInit]
    private static void ChunkGetCoordForMiddlePoint(MapChunk c, int row, int col,
                                        out float x, out float y, out float z)
    {
        int off = (9 + (row * 17) + col) * 3;
        x = ChunkReader.ZEROPOINT - c.vertices[off + 2];
        y = ChunkReader.ZEROPOINT - c.vertices[off];
        z = c.vertices[off + 1];
    }

    [SkipLocalsInit]
    public static void Rotate(float x, float y, float angle, out float nx, out float ny)
    {
        float rot = angle / 360.0f * Tau;
        float c_y = Cos(rot);
        float s_y = Sin(rot);

        nx = (c_y * x) - (s_y * y);
        ny = (s_y * x) + (c_y * y);
    }
}