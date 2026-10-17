// Ported from s&box's Sandbox.Mounting.GoldSrc (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Slopbox.Mounting.GoldSrc;

public sealed class WadHeader
{
    public string Magic;
    public int LumpCount;
    public int DirectoryOffset;
}

public sealed class WadLump
{
    public int Offset;
    public int DiskSize;
    public int Size;
    public byte Type;
    public byte Compression;
    public short Padding;
    public string Name;
}

/// <summary>A WAD3 texture archive. Only the directory is read up front; lumps are read when asked for.</summary>
public sealed class Wad
{
    /// <summary>The lump type of a mip texture.</summary>
    public const byte MipTexture = 67;

    public WadHeader Header { get; private set; }
    public List<WadLump> Lumps { get; } = [];

    private string _filePath;

    public void LoadWadFile(string filePath)
    {
        _filePath = filePath;

        using var reader = new BinaryReader(File.OpenRead(filePath), Encoding.ASCII);

        Header = new WadHeader
        {
            Magic = Encoding.ASCII.GetString(reader.ReadBytes(4)),
            LumpCount = reader.ReadInt32(),
            DirectoryOffset = reader.ReadInt32(),
        };

        if (Header.Magic != "WAD3") throw new InvalidDataException("Not a WAD3 file.");

        reader.BaseStream.Seek(Header.DirectoryOffset, SeekOrigin.Begin);

        for (int i = 0; i < Header.LumpCount; i++)
        {
            Lumps.Add(new WadLump
            {
                Offset = reader.ReadInt32(),
                DiskSize = reader.ReadInt32(),
                Size = reader.ReadInt32(),
                Type = reader.ReadByte(),
                Compression = reader.ReadByte(),
                Padding = reader.ReadInt16(),
                Name = Mdl.MdlFile.GetNullTerminatedString(reader.ReadBytes(16)),
            });
        }
    }

    public byte[] GetLumpData(string lumpName)
    {
        WadLump lump = Lumps.Find(l => l.Name.Equals(lumpName, StringComparison.OrdinalIgnoreCase));
        if (lump == null) return null;

        using var reader = new BinaryReader(File.OpenRead(_filePath));
        reader.BaseStream.Seek(lump.Offset, SeekOrigin.Begin);
        return reader.ReadBytes(lump.DiskSize);
    }
}
