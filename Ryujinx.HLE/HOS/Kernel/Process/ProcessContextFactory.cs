﻿using Ryujinx.Cpu;
using Ryujinx.Memory;

namespace Ryujinx.HLE.HOS.Kernel.Process
{
    class ProcessContextFactory : IProcessContextFactory
    {
        public IProcessContext Create(ulong addressSpaceSize, InvalidAccessHandler invalidAccessHandler)
        {
            return new ProcessContext(new AddressSpaceManager(addressSpaceSize));
        }
    }
}
