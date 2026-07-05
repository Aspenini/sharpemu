// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Threading;

namespace SharpEmu.Libs.CommonDialog;

public static class MsgDialogExports
{
    private const int AlreadyInitialized = unchecked((int)0x80B80002);
    private static int _initialized;

    [SysAbiExport(
        Nid = "lDqxaY1UbEo",
        ExportName = "sceMsgDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogInitialize(CpuContext ctx)
    {
        var result = Interlocked.Exchange(ref _initialized, 1) == 0
            ? 0
            : AlreadyInitialized;
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
