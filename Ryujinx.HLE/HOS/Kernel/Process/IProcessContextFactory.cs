﻿using Ryujinx.Cpu;
using Ryujinx.Memory;

namespace Ryujinx.HLE.HOS.Kernel.Process
{
    interface IProcessContextFactory
    {
        IProcessContext Create(ulong addressSpaceSize, InvalidAccessHandler invalidAccessHandler);
    }
}
