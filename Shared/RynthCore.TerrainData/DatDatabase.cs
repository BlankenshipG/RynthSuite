// Ported ACE code with a null-returning public API that RynthAi, RynthJuice and
// RynthVision already depend on; annotating it would add warnings in every caller.
#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.TerrainData;

namespace RynthCore.Plugin.RynthAi.Raycasting
{
    /// <summary>
    /// AC .dat file reader — ported from ACEmulator's DatLoader.
    ///
    /// Key format details (from ACE source):
    ///   - Header at file offset 0x140
    ///   - Block chain: FIRST 4 bytes of each block = next block address (byte offset)
    ///                  Remaining (BlockSize - 4) bytes = data
    ///   - B-tree node (DatDirectoryHeader): 62 branches, entry count, 61 entries × 24 bytes = 1716 total
    ///   - All pointers (BTree root, chain pointers, file offsets) are byte offsets
    ///
    /// Lookups walk the on-disk B-tree by key (root → child whose key range holds
    /// the id), the way ACE's DatDirectory search does, instead of indexing every
    /// entry up front: opening client_cell_1.dat used to read all ~805k entries
    /// (1.6-1.9 s and ~55 MB in a 32-bit process). Interior nodes are kept once
    /// read and leaves go through a small LRU, so a cold lookup reads one leaf
    /// and neighbouring ids (adjacent landblocks) touch the disk once. Full
    /// enumeration is still available on demand (<see cref="EnumerateEntries"/>,
    /// used by <see cref="GetSampleIds"/>; <see cref="RecordCount"/> walks only
    /// the node headers); neither keeps anything.
    ///
    /// Thread-safety: every stream read and cache access holds one lock, so a
    /// database can be shared between threads.
    /// </summary>
    public class DatDatabase : IDisposable
    {
        private FileStream _stream;
        private readonly object _lock = new object();

        private const uint DAT_HEADER_OFFSET = 0x140;

        // B-tree geometry (from ACE DatDirectoryHeader.cs):
        //   0x3E (62) branches, 0x3D (61) max entries, each entry = 6 × uint32 = 24 bytes
        //   ObjectSize = (4 * 62) + 4 + (24 * 61) = 248 + 4 + 1464 = 1716
        private const int BTREE_BRANCH_COUNT = 0x3E; // 62
        private const int BTREE_MAX_ENTRIES = 0x3D;   // 61
        private const int BTREE_ENTRY_SIZE = 24;      // 6 × uint32
        private const int BTREE_NODE_SIZE = (4 * BTREE_BRANCH_COUNT) + 4 + (BTREE_ENTRY_SIZE * BTREE_MAX_ENTRIES); // 1716

        // Deeper than any real dat (805k entries at fan-out 62 is 4 levels);
        // stops a corrupt branch pointer from looping forever.
        private const int MaxTreeDepth = 16;

        // Parsed nodes kept for repeat lookups (~2.5 KB each). Interior nodes
        // are few (about 1 in 62: ~220 for client_cell_1.dat) and on every
        // search path, so they are all kept once read, which makes a cold
        // lookup cost one leaf read. Leaves go through a small LRU; 128 holds
        // a 7×7-landblock window many times over.
        private const int LeafCacheCapacity = 128;
        private const int MaxInteriorNodes = 4096; // safety cap; real dats stay far below

        // Header fields
        public uint FileType { get; private set; }
        public uint BlockSize { get; private set; }
        public uint FileSize { get; private set; }
        public uint DataSet { get; private set; }
        public uint BTreeRoot { get; private set; } // Byte offset

        public bool IsLoaded { get; private set; }
        public string FilePath { get; private set; }

        private int _recordCount = -1;

        /// <summary>
        /// Number of files in the dat. Counted on first use by walking the
        /// B-tree node headers (branch table + entry count, the first block of
        /// each node; the entries themselves aren't read), then cached until
        /// Close. The old eager index paid a full read of every node at Open.
        /// </summary>
        public int RecordCount
        {
            get
            {
                if (!IsLoaded) return 0;
                if (_recordCount < 0)
                    _recordCount = CountEntries(BTreeRoot, 0);
                return _recordCount;
            }
        }

        private int CountEntries(uint offset, int depth)
        {
            if (offset == 0 || depth >= MaxTreeDepth) return 0;
            byte[] head;
            lock (_lock) head = ReadDatData(offset, NodeHeaderSize);
            if (head == null || head.Length < NodeHeaderSize) return 0;
            uint entryCount = BitConverter.ToUInt32(head, BTREE_BRANCH_COUNT * 4);
            if (entryCount > BTREE_MAX_ENTRIES) return 0;
            int total = (int)entryCount;
            if (BitConverter.ToUInt32(head, 0) != 0) // interior node
                for (int i = 0; i <= entryCount; i++)
                    total += CountEntries(BitConverter.ToUInt32(head, i * 4), depth + 1);
            return total;
        }

