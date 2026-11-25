using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace JustDance.Services;

public sealed record PapHeaderInfo(bool IsValid, ushort AnimCount, List<string> AnimationNames);

/// <summary>
/// Reads FFXIV .pap headers and patches internal animation names in-place.
/// PAP layout: magic "pap " (0x00), version (0x04), animCount (0x06),
/// infoOffset (0x0E), havokOffset (0x12), footerOffset (0x16).
/// </summary>
public static class PapInspector
{
    private const int AnimInfoStride = 0x28;
    private const int AnimInfoNameBytes = 0x20;

    public static PapHeaderInfo ReadHeader(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x20)
                return new PapHeaderInfo(false, 0, new List<string>());

            var header = new byte[0x20];
            if (fs.Read(header, 0, header.Length) < 0x20)
                return new PapHeaderInfo(false, 0, new List<string>());

            if (!HasPapMagic(header))
                return new PapHeaderInfo(false, 0, new List<string>());

            ushort animCount = BitConverter.ToUInt16(header, 0x06);
            if (animCount == 0)
                return new PapHeaderInfo(true, 0, new List<string>());

            uint infoOffset = ResolveInfoOffset(header, fs.Length);
            if (infoOffset == 0)
                return new PapHeaderInfo(true, animCount, new List<string>());

            int entriesToRead = Math.Min((int)animCount, 32);
            long maxReadable = fs.Length - infoOffset;
            int bytesToRead = (int)Math.Min(entriesToRead * AnimInfoStride, maxReadable);
            if (bytesToRead <= 0)
                return new PapHeaderInfo(true, animCount, new List<string>());

            var table = new byte[bytesToRead];
            fs.Position = infoOffset;
            int read = fs.Read(table, 0, bytesToRead);

            var names = ParseInfoTableNames(table.AsSpan(0, read), animCount);
            return new PapHeaderInfo(true, animCount, names);
        }
        catch
        {
            return new PapHeaderInfo(false, 0, new List<string>());
        }
    }

    public static List<string> ReadAnimationNames(byte[] papData)
    {
        if (papData.Length < 0x20 || !HasPapMagic(papData))
            return new List<string>();

        ushort animCount = BitConverter.ToUInt16(papData, 0x06);
        if (animCount == 0)
            return new List<string>();

        uint infoOffset = ResolveInfoOffset(papData, papData.Length);
        if (infoOffset == 0)
            return new List<string>();

        return ParseInfoTableNames(papData.AsSpan((int)infoOffset), animCount);
    }

    public static string? ReadPrimaryAnimationName(byte[] papData)
    {
        var names = ReadAnimationNames(papData);
        return SelectPrimaryAnimationName(names);
    }

    public static string? SelectPrimaryAnimationName(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return null;

        if (names.Count == 1)
            return names[0];

        // Prefer explicit loop clip when a single .pap contains start/loop/end clips.
        var loopCandidate = names.FirstOrDefault(n =>
            n.EndsWith("_loop", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("_lp", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("loop", StringComparison.OrdinalIgnoreCase));

        return loopCandidate ?? names[0];
    }

    public static string ContentTag(byte[] data)
    {
        var hash = SHA1.HashData(data);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>
    /// Replaces all null-terminated occurrences of targetName with carrierName in-place
    /// so internal Havok offsets remain valid.
    /// </summary>
    public static (byte[]? data, int replaced, int found) RewriteAnimationName(
        byte[] papData,
        string targetName,
        string carrierName)
    {
        if (string.Equals(targetName, carrierName, StringComparison.Ordinal))
            return ((byte[])papData.Clone(), 1, 1);

        var result = (byte[])papData.Clone();
        var targetBytes = Encoding.ASCII.GetBytes(targetName);
        var carrierBytes = Encoding.ASCII.GetBytes(carrierName);
        int replaced = 0;
        int found = 0;

        for (int i = 0; i <= result.Length - targetBytes.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < targetBytes.Length; j++)
            {
                if (result[i + j] != targetBytes[j])
                {
                    match = false;
                    break;
                }
            }
            if (!match)
                continue;

            // Require null terminator after the match so partial substrings are never touched.
            if (i + targetBytes.Length < result.Length && result[i + targetBytes.Length] != 0)
                continue;

            found++;

            int available = targetBytes.Length;
            while (i + available < result.Length && result[i + available] == 0)
                available++;

            if (carrierBytes.Length + 1 <= available)
            {
                Array.Copy(carrierBytes, 0, result, i, carrierBytes.Length);
                for (int j = carrierBytes.Length; j < available; j++)
                    result[i + j] = 0;
                replaced++;
            }
        }

        if (found == 0 || replaced == 0)
            return (null, replaced, found);

        return (result, replaced, found);
    }

    private static bool HasPapMagic(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data[0] == 0x70 && data[1] == 0x61 && data[2] == 0x70;

    private static uint ResolveInfoOffset(ReadOnlySpan<byte> header, long totalLength)
    {
        uint infoOffset = BitConverter.ToUInt32(header.Slice(0x0E, 4));
        if (infoOffset > 0 && infoOffset < totalLength)
            return infoOffset;

        infoOffset = BitConverter.ToUInt32(header.Slice(0x10, 4));
        if (infoOffset > 0 && infoOffset < totalLength)
            return infoOffset;

        return 0;
    }

    private static List<string> ParseInfoTableNames(ReadOnlySpan<byte> table, ushort animCount)
    {
        var names = new List<string>();
        int maxEntries = Math.Min((int)animCount, 32);

        for (int i = 0; i < maxEntries; i++)
        {
            int entryOffset = i * AnimInfoStride;
            if (entryOffset >= table.Length)
                break;

            int maxNameLen = Math.Min(AnimInfoNameBytes, table.Length - entryOffset);
            int end = 0;
            while (end < maxNameLen && table[entryOffset + end] != 0)
                end++;

            if (end > 0)
            {
                var name = Encoding.ASCII.GetString(table.Slice(entryOffset, end));
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }

        // Fallback: single null-terminated string right at infoOffset if stride table was compact.
        if (names.Count == 0 && table.Length > 0)
        {
            int end = 0;
            while (end < Math.Min(table.Length, 128) && table[end] != 0)
                end++;

            if (end > 0)
            {
                var name = Encoding.ASCII.GetString(table.Slice(0, end));
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }

        return names;
    }
}
