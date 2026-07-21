// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.Core;
using Xunit;
using ZarSharp;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class ZArchiveGameTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharpemu-zar-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Inspect_FindsNestedEbootAndReadsParamJson()
    {
        var archivePath = Path.Combine(_root, "nested.zar");
        const string paramJson = """
            { "titleId": "PPSA12345", "contentVersion": "01.002.000" }
            """;
        CreateArchive(
            archivePath,
            ("Game/eboot.bin", [0x7f, (byte)'E', (byte)'L', (byte)'F']),
            ("Game/sce_sys/param.json", Encoding.UTF8.GetBytes(paramJson)),
            ("Game/sce_sys/icon0.png", [4, 5]),
            ("Game/sce_sys/pic0.png", [6]),
            ("Game/data/file.bin", [1, 2, 3]));

        var info = ZArchiveGame.Inspect(archivePath);

        Assert.Equal("Game/eboot.bin", info.EbootEntryPath);
        Assert.Contains("PPSA12345", info.ParamJson);
        Assert.Equal("Game/sce_sys/icon0.png", info.CoverEntryPath);
        Assert.Equal("Game/sce_sys/pic0.png", info.BackgroundEntryPath);
        Assert.Equal([4, 5], ZArchiveGame.ReadEntryBytes(archivePath, info.CoverEntryPath!));
        Assert.Equal(4 + Encoding.UTF8.GetByteCount(paramJson) + 2 + 1 + 3, info.UncompressedSize);
    }

    [Fact]
    public void ExtractToCache_MaterializesTheWholeGameAndReusesTheCache()
    {
        var archivePath = Path.Combine(_root, "game.zar");
        var cacheRoot = Path.Combine(_root, "cache");
        CreateArchive(
            archivePath,
            ("eboot.bin", [10, 20, 30]),
            ("sce_sys/param.json", Encoding.UTF8.GetBytes("{\"titleId\":\"PPSA00001\"}")),
            ("data/asset.bin", [40, 50]));

        var ebootPath = ZArchiveGame.ExtractToCache(archivePath, cacheRoot);
        var cachedEbootPath = ZArchiveGame.ExtractToCache(archivePath, cacheRoot);

        Assert.Equal(ebootPath, cachedEbootPath);
        Assert.Equal([10, 20, 30], File.ReadAllBytes(ebootPath));
        Assert.Equal(
            [40, 50],
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(ebootPath)!, "data", "asset.bin")));
    }

    [Fact]
    public void Inspect_RejectsArchiveWithoutEboot()
    {
        var archivePath = Path.Combine(_root, "missing-eboot.zar");
        CreateArchive(archivePath, ("data/asset.bin", [1, 2, 3]));

        var exception = Assert.Throws<InvalidDataException>(() => ZArchiveGame.Inspect(archivePath));

        Assert.Contains("eboot.bin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void CreateArchive(string archivePath, params (string Path, byte[] Data)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using var writer = ZArchiveWriter.Create(archivePath);
        foreach (var entry in entries)
        {
            using var source = new MemoryStream(entry.Data, writable: false);
            writer.WriteEntry(entry.Path, source);
        }

        writer.Complete();
    }
}