        // Branch table + entry count: the part of a node CountEntries needs.
        private const int NodeHeaderSize = (4 * BTREE_BRANCH_COUNT) + 4; // 252

        private sealed class BTreeNode
        {
            public uint[] Branches;
            public DatBTreeEntry[] Entries; // sorted by ObjectId
            public bool IsLeaf => Branches[0] == 0;
        }

        private readonly LruCache<uint, BTreeNode> _leafCache = new LruCache<uint, BTreeNode>(LeafCacheCapacity);
        private readonly Dictionary<uint, BTreeNode> _interiorNodes = new Dictionary<uint, BTreeNode>();

        public List<string> DiagLog { get; } = new List<string>();

        public bool Open(string path)
        {
            // Re-opening must not leak the previous handle.
            Close();
            try
            {
                if (!File.Exists(path))
                {
                    Log($"File not found: {path}");
                    return false;
                }

                FilePath = path;
                // AC keeps its own handles on these files, and whichever of us
                // opens second must be compatible with the other: RynthAi opens
                // at plugin init (before acclient.exe opens its dats), RynthVision
                // and RynthJuice later. We only read, and we allow others to
                // read, write and delete-mark the file (FileShare.Delete matters
                // when we open first: AC's own open needs delete-rename semantics
                // for the patcher, and without it AC fails with "cannot access
                // the data files"). RandomAccess hint + 4 KB buffer suit the
                // scattered B-tree and block-chain reads.
                _stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    options: FileOptions.RandomAccess);

                Log($"File: {Path.GetFileName(path)}, Size: {_stream.Length:N0} bytes");

                if (!ReadHeader())
                {
                    Close();
                    return false;
                }

                // Validate the root node now so a bad file fails here, not on
                // the first lookup.
                BTreeNode root;
                lock (_lock) root = GetNodeLocked(BTreeRoot);
                if (root == null)
                {
                    Log($"Unreadable B-tree root at 0x{BTreeRoot:X8}");
                    Close();
                    return false;
                }

                IsLoaded = true;
                Log($"SUCCESS: opened (B-tree searched on demand), BlockSize={BlockSize}, Root=0x{BTreeRoot:X8}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                Close();
                return false;
            }
        }

