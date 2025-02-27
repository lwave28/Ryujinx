﻿using Ryujinx.Memory.Range;
using System.Collections.Generic;

namespace Ryujinx.Memory.Tracking
{
    /// <summary>
    /// Manages memory tracking for a given virutal/physical memory block.
    /// </summary>
    public class MemoryTracking
    {
        private readonly IVirtualMemoryManager _memoryManager;

        // Only use these from within the lock.
        private readonly NonOverlappingRangeList<VirtualRegion> _virtualRegions;

        // Only use these from within the lock.
        private readonly VirtualRegion[] _virtualResults = new VirtualRegion[10];

        private readonly int _pageSize;

        /// <summary>
        /// This lock must be obtained when traversing or updating the region-handle hierarchy.
        /// It is not required when reading dirty flags.
        /// </summary>
        internal object TrackingLock = new object();

        public bool EnablePhysicalProtection { get; set; }

        /// <summary>
        /// Create a new tracking structure for the given "physical" memory block,
        /// with a given "virtual" memory manager that will provide mappings and virtual memory protection.
        /// </summary>
        /// <param name="memoryManager">Virtual memory manager</param>
        /// <param name="block">Physical memory block</param>
        /// <param name="pageSize">Page size of the virtual memory space</param>
        public MemoryTracking(IVirtualMemoryManager memoryManager, int pageSize)
        {
            _memoryManager = memoryManager;
            _pageSize = pageSize;

            _virtualRegions = new NonOverlappingRangeList<VirtualRegion>();
        }

        private (ulong address, ulong size) PageAlign(ulong address, ulong size)
        {
            ulong pageMask = (ulong)_pageSize - 1;
            ulong rA = address & ~pageMask;
            ulong rS = ((address + size + pageMask) & ~pageMask) - rA;
            return (rA, rS);
        }

        /// <summary>
        /// Indicate that a virtual region has been mapped, and which physical region it has been mapped to.
        /// Should be called after the mapping is complete.
        /// </summary>
        /// <param name="va">Virtual memory address</param>
        /// <param name="size">Size to be mapped</param>
        public void Map(ulong va, ulong size)
        {
            // A mapping may mean we need to re-evaluate each VirtualRegion's affected area.
            // Find all handles that overlap with the range, we need to recalculate their physical regions

            lock (TrackingLock)
            {
                var results = _virtualResults;
                int count = _virtualRegions.FindOverlapsNonOverlapping(va, size, ref results);

                for (int i = 0; i < count; i++)
                {
                    VirtualRegion region = results[i];

                    // If the region has been fully remapped, signal that it has been mapped again.
                    bool remapped = _memoryManager.IsRangeMapped(region.Address, region.Size);
                    if (remapped)
                    {
                        region.SignalMappingChanged(true);
                    }

                    region.UpdateProtection();
                }
            }
        }

        /// <summary>
        /// Indicate that a virtual region has been unmapped.
        /// Should be called before the unmapping is complete.
        /// </summary>
        /// <param name="va">Virtual memory address</param>
        /// <param name="size">Size to be unmapped</param>
        public void Unmap(ulong va, ulong size)
        {
            // An unmapping may mean we need to re-evaluate each VirtualRegion's affected area.
            // Find all handles that overlap with the range, we need to notify them that the region was unmapped.

            lock (TrackingLock)
            {
                var results = _virtualResults;
                int count = _virtualRegions.FindOverlapsNonOverlapping(va, size, ref results);

                for (int i = 0; i < count; i++)
                {
                    VirtualRegion region = results[i];

                    region.SignalMappingChanged(false);
                }
            }
        }

        /// <summary>
        /// Get a list of virtual regions that a handle covers.
        /// </summary>
        /// <param name="va">Starting virtual memory address of the handle</param>
        /// <param name="size">Size of the handle's memory region</param>
        /// <returns>A list of virtual regions within the given range</returns>
        internal List<VirtualRegion> GetVirtualRegionsForHandle(ulong va, ulong size)
        {
            List<VirtualRegion> result = new List<VirtualRegion>();
            _virtualRegions.GetOrAddRegions(result, va, size, (va, size) => new VirtualRegion(this, va, size));

            return result;
        }

