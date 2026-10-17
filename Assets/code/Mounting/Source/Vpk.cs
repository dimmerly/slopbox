// Ported from s&box's Sandbox.Mounting.Source (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Slopbox.Mounting.Source;

/// <summary>One file in a VPK archive.</summary>
internal sealed class VpkEntry
{
    public string FullPath;
    public ushort PreloadBytes;
    public ushort ArchiveIndex;
    public uint EntryOffset;
    public uint EntryLength;
    public byte[] PreloadData;

    public int TotalSize => PreloadBytes + (int)EntryLength;
}

/// <summary>
/// A Valve pack file, read from its "_dir.vpk" directory. The files' data sits in numbered archives beside it
/// ("_000.vpk" and on), opened as they are first read.
/// </summary>
internal sealed class VpkArchive : IDisposable
{
    private const uint HeaderMarker = 0x55aa1234;
    private const ushort EmbeddedInDirFile = 0x7fff;
    private const ushort EntryTerminator = 0xffff;

    private readonly string _dirFilePath;
    private readonly Dictionary<int, FileStream> _archiveStreams = new();
    private int _embeddedDataOffset;

    public string BaseName { get; }
    public string Directory { get; }
    public List<VpkEntry> Entries { get; } = new();

    public VpkArchive(string dirFilePath)
    {
        _dirFilePath = dirFilePath;
        Directory = Path.GetDirectoryName(dirFilePath) ?? "";

        string fileName = Path.GetFileNameWithoutExtension(dirFilePath);
        BaseName = fileName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;

        ReadDirectory();
    }

    public byte[] Read(VpkEntry entry)
    {
        var data = new byte[entry.TotalSize];
        int offset = 0;

        if (entry.PreloadData != null && entry.PreloadBytes > 0)
        {
            Array.Copy(entry.PreloadData, 0, data, 0, entry.PreloadBytes);
            offset += entry.PreloadBytes;
        }

        if (entry.EntryLength > 0)
        {
            FileStream stream = GetArchiveStream(entry.ArchiveIndex);
            if (stream == null) return null;

            long seek = entry.ArchiveIndex == EmbeddedInDirFile ? _embeddedDataOffset + entry.EntryOffset : entry.EntryOffset;
            lock (stream)
            {
                stream.Seek(seek, SeekOrigin.Begin);
                stream.ReadExactly(data, offset, (int)entry.EntryLength);
            }
        }

        return data;
    }

    public void Dispose()
    {
        lock (_archiveStreams)
        {
            foreach (FileStream stream in _archiveStreams.Values) stream?.Dispose();
            _archiveStreams.Clear();
        }
    }

    private void ReadDirectory()
    {
        using var stream = File.OpenRead(_dirFilePath);
        using var reader = new BinaryReader(stream, Encoding.ASCII);

        int headerSize, directorySize;
        if (reader.ReadUInt32() == HeaderMarker)
        {
            int version = reader.ReadInt32();
            directorySize = reader.ReadInt32();
            headerSize = version switch
            {
                1 => 3 * 4,
                2 => 7 * 4,
                _ => throw new InvalidDataException($"Unknown VPK version {version}"),
            };
            stream.Seek(headerSize, SeekOrigin.Begin);
        }
        else
        {
            // Version 0 has no header at all.
            stream.Seek(0, SeekOrigin.Begin);
            directorySize = (int)stream.Length;
            headerSize = 0;
        }

        _embeddedDataOffset = headerSize + directorySize;

        while (true)
        {
            string extension = ReadString(reader);
            if (extension.Length == 0) break;

            while (true)
            {
                string path = ReadString(reader);
                if (path.Length == 0) break;

                while (true)
                {
                    string name = ReadString(reader);
                    if (name.Length == 0) break;

                    reader.ReadUInt32(); // CRC
                    var entry = new VpkEntry
                    {
                        PreloadBytes = reader.ReadUInt16(),
                        ArchiveIndex = reader.ReadUInt16(),
                        EntryOffset = reader.ReadUInt32(),
                        EntryLength = reader.ReadUInt32(),
                    };

                    if (reader.ReadUInt16() != EntryTerminator) throw new InvalidDataException("Bad VPK entry terminator");
                    if (entry.PreloadBytes > 0) entry.PreloadData = reader.ReadBytes(entry.PreloadBytes);

                    entry.FullPath = path == " " ? $"{name}.{extension}" : $"{path}/{name}.{extension}";
                    Entries.Add(entry);
                }
            }
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        var sb = new StringBuilder();
        for (byte b = reader.ReadByte(); b != 0; b = reader.ReadByte()) sb.Append((char)b);
        return sb.ToString();
    }

    private FileStream GetArchiveStream(int archiveIndex)
    {
        lock (_archiveStreams)
        {
            if (_archiveStreams.TryGetValue(archiveIndex, out FileStream existing)) return existing;

            string archivePath = archiveIndex == EmbeddedInDirFile
                ? _dirFilePath
                : Path.Combine(Directory, $"{BaseName}_{archiveIndex:D3}.vpk");
            if (!File.Exists(archivePath)) return null;

            var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _archiveStreams[archiveIndex] = stream;
            return stream;
        }
    }
}