        /// <summary>
        /// Reads the dat header from offset 0x140.
        /// Format matches ACE's DatDatabaseHeader.Unpack().
        /// </summary>
        private bool ReadHeader()
        {
            if (_stream.Length < DAT_HEADER_OFFSET + 64)
            {
                Log("File too small");
                return false;
            }

            _stream.Seek(DAT_HEADER_OFFSET, SeekOrigin.Begin);
            using (var reader = new BinaryReader(_stream, System.Text.Encoding.Default, true))
            {
                FileType = reader.ReadUInt32();
                BlockSize = reader.ReadUInt32();
                FileSize = reader.ReadUInt32();
                DataSet = reader.ReadUInt32();
                reader.ReadUInt32(); // dataSubset
                reader.ReadUInt32(); // freeHead
                reader.ReadUInt32(); // freeTail
                reader.ReadUInt32(); // freeCount
                BTreeRoot = reader.ReadUInt32();
            }

            Log($"FileType=0x{FileType:X8}, BlockSize={BlockSize}, DataSet={DataSet}, BTree=0x{BTreeRoot:X8}");

            if (BlockSize <= 4 || BTreeRoot == 0 || BTreeRoot >= (uint)_stream.Length)
            {
                Log("Invalid header values");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads data from the dat file following the block chain.
        /// Matches ACE's DatReader.ReadDat().
        ///
        /// Block format:
        ///   [4 bytes: next block address] [BlockSize - 4 bytes: data]
        ///
        /// The FIRST 4 bytes of each block are the chain pointer (next block address).
        /// If 0, this is the last block. Caller holds _lock.
        /// </summary>
        private byte[] ReadDatData(uint offset, int size)
        {
            if (_stream == null || (long)offset + BlockSize > _stream.Length || size <= 0)
                return null;

            byte[] buffer = new byte[size];
            Span<byte> addrBuf = stackalloc byte[4];

            _stream.Seek(offset, SeekOrigin.Begin);
            if (!ReadFully(addrBuf)) return null;
            uint nextAddress = BitConverter.ToUInt32(addrBuf);

            int bufferOffset = 0;
            int remaining = size;

            while (remaining > 0)
            {
                if (nextAddress == 0)
                {
                    // Last block — read remaining data directly
                    int toRead = (int)Math.Min(remaining, _stream.Length - _stream.Position);
                    if (toRead <= 0) break;
                    ReadFully(buffer.AsSpan(bufferOffset, toRead));
                    remaining = 0;
                }
                else
                {
                    // Read data portion of this block (BlockSize - 4 bytes)
                    int dataInBlock = (int)BlockSize - 4;
                    int toRead = Math.Min(dataInBlock, remaining);
                    if (!ReadFully(buffer.AsSpan(bufferOffset, toRead))) break;
                    bufferOffset += toRead;
                    remaining -= toRead;

                    if (remaining > 0)
                    {
                        // Follow chain to next block
                        if (nextAddress >= (uint)_stream.Length) break;
                        _stream.Seek(nextAddress, SeekOrigin.Begin);

                        // Read next block's chain pointer
                        if (!ReadFully(addrBuf)) break;
                        nextAddress = BitConverter.ToUInt32(addrBuf);
                    }
                }
            }

            return buffer;
        }

        private bool ReadFully(Span<byte> dest)
        {
            while (dest.Length > 0)
            {
                int n = _stream.Read(dest);
                if (n <= 0) return false;
                dest = dest.Slice(n);
            }
            return true;
        }

        /// <summary>Parses the B-tree node at <paramref name="offset"/>. Caller holds _lock.</summary>
        private BTreeNode ReadNodeLocked(uint offset)
        {
            if (_stream == null || offset == 0 || offset >= (uint)_stream.Length)
                return null;

            byte[] nodeData = ReadDatData(offset, BTREE_NODE_SIZE);
            if (nodeData == null || nodeData.Length < BTREE_NODE_SIZE)
                return null;

            int countOffset = BTREE_BRANCH_COUNT * 4; // 248
            uint entryCount = BitConverter.ToUInt32(nodeData, countOffset);
            if (entryCount > BTREE_MAX_ENTRIES)
                return null; // Invalid node

            var branches = new uint[BTREE_BRANCH_COUNT];
            for (int i = 0; i < BTREE_BRANCH_COUNT; i++)
                branches[i] = BitConverter.ToUInt32(nodeData, i * 4);

            int entriesOffset = countOffset + 4; // 252
            var entries = new DatBTreeEntry[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                int eOff = entriesOffset + i * BTREE_ENTRY_SIZE;
                entries[i] = new DatBTreeEntry
                {
                    BitFlags = BitConverter.ToUInt32(nodeData, eOff),
                    ObjectId = BitConverter.ToUInt32(nodeData, eOff + 4),
                    FileOffset = BitConverter.ToUInt32(nodeData, eOff + 8),
                    FileSize = BitConverter.ToUInt32(nodeData, eOff + 12)
                };
            }

            return new BTreeNode { Branches = branches, Entries = entries };
        }

        /// <summary>Cached node read. Caller holds _lock.</summary>
        private BTreeNode GetNodeLocked(uint offset)
        {
            if (_interiorNodes.TryGetValue(offset, out var node)) return node;
            if (_leafCache.TryGet(offset, out node)) return node;
            node = ReadNodeLocked(offset);
            if (node == null) return null;
            if (!node.IsLeaf && _interiorNodes.Count < MaxInteriorNodes)
                _interiorNodes[offset] = node;
            else
                _leafCache.Set(offset, node);
            return node;
        }

        // Index of the first entry whose ObjectId is >= id (entries.Length if none).
        private static int LowerBound(DatBTreeEntry[] entries, uint id)
        {
            int lo = 0, hi = entries.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (entries[mid].ObjectId < id) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// Finds a file by ID: descends the B-tree from the root, picking at each
        /// node the branch between the two keys that bracket the id.
        /// </summary>
        public DatBTreeEntry FindFile(uint fileId)
        {
            if (!IsLoaded || fileId == 0) return null;

            lock (_lock)
            {
                uint offset = BTreeRoot;
                for (int depth = 0; depth < MaxTreeDepth && offset != 0; depth++)
                {
                    BTreeNode node = GetNodeLocked(offset);
                    if (node == null) return null;

                    DatBTreeEntry[] entries = node.Entries;
                    int i = LowerBound(entries, fileId);
                    if (i < entries.Length && entries[i].ObjectId == fileId)
                        return entries[i];
                    if (node.IsLeaf || i >= BTREE_BRANCH_COUNT)
                        return null;
                    offset = node.Branches[i];
                }
            }
            return null;
        }

        /// <summary>
        /// Reads file data for a given entry.
        /// </summary>
        public byte[] ReadFileData(DatBTreeEntry entry)
        {
            if (!IsLoaded || entry == null || entry.FileSize == 0)
                return null;

            lock (_lock)
            {
                return ReadDatData(entry.FileOffset, (int)entry.FileSize);
            }
        }

        public byte[] GetFileData(uint fileId)
        {
            var entry = FindFile(fileId);
            return entry != null ? ReadFileData(entry) : null;
        }

        /// <summary>
        /// Returns all interior cell IDs (0x0100–0xFFFD) for a given landblock,
        /// ascending. A B-tree range search: only the nodes whose key range
        /// overlaps the landblock's are read.
        /// </summary>
        public List<uint> GetLandblockCellIds(uint landblockKey)
        {
            var cellIds = new List<uint>();
            if (!IsLoaded) return cellIds;

            uint prefix = landblockKey << 16;
            uint lo = prefix | 0x0100;
            uint hi = prefix | 0xFFFD;
            lock (_lock)
            {
                CollectRangeLocked(BTreeRoot, lo, hi, cellIds, 0);
            }
            return cellIds;
        }

        private void CollectRangeLocked(uint offset, uint lo, uint hi, List<uint> output, int depth)
        {
            if (offset == 0 || depth >= MaxTreeDepth) return;
            BTreeNode node = GetNodeLocked(offset);
            if (node == null) return;

            DatBTreeEntry[] entries = node.Entries;
            int count = entries.Length;
            // Branch i holds the ids between entries[i-1] and entries[i].
            int start = LowerBound(entries, lo);
            for (int i = start; i <= count; i++)
            {
                if (!node.IsLeaf && i < BTREE_BRANCH_COUNT)
                    CollectRangeLocked(node.Branches[i], lo, hi, output, depth + 1);
                if (i == count) break;
                uint id = entries[i].ObjectId;
                if (id > hi) break;
                output.Add(id);
            }
        }

        /// <summary>
        /// Every entry in the dat, in ascending id order, read from the B-tree
        /// on demand (nothing is kept afterwards). Walking client_cell_1.dat
        /// reads every node, so use it for diagnostics and counts, not lookups.
        /// </summary>
        public IEnumerable<DatBTreeEntry> EnumerateEntries()
        {
            if (!IsLoaded) yield break;

            // Explicit stack instead of recursion so the lock is only held per
            // node read, never across a yield. A frame is "visit branch I, then
            // entry I, then move on to I+1".
            BTreeNode root = ReadNodeForWalk(BTreeRoot);
            if (root == null) yield break;
            var stack = new Stack<WalkFrame>();
            stack.Push(new WalkFrame(root, 0, false));

            while (stack.Count > 0)
            {
                WalkFrame f = stack.Pop();
                if (!f.BranchDone)
                {
                    stack.Push(new WalkFrame(f.Node, f.Index, true));
                    if (!f.Node.IsLeaf && f.Index < BTREE_BRANCH_COUNT && stack.Count <= MaxTreeDepth)
                    {
                        BTreeNode child = ReadNodeForWalk(f.Node.Branches[f.Index]);
                        if (child != null) stack.Push(new WalkFrame(child, 0, false));
                    }
                    continue;
                }
                if (f.Index < f.Node.Entries.Length)
                {
                    yield return f.Node.Entries[f.Index];
                    stack.Push(new WalkFrame(f.Node, f.Index + 1, false));
                }
            }
        }

        private readonly struct WalkFrame
        {
            public readonly BTreeNode Node;
            public readonly int Index;
            public readonly bool BranchDone;
            public WalkFrame(BTreeNode node, int index, bool branchDone) { Node = node; Index = index; BranchDone = branchDone; }
        }

        private BTreeNode ReadNodeForWalk(uint offset)
        {
            lock (_lock)
            {
                // Don't churn the lookup cache with a full walk: use a cached
                // node if present, otherwise read without caching.
                if (_interiorNodes.TryGetValue(offset, out var cached)) return cached;
                if (_leafCache.TryGet(offset, out cached)) return cached;
                return ReadNodeLocked(offset);
            }
        }

        /// <summary>
        /// Returns the lowest file IDs in the dat (in-order walk, stops early),
        /// for diagnostics.
        /// </summary>
        public List<uint> GetSampleIds(int maxCount = 20)
        {
            var ids = new List<uint>();
            if (maxCount <= 0) return ids;
            foreach (var e in EnumerateEntries())
            {
                ids.Add(e.ObjectId);
                if (ids.Count >= maxCount) break;
            }
            return ids;
        }

        public void Close()
        {
            lock (_lock)
            {
                _stream?.Dispose();
                _stream = null;
                IsLoaded = false;
                _recordCount = -1;
                _leafCache.Clear();
                _interiorNodes.Clear();
            }
        }

        public void Dispose() { Close(); }

        private void Log(string msg)
        {
            string line = $"[DatDB] {msg}";
            System.Diagnostics.Debug.WriteLine(line);
            if (DiagLog.Count < 100) DiagLog.Add(line);
        }
    }

    public class DatBTreeEntry
    {
        public uint BitFlags;
        public uint ObjectId;
        public uint FileOffset;  // Byte offset into file
        public uint FileSize;
    }
}
