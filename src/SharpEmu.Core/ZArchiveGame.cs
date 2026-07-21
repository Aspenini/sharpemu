// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZarSharp;

namespace SharpEmu.Core;

/// <summary>Metadata needed to display and launch a game stored in a ZArchive.</summary>
public sealed record ZArchiveGameInfo(
    string EbootEntryPath,
    string? ParamJson,
    long UncompressedSize,
    string? CoverEntryPath,
    string? BackgroundEntryPath);

/// <summary>Inspects and materializes game folders stored as <c>.zar</c> files.</summary>
public static class ZArchiveGame
{
    private const string ArchiveExtension = ".zar";
    private const int CacheFormatVersion = 1;
    private const string CacheDirectoryName = "zar";

    /// <summary>Returns whether <paramref name="path"/> has the supported archive extension.</summary>
    public static bool IsArchivePath(string? path) =>
        string.Equals(Path.GetExtension(path), ArchiveExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads game metadata without extracting the archive.</summary>
    public static ZArchiveGameInfo Inspect(string archivePath)
    {
        var fullPath = ValidateArchivePath(archivePath);
        using var archive = ZArchiveReader.Open(fullPath);
        var eboot = FindEboot(archive);
        var gameDirectory = ArchiveDirectoryName(eboot.FullName);
        var paramJson = ReadTextEntry(archive, ArchiveCombine(gameDirectory, "sce_sys/param.json"))
            ?? ReadTextEntry(archive, ArchiveCombine(gameDirectory, "param.json"));
        var coverEntryPath = FindFirstFile(
            archive,
            gameDirectory,
            "sce_sys/icon0.png",
            "sce_sys/pic0.png");
        var backgroundEntryPath = FindFirstFile(
            archive,
            gameDirectory,
            "sce_sys/pic0.png",
            "sce_sys/pic1.png");

        long uncompressedSize = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.EntryType == ZArchiveEntryType.File)
            {
                try
                {
                    uncompressedSize = checked(uncompressedSize + entry.Length);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException("The archive's uncompressed size is too large.", exception);
                }
            }
        }