        /// <summary>
        /// Remove a virtual region from the range list. This assumes that the lock has been acquired.
        /// </summary>
        /// <param name="region">Region to remove</param>
        internal void RemoveVirtual(VirtualRegion region)
        {
            _virtualRegions.Remove(region);
        }

        /// <summary>
        /// Obtains a memory tracking handle for the given virtual region, with a specified granularity. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="granularity">Desired granularity of write tracking</param>
        /// <returns>The memory tracking handle</returns>
        public MultiRegionHandle BeginGranularTracking(ulong address, ulong size, ulong granularity)
        {
            (address, size) = PageAlign(address, size);

            return new MultiRegionHandle(this, address, size, granularity);
        }

        /// <summary>
        /// Obtains a smart memory tracking handle for the given virtual region, with a specified granularity. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="granularity">Desired granularity of write tracking</param>
        /// <returns>The memory tracking handle</returns>
        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ulong granularity)
        {
            (address, size) = PageAlign(address, size);

            return new SmartMultiRegionHandle(this, address, size, granularity);
        }

        /// <summary>
        /// Obtains a memory tracking handle for the given virtual region. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <returns>The memory tracking handle</returns>
        public RegionHandle BeginTracking(ulong address, ulong size)
        {
            (address, size) = PageAlign(address, size);

            lock (TrackingLock)
            {
                RegionHandle handle = new RegionHandle(this, address, size, _memoryManager.IsRangeMapped(address, size));

                return handle;
            }
        }

        /// <summary>
        /// Signal that a virtual memory event happened at the given location (one byte).
        /// </summary>
        /// <param name="address">Virtual address accessed</param>
        /// <param name="write">Whether the address was written to or read</param>
        /// <returns>True if the event triggered any tracking regions, false otherwise</returns>
        public bool VirtualMemoryEventTracking(ulong address, bool write)
        {
            return VirtualMemoryEvent(address, 1, write);
        }

        /// <summary>
        /// Signal that a virtual memory event happened at the given location.
        /// </summary>
        /// <param name="address">Virtual address accessed</param>
        /// <param name="size">Size of the region affected in bytes</param>
        /// <param name="write">Whether the region was written to or read</param>
        /// <returns>True if the event triggered any tracking regions, false otherwise</returns>
        public bool VirtualMemoryEvent(ulong address, ulong size, bool write)
        {
            // Look up the virtual region using the region list.
            // Signal up the chain to relevant handles.

            lock (TrackingLock)
            {
                var results = _virtualResults;
                int count = _virtualRegions.FindOverlapsNonOverlapping(address, size, ref results);

                if (count == 0)
                {
                    if (!_memoryManager.IsMapped(address))
                    {
                        throw new InvalidMemoryRegionException();
                    }

                    _memoryManager.TrackingReprotect(address & ~(ulong)(_pageSize - 1), (ulong)_pageSize, MemoryPermission.ReadAndWrite);
                    return false; // We can't handle this - it's probably a real invalid access.
                }

                for (int i = 0; i < count; i++)
                {
                    VirtualRegion region = results[i];
                    region.Signal(address, size, write);
                }
            }

            return true;
        }

        /// <summary>
        /// Reprotect a given virtual region. The virtual memory manager will handle this.
        /// </summary>
        /// <param name="region">Region to reprotect</param>
        /// <param name="permission">Memory permission to protect with</param>
        internal void ProtectVirtualRegion(VirtualRegion region, MemoryPermission permission)
        {
            _memoryManager.TrackingReprotect(region.Address, region.Size, permission);
        }

        /// <summary>
        /// Returns the number of virtual regions currently being tracked.
        /// Useful for tests and metrics.
        /// </summary>
        /// <returns>The number of virtual regions</returns>
        public int GetRegionCount()
        {
            lock (TrackingLock)
            {
                return _virtualRegions.Count;
            }
        }
    }
}
