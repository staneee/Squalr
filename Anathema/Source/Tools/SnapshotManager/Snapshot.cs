﻿using Binarysharp.MemoryManagement;
using Binarysharp.MemoryManagement.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace Anathema
{
    /// <summary>
    /// Defines data contained in a single snapshot
    /// </summary>
    class Snapshot : IEnumerable, IProcessObserver
    {
        private MemorySharp MemoryEditor;

        protected SnapshotRegion[] SnapshotRegions;
        protected ConcurrentBag<SnapshotRegion> DeallocatedRegions;

        // Variables to send to the display when displaying this snapshot
        private String ScanMethod;
        private DateTime TimeStamp;
        private Type ElementType;

        #region Constructors

        /// <summary>
        /// Constructor for creating an empty snapshot
        /// </summary>
        public Snapshot()
        {
            this.SnapshotRegions = null;

            Initialize();
        }

        /// <summary>
        /// Constructor to clone a snapshot from another snapshot
        /// </summary>
        /// <param name="BaseSnapshot"></param>
        public Snapshot(Snapshot BaseSnapshot)
        {
            List<SnapshotRegion> Regions = new List<SnapshotRegion>();

            foreach (SnapshotRegion Region in BaseSnapshot.GetSnapshotRegions())
            {
                Regions.Add(new SnapshotRegion(Region));
                Regions.Last().SetCurrentValues(Region.GetCurrentValues());
                Regions.Last().SetElementType(Region.GetElementType());
            }
            SnapshotRegions = Regions.ToArray();

            Initialize();
        }

        /// <summary>
        /// Constructor to create a snapshot from various regions
        /// </summary>
        /// <param name="SnapshotRegions"></param>
        public Snapshot(SnapshotRegion[] SnapshotRegions)
        {
            this.SnapshotRegions = SnapshotRegions;

            Initialize();
        }

        #endregion

        /// <summary>
        /// Indexer to allow the retrieval of the element at the specified index. Note that this does NOT index into a region.
        /// </summary>
        /// <param name="Index"></param>
        /// <returns></returns>
        public SnapshotElement this[Int32 Index]
        {
            get
            {
                foreach (SnapshotRegion MemoryRegion in this)
                {
                    if (Index - MemoryRegion.RegionSize >= 0)
                        Index -= MemoryRegion.RegionSize;
                    else
                        return MemoryRegion[Index];
                }
                return null;
            }
        }

        #region Initialization

        public void Initialize()
        {
            this.DeallocatedRegions = new ConcurrentBag<SnapshotRegion>();
            InitializeObserver();
            MergeRegions();
        }

        public void InitializeObserver()
        {
            ProcessSelector.GetInstance().Subscribe(this);
        }

        public void UpdateMemoryEditor(MemorySharp MemoryEditor)
        {
            this.MemoryEditor = MemoryEditor;
        }

        #endregion

        #region Property Accessors

        /// <summary>
        /// Sets the underlying data type of the element to an arbitrary data type of the specified size
        /// </summary>
        /// <param name="VariableSize"></param>
        public void SetVariableSize(Int32 VariableSize)
        {
            switch (VariableSize)
            {
                case sizeof(SByte): SetElementType(typeof(SByte)); break;
                case sizeof(Int16): SetElementType(typeof(Int16)); break;
                case sizeof(Int32): SetElementType(typeof(Int32)); break;
                case sizeof(Int64): SetElementType(typeof(Int64)); break;
            }
        }

        public void SetTimeStampToNow()
        {
            TimeStamp = DateTime.Now;
        }

        public DateTime GetTimeStamp()
        {
            return TimeStamp;
        }

        public Type GetElementType()
        {
            return ElementType;
        }

        /// <summary>
        /// Updates type of every element with the specified type
        /// </summary>
        /// <param name="ElementType"></param>
        public void SetElementType(Type ElementType)
        {
            this.ElementType = ElementType;
            foreach (SnapshotRegion Region in this)
                Region.SetElementType(ElementType);
        }

        public void SetScanMethod(String ScanMethod)
        {
            this.ScanMethod = ScanMethod;
        }

        public String GetScanMethod()
        {
            return ScanMethod;
        }

        public SnapshotRegion[] GetSnapshotRegions()
        {
            return SnapshotRegions;
        }

        public Int32 GetRegionCount()
        {
            return SnapshotRegions.Length;
        }

        public UInt64 GetMemorySize()
        {
            return (UInt64)SnapshotRegions.AsEnumerable().Sum(x => (Int64)x.RegionSize);
        }

        #endregion

        /// <summary>
        /// Reads memory for every snapshot, with each region storing the current and previous read values.
        /// 
        /// Handles ScanFailedExceptions
        /// </summary>
        /// <param name="KeepPreviousValues"></param>
        public void ReadAllSnapshotMemory()
        {
            Parallel.ForEach(SnapshotRegions, (SnapshotRegion) =>
            {
                try
                {
                    SnapshotRegion.ReadAllSnapshotMemory(MemoryEditor, true);
                }
                catch (ScanFailedException)
                {
                    if (!DeallocatedRegions.Contains(SnapshotRegion))
                        DeallocatedRegions.Add(SnapshotRegion);
                }
            });

            // Handle invalid reads
            if (DeallocatedRegions.Count > 0)
                MaskDeallocatedRegions();

            SetTimeStampToNow();
        }

        /// <summary>
        /// Reads and returns the memory of the specified region. Does not store the memory. Used for scans that
        /// just need to quickly grab memory with no use for it afterwards (chunk scan, tree scan).
        /// </summary>
        /// <param name="SnapshotRegion"></param>
        /// <returns></returns>
        public Byte[] ReadSnapshotMemoryOfRegion(SnapshotRegion SnapshotRegion)
        {
            try
            {
                SnapshotRegion.ReadAllSnapshotMemory(MemoryEditor, false);
            }
            catch (ScanFailedException)
            {
                if (!DeallocatedRegions.Contains(SnapshotRegion))
                    DeallocatedRegions.Add(SnapshotRegion);
            }

            // Grab the saved values
            Byte[] SnapshotRegionData = SnapshotRegion.GetCurrentValues();

            // Clear the values, so they do not persist in memory and the GC can get to them
            SnapshotRegion.SetCurrentValues(null, false);

            return SnapshotRegionData;
        }

        protected virtual void MaskDeallocatedRegions()
        {
            List<SnapshotRegion> NewSnapshotRegions = new List<SnapshotRegion>(SnapshotRegions);

            // Remove invalid items from collection
            foreach (SnapshotRegion Region in DeallocatedRegions)
                NewSnapshotRegions.Remove(Region);

            // Get current memory regions
            Snapshot Mask = SnapshotManager.GetInstance().SnapshotAllRegions();

            // Mask each region against the current virtual memory regions
            SnapshotRegion[] MaskedRegions = MaskRegions(Mask, DeallocatedRegions.ToArray());

            // Merge split regions back with the main list
            NewSnapshotRegions.AddRange(MaskedRegions);

            // Clear invalid items
            DeallocatedRegions = new ConcurrentBag<SnapshotRegion>();

            // Store result as main snapshot array
            this.SnapshotRegions = NewSnapshotRegions.ToArray();
        }

        public virtual SnapshotRegion[] GetValidRegions()
        {
            List<SnapshotRegion> ValidRegions = new List<SnapshotRegion>();

            foreach (SnapshotRegion Region in this)
                ValidRegions.AddRange(Region.GetValidRegions());

            return ValidRegions.ToArray();
        }

        public void MarkAllValid()
        {
            foreach (SnapshotRegion Region in this)
                Region.MarkAllValid();
        }

        public void MarkAllInvalid()
        {
            foreach (SnapshotRegion Region in this)
                Region.MarkAllInvalid();
        }

        /// <summary>
        /// Expands all memory regions in both directions based on the size of the current element type.
        /// Useful for filtering methods that isolate changing bytes (ie 1 byte of an 8 byte integer), where we would want to grow to recover the other 7 bytes.
        /// </summary>
        /// <param name="GrowAmount"></param>
        public void GrowAllRegions()
        {
            Int32 ExpandSize = Marshal.SizeOf(ElementType) - 1;

            foreach (SnapshotRegion Region in this)
            {
                Region.BaseAddress -= ExpandSize;
                Region.RegionSize += ExpandSize * 2;
            }
        }

        /// <summary>
        /// Expands all snapshot region's valid element ranges forward by the size of the current element type. This ensures
        /// That for multi-byte element types, that the elements following the address in question
        /// </summary>
        public void ExpandValidRegions()
        {
            Int32 ExpandSize = Marshal.SizeOf(ElementType) - 1;

            foreach (SnapshotRegion Region in this)
            {
                Region.ExpandValidRegions(ExpandSize);
            }
        }

        /// <summary>
        /// Masks the given memory regions against the memory regions of a given snapshot, keeping the common elements of the two.
        /// </summary>
        /// <param name="Mask"></param>
        public SnapshotRegion[] MaskRegions(Snapshot Mask, SnapshotRegion[] TargetRegions)
        {
            List<SnapshotRegion> ResultRegions = new List<SnapshotRegion>();

            // Initialize stacks with regions and masking regions
            Queue<SnapshotRegion> CandidateRegions = new Queue<SnapshotRegion>();
            Queue<SnapshotRegion> MaskingRegions = new Queue<SnapshotRegion>();

            foreach (SnapshotRegion Region in TargetRegions)
                CandidateRegions.Enqueue(Region);

            foreach (SnapshotRegion MaskRegion in Mask)
                MaskingRegions.Enqueue(MaskRegion);

            if (CandidateRegions.Count == 0 || MaskingRegions.Count == 0)
                return null;

            SnapshotRegion CurrentRegion;
            SnapshotRegion CurrentMask = MaskingRegions.Dequeue();

            while (CandidateRegions.Count > 0)
            {
                // Grab next region
                CurrentRegion = CandidateRegions.Dequeue();

                // Grab the next mask following the current region
                while ((UInt64)CurrentMask.EndAddress < (UInt64)CurrentRegion.BaseAddress)
                    CurrentMask = MaskingRegions.Dequeue();

                // Check for mask completely removing this region
                if ((UInt64)CurrentMask.BaseAddress > (UInt64)CurrentRegion.EndAddress)
                    continue;

                // Mask completely overlaps, just use the original region
                if (CurrentMask.BaseAddress == CurrentRegion.BaseAddress && CurrentMask.EndAddress == CurrentRegion.EndAddress)
                {
                    ResultRegions.Add(CurrentRegion);
                    continue;
                }

                // Mask is within bounds; Grab the masked portion of this region
                Int32 BaseOffset = 0;
                if ((UInt64)CurrentMask.BaseAddress > (UInt64)CurrentRegion.BaseAddress)
                    BaseOffset = (Int32)((UInt64)CurrentMask.BaseAddress - (UInt64)CurrentRegion.BaseAddress);

                ResultRegions.Add(new SnapshotRegion(CurrentRegion));
                ResultRegions.Last().BaseAddress = CurrentRegion.BaseAddress + BaseOffset;
                ResultRegions.Last().EndAddress = (IntPtr)Math.Min((UInt64)CurrentMask.EndAddress, (UInt64)CurrentRegion.EndAddress);
                ResultRegions.Last().SetCurrentValues(CurrentRegion.GetCurrentValues().SubArray(BaseOffset, ResultRegions.Last().RegionSize));
                ResultRegions.Last().SetPreviousValues(CurrentRegion.GetPreviousValues().SubArray(BaseOffset, ResultRegions.Last().RegionSize));
                ResultRegions.Last().SetElementType(CurrentRegion.GetElementType());
            }

            return ResultRegions.ToArray();
        }

        /// <summary>
        /// Merges continguous regions in the current list of memory regions using a fast stack based algorithm O(nlogn + n)
        /// </summary>
        protected virtual void MergeRegions()
        {
            if (SnapshotRegions == null || SnapshotRegions.Length == 0)
                return;

            // First, sort by start address
            Array.Sort(SnapshotRegions, (x, y) => ((UInt64)x.BaseAddress).CompareTo((UInt64)y.BaseAddress));

            // Create and initialize the stack with the first region
            Stack<SnapshotRegion> CombinedRegions = new Stack<SnapshotRegion>();
            CombinedRegions.Push(SnapshotRegions[0]);

            // Build the remaining regions
            for (Int32 Index = CombinedRegions.Count; Index < SnapshotRegions.Length; Index++)
            {
                SnapshotRegion Top = CombinedRegions.Peek();

                // If the interval does not overlap, put it on the top of the stack
                if ((UInt64)Top.EndAddress < (UInt64)SnapshotRegions[Index].BaseAddress)
                {
                    CombinedRegions.Push(SnapshotRegions[Index]);
                }
                // The interval overlaps; just merge it with the current top of the stack
                else if ((UInt64)Top.EndAddress <= (UInt64)SnapshotRegions[Index].EndAddress)
                {
                    Top.RegionSize = (Int32)((UInt64)SnapshotRegions[Index].EndAddress - (UInt64)Top.BaseAddress);
                }
            }

            // Replace memory regions with merged memory regions
            SnapshotRegions = CombinedRegions.ToArray();
            Array.Sort(SnapshotRegions, (x, y) => ((UInt64)x.BaseAddress).CompareTo((UInt64)y.BaseAddress));
        }

        public IEnumerator GetEnumerator()
        {
            return SnapshotRegions.GetEnumerator();
        }
    }

    /// <summary>
    /// Defines labeled data contained in a single snapshot
    /// </summary>
    class Snapshot<T> : Snapshot where T : struct
    {
        public Snapshot() : base()
        {

        }

        public Snapshot(Snapshot BaseSnapshot)
        {
            // Copy and convert the snapshot data to a labeled format
            SnapshotRegions = new SnapshotRegion<T>[BaseSnapshot.GetRegionCount()];
            for (Int32 RegionIndex = 0; RegionIndex < SnapshotRegions.Length; RegionIndex++)
                SnapshotRegions[RegionIndex] = new SnapshotRegion<T>(BaseSnapshot.GetSnapshotRegions()[RegionIndex]);

            Initialize();
        }

        public Snapshot(SnapshotRegion<T>[] SnapshotData)
        {
            this.SnapshotRegions = SnapshotData;
            Initialize();
        }

        /// <summary>
        /// Indexer to allow the retrieval of the element at the specified index
        /// </summary>
        /// <param name="Index"></param>
        /// <returns></returns>
        public new SnapshotElement<T> this[Int32 Index]
        {
            get
            {
                foreach (SnapshotRegion<T> MemoryRegion in this)
                {
                    if (Index - MemoryRegion.RegionSize >= 0)
                        Index -= MemoryRegion.RegionSize;
                    else
                        return MemoryRegion[Index];
                }
                return null;
            }
        }

        public new SnapshotRegion<T>[] GetValidRegions()
        {
            List<SnapshotRegion<T>> ValidRegions = new List<SnapshotRegion<T>>();

            foreach (SnapshotRegion<T> Region in this)
                ValidRegions.AddRange(Region.GetValidRegions());

            return ValidRegions.ToArray();
        }

        public void SetMemoryLabels(T Value)
        {
            foreach (SnapshotRegion<T> Region in this)
                Region.SetMemoryLabels(Value);
        }

        protected override void MaskDeallocatedRegions()
        {
            List<SnapshotRegion<T>> NewSnapshotRegions = SnapshotRegions.Select(x => (SnapshotRegion<T>)x).ToList();

            // Remove invalid items from collection
            foreach (SnapshotRegion<T> Region in DeallocatedRegions)
                NewSnapshotRegions.Remove(Region);

            // Get current memory regions
            Snapshot<T> Mask = new Snapshot<T>(SnapshotManager.GetInstance().SnapshotAllRegions());

            // Mask each region against the current virtual memory regions
            SnapshotRegion<T>[] MaskedRegions = MaskRegions(Mask, DeallocatedRegions.Select(x => (SnapshotRegion<T>)x).ToArray());

            // Merge split regions back with the main list
            NewSnapshotRegions.AddRange(MaskedRegions);

            // Clear invalid items
            DeallocatedRegions = new ConcurrentBag<SnapshotRegion>();

            // Store result as main snapshot array
            this.SnapshotRegions = NewSnapshotRegions.ToArray();
        }

        /// <summary>
        /// Masks the given memory regions against the memory regions of a given snapshot, keeping the common elements of the two.
        /// </summary>
        /// <param name="Mask"></param>
        public SnapshotRegion<T>[] MaskRegions(Snapshot<T> Mask, SnapshotRegion<T>[] TargetRegions)
        {
            List<SnapshotRegion<T>> ResultRegions = new List<SnapshotRegion<T>>();

            // Initialize stacks with regions and masking regions
            Queue<SnapshotRegion<T>> CandidateRegions = new Queue<SnapshotRegion<T>>();
            Queue<SnapshotRegion<T>> MaskingRegions = new Queue<SnapshotRegion<T>>();

            foreach (SnapshotRegion<T> Region in TargetRegions)
                CandidateRegions.Enqueue(Region);

            foreach (SnapshotRegion<T> MaskRegion in Mask)
                MaskingRegions.Enqueue(MaskRegion);

            if (CandidateRegions.Count == 0 || MaskingRegions.Count == 0)
                return null;

            SnapshotRegion<T> CurrentRegion;
            SnapshotRegion<T> CurrentMask = MaskingRegions.Dequeue();

            while (CandidateRegions.Count > 0)
            {
                // Grab next region
                CurrentRegion = CandidateRegions.Dequeue();

                // Grab the next mask following the current region
                while ((UInt64)CurrentMask.EndAddress < (UInt64)CurrentRegion.BaseAddress)
                    CurrentMask = MaskingRegions.Dequeue();

                // Check for mask completely removing this region
                if ((UInt64)CurrentMask.BaseAddress > (UInt64)CurrentRegion.EndAddress)
                    continue;

                // Mask completely overlaps, just use the original region
                if (CurrentMask.BaseAddress == CurrentRegion.BaseAddress && CurrentMask.EndAddress == CurrentRegion.EndAddress)
                {
                    ResultRegions.Add(CurrentRegion);
                    continue;
                }

                // Mask is within bounds; Grab the masked portion of this region
                Int32 BaseOffset = 0;
                if ((UInt64)CurrentMask.BaseAddress > (UInt64)CurrentRegion.BaseAddress)
                    BaseOffset = (Int32)((UInt64)CurrentMask.BaseAddress - (UInt64)CurrentRegion.BaseAddress);

                ResultRegions.Add(new SnapshotRegion<T>(CurrentRegion));
                ResultRegions.Last().BaseAddress = CurrentRegion.BaseAddress + BaseOffset;
                ResultRegions.Last().EndAddress = (IntPtr)Math.Min((UInt64)CurrentMask.EndAddress, (UInt64)CurrentRegion.EndAddress);
                ResultRegions.Last().SetCurrentValues(CurrentRegion.GetCurrentValues().SubArray(BaseOffset, ResultRegions.Last().RegionSize));
                ResultRegions.Last().SetPreviousValues(CurrentRegion.GetPreviousValues().SubArray(BaseOffset, ResultRegions.Last().RegionSize));
                ResultRegions.Last().SetMemoryLabels(CurrentRegion.GetMemoryLabels().SubArray(BaseOffset, ResultRegions.Last().RegionSize));
                ResultRegions.Last().SetElementType(CurrentRegion.GetElementType());
            }

            return ResultRegions.ToArray();
        }

        /// <summary>
        /// Merges labeled, non-overlapping regions in the current list of memory regions using a fast stack based algorithm O(nlogn + n)
        /// </summary>
        protected override void MergeRegions()
        {
            SnapshotRegion<T>[] SnapshotRegions = (SnapshotRegion<T>[])this.SnapshotRegions;

            if (SnapshotRegions == null || SnapshotRegions.Length == 0)
                return;

            // First, sort by start address
            Array.Sort(SnapshotRegions, (x, y) => ((UInt64)x.BaseAddress).CompareTo((UInt64)y.BaseAddress));

            // Create and initialize the stack with the first region
            Stack<SnapshotRegion<T>> CombinedRegions = new Stack<SnapshotRegion<T>>();
            CombinedRegions.Push(SnapshotRegions[0]);

            // Build the remaining regions
            for (Int32 Index = CombinedRegions.Count; Index < SnapshotRegions.Length; Index++)
            {
                SnapshotRegion<T> Top = CombinedRegions.Peek();

                // If the interval does not overlap, put it on the top of the stack
                if ((UInt64)Top.EndAddress < (UInt64)SnapshotRegions[Index].BaseAddress)
                {
                    CombinedRegions.Push(SnapshotRegions[Index]);
                }
                // The regions are adjacent; merge them
                else if ((UInt64)Top.EndAddress == (UInt64)SnapshotRegions[Index].BaseAddress)
                {
                    Top.RegionSize = (Int32)((UInt64)SnapshotRegions[Index].EndAddress - (UInt64)Top.BaseAddress);
                    Top.SetMemoryLabels(Top.GetMemoryLabels().Concat(SnapshotRegions[Index].GetMemoryLabels()));
                }
                // The regions overlap, which should not happen
                else if ((UInt64)Top.EndAddress > (UInt64)SnapshotRegions[Index].BaseAddress)
                {
                    throw new Exception("The labeled regions overlap and can not be merged.");
                }
            }

            // Replace memory regions with merged memory regions
            this.SnapshotRegions = CombinedRegions.ToArray();
            Array.Sort(this.SnapshotRegions, (x, y) => ((UInt64)x.BaseAddress).CompareTo((UInt64)y.BaseAddress));
        }

    } // End class

} // End namespace