        return new ZArchiveGameInfo(
            eboot.FullName,
            paramJson,
            uncompressedSize,
            coverEntryPath,
            backgroundEntryPath);
    }

    /// <summary>Reads one file entry from an archive into memory.</summary>
    public static byte[] ReadEntryBytes(string archivePath, string entryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);
        var fullPath = ValidateArchivePath(archivePath);
        using var archive = ZArchiveReader.Open(fullPath);
        var entry = archive.GetEntry(entryPath);
        if (entry is null || entry.EntryType != ZArchiveEntryType.File)
        {
            throw new FileNotFoundException(
                $"The ZArchive entry was not found: {entryPath}",
                entryPath);
        }
        if (entry.Length > int.MaxValue)
        {
            throw new InvalidDataException(
                $"The ZArchive entry is too large to read into memory: {entryPath}");
        }

        var data = new byte[(int)entry.Length];
        using var input = entry.Open();
        input.ReadExactly(data);
        return data;
    }

    /// <summary>
    /// Extracts an archive into a persistent cache and returns the physical path to its
    /// <c>eboot.bin</c>. A physical tree is required because guest file APIs currently
    /// map <c>/app0</c> to host paths.
    /// </summary>
    public static string ExtractToCache(string archivePath, string? cacheRoot = null)
    {
        var fullPath = ValidateArchivePath(archivePath);
        var archiveInfo = new FileInfo(fullPath);
        cacheRoot = Path.GetFullPath(cacheRoot ?? GetDefaultCacheRoot());
        Directory.CreateDirectory(cacheRoot);

        var cacheIdentity = $"{fullPath}\n{archiveInfo.Length}\n{archiveInfo.LastWriteTimeUtc.Ticks}";
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cacheIdentity)));
        var extractionRoot = Path.Combine(cacheRoot, key);
        var statePath = Path.Combine(cacheRoot, $"{key}.json");
        var lockPath = Path.Combine(cacheRoot, $"{key}.lock");

        using var extractionLock = AcquireExtractionLock(lockPath);
        if (TryGetCachedEboot(
                extractionRoot,
                statePath,
                archiveInfo.Length,
                archiveInfo.LastWriteTimeUtc.Ticks,
                out var cachedEboot))
        {
            return cachedEboot;
        }

        if (Directory.Exists(extractionRoot))
        {
            Directory.Delete(extractionRoot, recursive: true);
        }
        if (File.Exists(statePath))
        {
            File.Delete(statePath);
        }

        var stagingRoot = $"{extractionRoot}.extracting-{Guid.NewGuid():N}";
        Directory.CreateDirectory(stagingRoot);
        try
        {
            using var archive = ZArchiveReader.Open(fullPath);
            var eboot = FindEboot(archive);
            foreach (var entry in archive.Entries)
            {
                var destination = GetSafeDestination(stagingRoot, entry.FullName);
                if (entry.EntryType == ZArchiveEntryType.Directory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                using var input = entry.Open();
                using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.SequentialScan);
                input.CopyTo(output, 1024 * 1024);
            }

            var stagedEboot = GetSafeDestination(stagingRoot, eboot.FullName);
            if (!File.Exists(stagedEboot))
            {
                throw new InvalidDataException("The archive's eboot.bin could not be extracted.");
            }

            Directory.Move(stagingRoot, extractionRoot);
            var state = new CacheState(
                CacheFormatVersion,
                archiveInfo.Length,
                archiveInfo.LastWriteTimeUtc.Ticks,
                eboot.FullName);
            File.WriteAllText(statePath, JsonSerializer.Serialize(state));
            return GetSafeDestination(extractionRoot, eboot.FullName);
        }
        catch
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }

            throw;
        }
    }

    private static string ValidateArchivePath(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        if (!IsArchivePath(fullPath))
        {
            throw new ArgumentException("The game archive must use the .zar extension.", nameof(archivePath));
        }
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The game archive was not found.", fullPath);
        }

        return fullPath;
    }

    private static ZArchiveEntry FindEboot(ZArchiveReader archive)
    {
        var candidates = archive.Entries
            .Where(static entry =>
                entry.EntryType == ZArchiveEntryType.File &&
                string.Equals(entry.Name, "eboot.bin", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static entry => entry.FullName.Count(character => character == '/'))
            .ThenBy(static entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return candidates.FirstOrDefault()
            ?? throw new InvalidDataException("The ZArchive does not contain an eboot.bin file.");
    }

    private static string? ReadTextEntry(ZArchiveReader archive, string entryPath)
    {
        var entry = archive.GetEntry(entryPath);
        if (entry is null || entry.EntryType != ZArchiveEntryType.File)
        {
            return null;
        }

        using var input = entry.Open();
        using var reader = new StreamReader(
            input,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        return reader.ReadToEnd();
    }

    private static string? FindFirstFile(
        ZArchiveReader archive,
        string gameDirectory,
        params string[] relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            var entry = archive.GetEntry(ArchiveCombine(gameDirectory, relativePath));
            if (entry is not null && entry.EntryType == ZArchiveEntryType.File)
            {
                return entry.FullName;
            }
        }

        return null;
    }

    private static string ArchiveDirectoryName(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string ArchiveCombine(string directory, string relativePath) =>
        string.IsNullOrEmpty(directory) ? relativePath : $"{directory}/{relativePath}";

    private static string GetSafeDestination(string root, string archivePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var relativePath = archivePath.Replace('/', Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var rootPrefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, PathComparison))
        {
            throw new InvalidDataException($"Archive entry escapes the extraction root: {archivePath}");
        }

        return destination;
    }

    private static bool TryGetCachedEboot(
        string extractionRoot,
        string statePath,
        long archiveLength,
        long archiveWriteTicks,
        out string ebootPath)
    {
        ebootPath = string.Empty;
        if (!Directory.Exists(extractionRoot) || !File.Exists(statePath))
        {
            return false;
        }

        try
        {
            var state = JsonSerializer.Deserialize<CacheState>(File.ReadAllText(statePath));
            if (state is null ||
                state.FormatVersion != CacheFormatVersion ||
                state.ArchiveLength != archiveLength ||
                state.ArchiveWriteTimeUtcTicks != archiveWriteTicks ||
                string.IsNullOrEmpty(state.EbootEntryPath))
            {
                return false;
            }

            ebootPath = GetSafeDestination(extractionRoot, state.EbootEntryPath);
            return File.Exists(ebootPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ebootPath = string.Empty;
            return false;
        }
    }

    private static FileStream AcquireExtractionLock(string lockPath)
    {
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static string GetDefaultCacheRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        return Path.Combine(localData, "SharpEmu", "cache", CacheDirectoryName);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record CacheState(
        int FormatVersion,
        long ArchiveLength,
        long ArchiveWriteTimeUtcTicks,
        string EbootEntryPath);
}
