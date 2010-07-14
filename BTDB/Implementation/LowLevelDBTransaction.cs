﻿using System;
using System.Diagnostics;

namespace BTDB
{
    internal class LowLevelDBTransaction : ILowLevelDBTransaction
    {
        readonly LowLevelDB _owner;

        // if this is null then this transaction is writing kind
        LowLevelDB.ReadTrLink _readLink;
        Sector _currentKeySector;
        int _currentKeyIndexInLeaf;
        long _currentKeyIndex;
        byte[] _prefix;
        int _prefixOfs;
        int _prefixLen;
        long _prefixKeyStart;
        long _prefixKeyCount;


        internal LowLevelDBTransaction(LowLevelDB owner, LowLevelDB.ReadTrLink readLink)
        {
            _owner = owner;
            _readLink = readLink;
            _currentKeySector = null;
            _currentKeyIndexInLeaf = -1;
            _prefixLen = 0;
            _prefixKeyStart = 0;
            _prefixKeyCount = (long) _readLink.KeyValuePairCount;
        }

        public void Dispose()
        {
            if (_readLink != null)
            {
                _owner.DisposeReadTransaction(_readLink);
            }
            else
            {
                _owner.DisposeWriteTransaction();
            }
        }

        private void UpgradeToWriteTransaction()
        {
            if (IsWriteTransaction()) return;
            _owner.UpgradeTransactionToWriteOne(this, _readLink);
            _readLink = null;
        }

        public void SetKeyPrefix(byte[] prefix, int prefixOfs, int prefixLen)
        {
            _prefix = prefix;
            _prefixOfs = prefixOfs;
            _prefixLen = prefixLen;
            if (prefixLen==0)
            {
                _prefixKeyStart = 0;
                _prefixKeyCount = (long) (IsWriteTransaction() ? _owner.NewState.KeyValuePairCount : _readLink.KeyValuePairCount);
            }
            else
            {
                _prefixKeyStart = -1;
                _prefixKeyCount = -1;
            }
        }

        public void InvalidateCurrentKey()
        {
            _currentKeyIndexInLeaf = -1;
            _currentKeySector = null;
            _currentKeyIndex = -1;
        }

        public bool FindFirstKey()
        {
            throw new NotImplementedException();
        }

        public bool FindLastKey()
        {
            throw new NotImplementedException();
        }

        public bool FindPreviousKey()
        {
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            if (_currentKeyIndexInLeaf > 0)
            {
                _currentKeyIndexInLeaf--;
                return true;
            }
            var sector = _currentKeySector;
            while (sector.Parent != null)
            {
                var iter = new BTreeParentIterator(sector.Parent.Data);
                var childByPos = iter.FindChildByPos(sector.Position);
                if (childByPos==0)
                {
                    sector = sector.Parent;
                    continue;
                }
                var childSectorPtr = iter.GetChildSectorPtr(childByPos-1);
                while (true)
                {
                    sector = LoadBTreeSector(childSectorPtr, sector);
                    if (sector.Type == SectorType.BTreeChild)
                    {
                        _currentKeySector = sector;
                        _currentKeyIndexInLeaf = sector.Data[0] - 1;
                        return true;
                    }
                    iter = new BTreeParentIterator(sector.Data);
                    childSectorPtr = iter.GetChildSectorPtr(iter.Count);
                }
            }
            return false;
        }

        public bool FindNextKey()
        {
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            if (_currentKeyIndexInLeaf+1 < _currentKeySector.Data[0])
            {
                _currentKeyIndexInLeaf++;
                return true;
            }
            var sector = _currentKeySector;
            while (sector.Parent != null)
            {
                var iter = new BTreeParentIterator(sector.Parent.Data);
                var childByPos = iter.FindChildByPos(sector.Position);
                if (childByPos == iter.Count)
                {
                    sector = sector.Parent;
                    continue;
                }
                var childSectorPtr = iter.GetChildSectorPtr(childByPos + 1);
                while (true)
                {
                    sector = LoadBTreeSector(childSectorPtr, sector);
                    if (sector.Type == SectorType.BTreeChild)
                    {
                        _currentKeySector = sector;
                        _currentKeyIndexInLeaf = 0;
                        return true;
                    }
                    iter = new BTreeParentIterator(sector.Data);
                    childSectorPtr = iter.GetChildSectorPtr(0);
                }
            }
            return false;
        }

        public FindKeyResult FindKey(byte[] keyBuf, int keyOfs, int keyLen, FindKeyStrategy strategy)
        {
            if (keyLen < 0) throw new ArgumentOutOfRangeException("keyLen");
            if (strategy == FindKeyStrategy.Create) UpgradeToWriteTransaction();
            var rootBTree = GetRootBTreeSectorPtr();
            if (rootBTree.Ptr == 0)
            {
                return FindKeyInEmptyBTree(keyBuf, keyOfs, keyLen, strategy);
            }
            Sector sector;
            Sector parentOfSector = null;
            while (true)
            {
                sector = LoadBTreeSector(rootBTree, parentOfSector);
                if (sector.Type == SectorType.BTreeChild) break;
                var iterParent = new BTreeParentIterator(sector.Data);
                int bindexParent = iterParent.BinarySearch(keyBuf, keyOfs, keyLen, sector, SectorDataCompare);
                rootBTree = iterParent.GetChildSectorPtr((bindexParent + 1) / 2);
                parentOfSector = sector;
            }
            var iter = new BTreeChildIterator(sector.Data);
            int bindex = iter.BinarySearch(keyBuf, keyOfs, keyLen, sector, SectorDataCompare);
            _currentKeySector = sector;
            _currentKeyIndexInLeaf = bindex / 2;
            if ((bindex & 1) != 0)
            {
                return FindKeyResult.FoundExact;
            }
            if (strategy != FindKeyStrategy.Create)
            {
                return FindKeyNoncreateStrategy(strategy, iter);
            }
            int additionalLengthNeeded = BTreeChildIterator.CalcEntrySize(keyLen);
            if (iter.TotalLength + additionalLengthNeeded <= 4096 && iter.Count < 126)
            {
                sector = _owner.ResizeSectorWithUpdatePosition(sector, iter.TotalLength + additionalLengthNeeded, sector.Parent);
                sector.Data[0] = (byte)(iter.Count + 1);
                int insertOfs = iter.OffsetOfIndex(_currentKeyIndexInLeaf);
                Array.Copy(iter.Data, 1, sector.Data, 1, insertOfs - 1);
                Array.Copy(iter.Data, insertOfs, sector.Data, insertOfs + additionalLengthNeeded, iter.TotalLength - insertOfs);
                SetBTreeChildKeyData(sector, keyBuf, keyOfs, keyLen, insertOfs);
                IncrementChildCountInBTreeParents(sector);
            }
            else
            {
                int middleoffset = (iter.TotalLength + additionalLengthNeeded) / 2;
                iter.MoveFirst();
                bool beforeNew = true;
                int splitIndex = 0;
                int currentPos = 1;
                while (currentPos < middleoffset)
                {
                    if (beforeNew && splitIndex == _currentKeyIndexInLeaf)
                    {
                        beforeNew = false;
                        currentPos += additionalLengthNeeded;
                    }
                    else
                    {
                        currentPos += iter.CurrentEntrySize;
                        splitIndex++;
                        iter.MoveNext();
                    }
                }
                Sector rightSector = _owner.NewSector();
                rightSector.Type = SectorType.BTreeChild;
                rightSector.SetLengthWithRound(1 + iter.TotalLength + additionalLengthNeeded - currentPos);
                rightSector.Data[0] = (byte)(iter.Count - splitIndex + (beforeNew ? 1 : 0));
                Sector leftSector = _owner.ResizeSectorWithUpdatePosition(sector, currentPos, sector.Parent);
                leftSector.Data[0] = (byte)(splitIndex + (beforeNew ? 0 : 1));
                int newItemPos = iter.OffsetOfIndex(_currentKeyIndexInLeaf);
                if (beforeNew)
                {
                    Array.Copy(iter.Data, 1, leftSector.Data, 1, currentPos - 1);
                    Array.Copy(iter.Data, currentPos, rightSector.Data, 1, newItemPos - currentPos);
                    int rightPos = 1 + newItemPos - currentPos;
                    SetBTreeChildKeyData(rightSector, keyBuf, keyOfs, keyLen, rightPos);
                    rightPos += additionalLengthNeeded;
                    Array.Copy(iter.Data, newItemPos, rightSector.Data, rightPos, iter.TotalLength - newItemPos);
                    _currentKeySector = rightSector;
                    _currentKeyIndexInLeaf -= splitIndex;
                }
                else
                {
                    Array.Copy(iter.Data, 1, leftSector.Data, 1, newItemPos - 1);
                    int leftPos = newItemPos;
                    SetBTreeChildKeyData(leftSector, keyBuf, keyOfs, keyLen, leftPos);
                    leftPos += additionalLengthNeeded;
                    Array.Copy(iter.Data, newItemPos, leftSector.Data, leftPos, currentPos - leftPos);
                    Array.Copy(iter.Data, currentPos - additionalLengthNeeded, rightSector.Data, 1,
                               iter.TotalLength + additionalLengthNeeded - currentPos);
                    _currentKeySector = leftSector;
                }
                _owner.PublishSector(rightSector);
                if (leftSector.Parent == null)
                {
                    CreateBTreeParentFromTwoLeafs(rightSector, leftSector);
                }
                else
                {
                    iter = new BTreeChildIterator(rightSector.Data);
                    int keyLenInSector = iter.KeyLenInline + (iter.HasKeySectorPtr ? LowLevelDB.PtrDownSize : 0);
                    AddToBTreeParent(leftSector, rightSector, iter.Data, iter.KeyLen, iter.KeyOffset, keyLenInSector);
                }
            }
            _owner.NewState.KeyValuePairCount++;
            if (_prefixKeyCount != -1) _prefixKeyCount++;
            return FindKeyResult.Created;
        }

        public long GetKeyValueCount()
        {
            if (_prefixKeyCount != -1) return _prefixKeyCount;
            throw new NotImplementedException();
        }

        public long GetKeyIndex()
        {
            return _currentKeyIndex;
        }

        Sector LoadBTreeSector(SectorPtr sectorPtr, Sector parent)
        {
            Sector sector = _owner.TryGetSector(sectorPtr.Ptr);
            if (sector == null)
            {
                sector = _owner.ReadSector(sectorPtr, IsWriteTransaction());
                sector.Type = (sector.Data[0] & 0x80) != 0 ? SectorType.BTreeParent : SectorType.BTreeChild;
                sector.Parent = parent;
            }
            return sector;
        }

        void AddToBTreeParent(Sector leftSector, Sector rightSector, byte[] middleKeyData, int middleKeyLen, int middleKeyOfs, int middleKeyLenInSector)
        {
            var parentSector = leftSector.Parent;
            var iter = new BTreeParentIterator(parentSector.Data);
            int additionalLengthNeeded = BTreeParentIterator.CalcEntrySize(middleKeyLen);
            int leftIndexInParent = iter.FindChildByPos(leftSector.Position);
            byte[] mergedData;
            if (iter.TotalLength + additionalLengthNeeded <= 4096 && iter.Count < 126)
            {
                parentSector = _owner.ResizeSectorWithUpdatePosition(parentSector,
                                                                     iter.TotalLength +
                                                                     additionalLengthNeeded,
                                                                     parentSector.Parent);
                mergedData = parentSector.Data;
            }
            else
            {
                mergedData = new byte[iter.TotalLength + additionalLengthNeeded];
            }
            mergedData[0] = (byte)(128 + iter.Count + 1);
            int splitOfs = iter.OffsetOfIndex(leftIndexInParent);
            int ofs = splitOfs + additionalLengthNeeded;
            Array.Copy(iter.Data, splitOfs, mergedData, ofs, iter.TotalLength - splitOfs);
            ofs = splitOfs;
            Array.Copy(iter.Data, 1, mergedData, 1, ofs - 1 - 8);
            PackUnpack.PackUInt64(mergedData, ofs - 8, CalcKeysInSector(leftSector));
            PackUnpack.PackInt32(mergedData, ofs, middleKeyLen);
            ofs += 4;
            Array.Copy(middleKeyData, middleKeyOfs, mergedData, ofs, middleKeyLenInSector);
            ofs += middleKeyLenInSector;
            SectorPtr.Pack(mergedData, ofs, rightSector.ToPtrWithLen());
            ofs += LowLevelDB.PtrDownSize;
            PackUnpack.PackUInt64(mergedData, ofs, CalcKeysInSector(rightSector));
            if (mergedData == parentSector.Data)
            {
                leftSector.Parent = parentSector;
                rightSector.Parent = parentSector;
                IncrementChildCountInBTreeParents(parentSector);
            }
            else
            {
                iter = new BTreeParentIterator(mergedData);
                int middleoffset = mergedData.Length / 2;
                iter.MoveFirst();
                int splitIndex = 0;
                int currentPos = BTreeParentIterator.HeaderSize;
                while (currentPos < middleoffset)
                {
                    currentPos += iter.CurrentEntrySize;
                    splitIndex++;
                    iter.MoveNext();
                }
                Sector rightParentSector = _owner.NewSector();
                rightParentSector.Type = SectorType.BTreeParent;
                rightParentSector.SetLengthWithRound(1 + mergedData.Length - iter.ChildSectorPtrOffset);
                rightParentSector.Data[0] = (byte)(128 + iter.Count - splitIndex - 1);
                Sector leftParentSector = _owner.ResizeSectorWithUpdatePosition(parentSector, currentPos, parentSector.Parent);
                leftParentSector.Data[0] = (byte)(128 + splitIndex);
                leftSector.Parent = leftParentSector;
                rightSector.Parent = leftParentSector;
                Array.Copy(mergedData, 1, leftParentSector.Data, 1, currentPos - 1);
                Array.Copy(mergedData, iter.ChildSectorPtrOffset, rightParentSector.Data, 1, mergedData.Length - iter.ChildSectorPtrOffset);
                _owner.PublishSector(rightParentSector);
                FixChildrenParentPointers(rightParentSector);
                int keyLenInSector = iter.KeyLenInline + (iter.HasKeySectorPtr ? LowLevelDB.PtrDownSize : 0);
                if (leftParentSector.Parent == null)
                {
                    CreateBTreeParentFromTwoParents(rightParentSector, leftParentSector, iter.Data, iter.KeyLen, iter.KeyOffset, keyLenInSector);
                }
                else
                {
                    AddToBTreeParent(leftParentSector, rightParentSector, iter.Data, iter.KeyLen, iter.KeyOffset, keyLenInSector);
                }
            }
        }

        void FixChildrenParentPointers(Sector parent)
        {
            Debug.Assert(parent.Type==SectorType.BTreeParent);
            var iter = new BTreeParentIterator(parent.Data);
            for (int i = 0; i <= iter.Count;i++ )
            {
                var childSectorPtr = iter.GetChildSectorPtr(i);
                var sector = _owner.TryGetSector(childSectorPtr.Ptr);
                if (sector!=null && sector.InTransaction)
                {
                    sector.Parent = parent;
                }
            }
        }

        void CreateBTreeParentFromTwoParents(Sector rightSector, Sector leftSector, byte[] middleKeyData, int middleKeyLen, int middleKeyOfs, int middleKeyLenInSector)
        {
            Sector parentSector = _owner.NewSector();
            parentSector.Type = SectorType.BTreeParent;
            parentSector.SetLengthWithRound(BTreeParentIterator.HeaderSize + BTreeParentIterator.CalcEntrySize(middleKeyLen));
            parentSector.Data[0] = 128 + 1;
            int ofs = 1;
            SectorPtr.Pack(parentSector.Data, ofs, leftSector.ToPtrWithLen());
            ofs += LowLevelDB.PtrDownSize;
            PackUnpack.PackUInt64(parentSector.Data, ofs, CalcKeysInSector(leftSector));
            ofs += 8;
            PackUnpack.PackUInt32(parentSector.Data, ofs, (uint)middleKeyLen);
            ofs += 4;
            Array.Copy(middleKeyData, middleKeyOfs, parentSector.Data, ofs, middleKeyLenInSector);
            ofs += middleKeyLenInSector;
            SectorPtr.Pack(parentSector.Data, ofs, rightSector.ToPtrWithLen());
            ofs += LowLevelDB.PtrDownSize;
            PackUnpack.PackUInt64(parentSector.Data, ofs, CalcKeysInSector(rightSector));
            _owner.NewState.RootBTree.Ptr = parentSector.Position;
            _owner.NewState.RootBTreeLevels++;
            leftSector.Parent = parentSector;
            rightSector.Parent = parentSector;
            _owner.PublishSector(parentSector);
        }

        static ulong CalcKeysInSector(Sector sector)
        {
            if (sector.Type == SectorType.BTreeChild)
            {
                return sector.Data[0];
            }
            Debug.Assert(sector.Type == SectorType.BTreeParent);
            var iter = new BTreeParentIterator(sector.Data);
            var res = (ulong)iter.FirstChildKeyCount;
            do
            {
                res += (ulong)iter.ChildKeyCount;
            } while (iter.MoveNext());
            return res;
        }

        static void IncrementChildCountInBTreeParents(Sector sector)
        {
            while (sector.Parent != null)
            {
                Sector parentSector = sector.Parent;
                Debug.Assert(parentSector.Dirty);
                BTreeParentIterator.ModifyChildCount(parentSector.Data, sector.Position, 1);
                sector = parentSector;
            }
        }

        FindKeyResult FindKeyInEmptyBTree(byte[] keyBuf, int keyOfs, int keyLen, FindKeyStrategy strategy)
        {
            switch (strategy)
            {
                case FindKeyStrategy.Create:
                    Sector newRootBTreeSector = CreateBTreeChildWith1Key(keyBuf, keyOfs, keyLen);
                    _owner.NewState.RootBTree.Ptr = newRootBTreeSector.Position;
                    _owner.NewState.RootBTreeLevels = 1;
                    _owner.NewState.KeyValuePairCount = 1;
                    _owner.PublishSector(newRootBTreeSector);
                    _currentKeySector = newRootBTreeSector;
                    _currentKeyIndexInLeaf = 0;
                    return FindKeyResult.Created;
                case FindKeyStrategy.ExactMatch:
                case FindKeyStrategy.PreferPrevious:
                case FindKeyStrategy.PreferNext:
                case FindKeyStrategy.OnlyPrevious:
                case FindKeyStrategy.OnlyNext:
                    return FindKeyNotFound();
                default:
                    throw new ArgumentOutOfRangeException("strategy");
            }
        }

        SectorPtr GetRootBTreeSectorPtr()
        {
            return IsWriteTransaction() ? _owner.NewState.RootBTree : _readLink.RootBTree;
        }

        void CreateBTreeParentFromTwoLeafs(Sector rightSector, Sector leftSector)
        {
            Sector parentSector = _owner.NewSector();
            parentSector.Type = SectorType.BTreeParent;
            var iter = new BTreeChildIterator(rightSector.Data);
            int keyLenInSector = iter.KeyLenInline + (iter.HasKeySectorPtr ? LowLevelDB.PtrDownSize : 0);
            parentSector.SetLengthWithRound(BTreeParentIterator.HeaderSize + BTreeParentIterator.CalcEntrySize(iter.KeyLen));
            parentSector.Data[0] = 128 + 1;
            int ofs = 1;
            SectorPtr.Pack(parentSector.Data, ofs, leftSector.ToPtrWithLen());
            ofs += LowLevelDB.PtrDownSize;
            PackUnpack.PackUInt64(parentSector.Data, ofs, leftSector.Data[0]);
            ofs += 8;
            PackUnpack.PackUInt32(parentSector.Data, ofs, (uint)iter.KeyLen);
            ofs += 4;
            Array.Copy(iter.Data, iter.KeyOffset, parentSector.Data, ofs, keyLenInSector);
            ofs += keyLenInSector;
            SectorPtr.Pack(parentSector.Data, ofs, rightSector.ToPtrWithLen());
            ofs += LowLevelDB.PtrDownSize;
            PackUnpack.PackUInt64(parentSector.Data, ofs, rightSector.Data[0]);
            _owner.NewState.RootBTree.Ptr = parentSector.Position;
            _owner.NewState.RootBTreeLevels++;
            leftSector.Parent = parentSector;
            rightSector.Parent = parentSector;
            _owner.PublishSector(parentSector);
        }

        FindKeyResult FindKeyNoncreateStrategy(FindKeyStrategy strategy, BTreeChildIterator iter)
        {
            switch (strategy)
            {
                case FindKeyStrategy.ExactMatch:
                    return FindKeyNotFound();
                case FindKeyStrategy.OnlyNext:
                    if (_currentKeyIndexInLeaf < iter.Count)
                    {
                        return FindKeyResult.FoundNext;
                    }
                    _currentKeyIndexInLeaf--;
                    return FindNextKey() ? FindKeyResult.FoundNext : FindKeyNotFound();
                case FindKeyStrategy.PreferNext:
                    if (_currentKeyIndexInLeaf < iter.Count)
                    {
                        return FindKeyResult.FoundNext;
                    }
                    _currentKeyIndexInLeaf--;
                    return FindNextKey() ? FindKeyResult.FoundNext : FindKeyResult.FoundPrevious;
                case FindKeyStrategy.OnlyPrevious:
                    if (_currentKeyIndexInLeaf > 0)
                    {
                        _currentKeyIndexInLeaf--;
                        return FindKeyResult.FoundPrevious;
                    }
                    return FindPreviousKey() ? FindKeyResult.FoundPrevious : FindKeyNotFound();
                case FindKeyStrategy.PreferPrevious:
                    if (_currentKeyIndexInLeaf > 0)
                    {
                        _currentKeyIndexInLeaf--;
                        return FindKeyResult.FoundPrevious;
                    }
                    return FindPreviousKey() ? FindKeyResult.FoundPrevious : FindKeyResult.FoundNext;
                default:
                    throw new ArgumentOutOfRangeException("strategy");
            }
        }

        FindKeyResult FindKeyNotFound()
        {
            _currentKeySector = null;
            _currentKeyIndexInLeaf = -1;
            _currentKeyIndex = -1;
            return FindKeyResult.NotFound;
        }

        int SectorDataCompare(byte[] buf, int ofs, int len, SectorPtr sectorPtr, int dataLen, Sector parent)
        {
            Sector sector = _owner.TryGetSector(sectorPtr.Ptr);
            if (sector == null)
            {
                sector = _owner.ReadSector(sectorPtr, IsWriteTransaction());
                sector.Type = dataLen > sector.Length ? SectorType.DataParent : SectorType.DataChild;
                sector.Parent = parent;
            }
            if (sector.Type == SectorType.DataChild)
            {
                return BitArrayManipulation.CompareByteArray(buf,
                                                             ofs,
                                                             dataLen > sector.Length && len > sector.Length ? sector.Length : len,
                                                             sector.Data,
                                                             0,
                                                             sector.Length);
            }
            int downPtrCount;
            var bytesInDownLevel = (int)GetBytesInDownLevel(dataLen, out downPtrCount);
            int i;
            SectorPtr downSectorPtr;
            for (i = 0; i < downPtrCount - 1; i++)
            {
                downSectorPtr = SectorPtr.Unpack(sector.Data, i * LowLevelDB.PtrDownSize);
                int res = SectorDataCompare(buf,
                                            ofs,
                                            Math.Min(len, bytesInDownLevel),
                                            downSectorPtr,
                                            Math.Min(dataLen, bytesInDownLevel),
                                            sector);
                if (res != 0) return res;
                ofs += bytesInDownLevel;
                len -= bytesInDownLevel;
                dataLen -= bytesInDownLevel;
            }
            downSectorPtr = SectorPtr.Unpack(sector.Data, i * LowLevelDB.PtrDownSize);
            return SectorDataCompare(buf, ofs, len, downSectorPtr, dataLen, sector);
        }

        Sector CreateBTreeChildWith1Key(byte[] keyBuf, int keyOfs, int keyLen)
        {
            var newRootBTreeSector = _owner.NewSector();
            newRootBTreeSector.Type = SectorType.BTreeChild;
            newRootBTreeSector.SetLengthWithRound(1 + BTreeChildIterator.CalcEntrySize(keyLen));
            newRootBTreeSector.Data[0] = 1;
            SetBTreeChildKeyData(newRootBTreeSector, keyBuf, keyOfs, keyLen, 1);
            return newRootBTreeSector;
        }

        void SetBTreeChildKeyData(Sector inSector, byte[] keyBuf, int keyOfs, int keyLen, int sectorDataOfs)
        {
            byte[] sectorData = inSector.Data;
            int keyLenInline = BTreeChildIterator.CalcKeyLenInline(keyLen);
            PackUnpack.PackUInt32(sectorData, sectorDataOfs, (uint)keyLen);
            Array.Copy(keyBuf, keyOfs, sectorData, sectorDataOfs + 4 + 8, keyLenInline);
            if (keyLen > BTreeChildIterator.MaxKeyLenInline)
            {
                SectorPtr keySecPtr = CreateContentSector(keyBuf, keyOfs + keyLenInline, keyLen - keyLenInline, inSector);
                SectorPtr.Pack(sectorData, sectorDataOfs + 4 + 8 + keyLenInline, keySecPtr);
            }
        }

        SectorPtr CreateContentSector(byte[] buf, int ofs, int len, Sector parent)
        {
            if (len <= LowLevelDB.MaxLeafDataSectorSize)
            {
                var newLeafSector = _owner.NewSector();
                newLeafSector.Type = SectorType.DataChild;
                newLeafSector.SetLengthWithRound(len);
                newLeafSector.Parent = parent;
                if (buf != null) Array.Copy(buf, ofs, newLeafSector.Data, 0, len);
                _owner.PublishSector(newLeafSector);
                return newLeafSector.ToPtrWithLen();
            }
            int downPtrCount;
            var bytesInDownLevel = (int)GetBytesInDownLevel(len, out downPtrCount);
            var newSector = _owner.NewSector();
            newSector.Type = SectorType.DataParent;
            newSector.SetLengthWithRound(downPtrCount * LowLevelDB.PtrDownSize);
            newSector.Parent = parent;
            for (int i = 0; i < downPtrCount; i++)
            {
                SectorPtr sectorPtr = CreateContentSector(buf, ofs, Math.Min(len, bytesInDownLevel), newSector);
                SectorPtr.Pack(newSector.Data, i * LowLevelDB.PtrDownSize, sectorPtr);
                ofs += bytesInDownLevel;
                len -= bytesInDownLevel;
            }
            _owner.PublishSector(newSector);
            return newSector.ToPtrWithLen();
        }

        SectorPtr CreateContentSector(long len, Sector parent)
        {
            if (len <= LowLevelDB.MaxLeafDataSectorSize)
            {
                var newLeafSector = _owner.NewSector();
                newLeafSector.Type = SectorType.DataChild;
                newLeafSector.SetLengthWithRound((int)len);
                newLeafSector.Parent = parent;
                _owner.PublishSector(newLeafSector);
                return newLeafSector.ToPtrWithLen();
            }
            int downPtrCount;
            long bytesInDownLevel = GetBytesInDownLevel(len, out downPtrCount);
            var newSector = _owner.NewSector();
            newSector.Type = SectorType.DataParent;
            newSector.SetLengthWithRound(downPtrCount * LowLevelDB.PtrDownSize);
            newSector.Parent = parent;
            for (int i = 0; i < downPtrCount; i++)
            {
                SectorPtr sectorPtr = CreateContentSector(Math.Min(len, bytesInDownLevel), newSector);
                SectorPtr.Pack(newSector.Data, i * LowLevelDB.PtrDownSize, sectorPtr);
                len -= bytesInDownLevel;
            }
            _owner.PublishSector(newSector);
            return newSector.ToPtrWithLen();
        }

        void DeleteContentSector(SectorPtr sectorPtr, long len, Sector parent)
        {
            Sector sector = _owner.TryGetSector(sectorPtr.Ptr);
            if (len <= LowLevelDB.MaxLeafDataSectorSize)
            {
                if (sector == null)
                {
                    sector = _owner.ReadSector(sectorPtr, true);
                    sector.Type = SectorType.DataChild;
                    sector.Parent = parent;
                }
                _owner.DeallocateSector(sector);
                return;
            }
            int downPtrCount;
            long bytesInDownLevel = GetBytesInDownLevel(len, out downPtrCount);
            if (sector == null)
            {
                sector = _owner.ReadSector(sectorPtr, true);
                sector.Type = SectorType.DataParent;
                sector.Parent = parent;
            }
            for (int i = 0; i < downPtrCount; i++)
            {
                var downSectorPtr = SectorPtr.Unpack(sector.Data, i * LowLevelDB.PtrDownSize);
                DeleteContentSector(downSectorPtr, Math.Min(len, bytesInDownLevel), sector);
                len -= bytesInDownLevel;
            }
        }

        public int GetKeySize()
        {
            if (_currentKeyIndexInLeaf < 0) return -1;
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            return iter.KeyLen;
        }

        public long GetValueSize()
        {
            if (_currentKeyIndexInLeaf < 0) return -1;
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            return iter.ValueLen;
        }

        public void PeekKey(int ofs, out int len, out byte[] buf, out int bufOfs)
        {
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            if (ofs >= iter.KeyLen)
            {
                len = 0;
                buf = null;
                bufOfs = 0;
                return;
            }
            if (ofs < iter.KeyLenInline)
            {
                len = iter.KeyLenInline - ofs;
                buf = _currentKeySector.Data;
                bufOfs = iter.KeyOffset + ofs;
                return;
            }
            ofs -= iter.KeyLenInline;
            SectorPtr dataSectorPtr = iter.KeySectorPtr;
            int dataLen = iter.KeyLen - iter.KeyLenInline;
            Sector parentOfSector = _currentKeySector;
            while (true)
            {
                Sector dataSector = _owner.TryGetSector(dataSectorPtr.Ptr);
                if (dataLen <= LowLevelDB.MaxLeafDataSectorSize)
                {
                    if (dataSector == null)
                    {
                        dataSector = _owner.ReadSector(dataSectorPtr, IsWriteTransaction());
                        dataSector.Type = SectorType.DataChild;
                        dataSector.Parent = parentOfSector;
                    }
                    buf = dataSector.Data;
                    bufOfs = ofs;
                    len = dataSector.Length - ofs;
                    return;
                }
                if (dataSector == null)
                {
                    dataSector = _owner.ReadSector(dataSectorPtr, IsWriteTransaction());
                    dataSector.Type = SectorType.DataParent;
                    dataSector.Parent = parentOfSector;
                }
                int downPtrCount;
                var bytesInDownLevel = (int)GetBytesInDownLevel(dataLen, out downPtrCount);
                int i = ofs / bytesInDownLevel;
                ofs = ofs % bytesInDownLevel;
                dataSectorPtr = SectorPtr.Unpack(dataSector.Data, i * LowLevelDB.PtrDownSize);
                if (i < downPtrCount - 1)
                {
                    dataLen = bytesInDownLevel;
                }
                else
                {
                    dataLen = dataLen % bytesInDownLevel;
                    if (dataLen == 0) dataLen = bytesInDownLevel;
                }
                parentOfSector = dataSector;
            }
        }

        bool IsWriteTransaction()
        {
            return _readLink == null;
        }

        public void ReadKey(int ofs, int len, byte[] buf, int bufOfs)
        {
            while (len > 0)
            {
                byte[] localBuf;
                int localBufOfs;
                int localOutLen;
                PeekKey(ofs, out localOutLen, out localBuf, out localBufOfs);
                if (localOutLen == 0) throw new BTDBException("Trying to read key outside of its boundary");
                Array.Copy(localBuf, localBufOfs, buf, bufOfs, Math.Min(len, localOutLen));
                ofs += localOutLen;
                bufOfs += localOutLen;
                len -= localOutLen;
            }
        }

        public void PeekValue(long ofs, out int len, out byte[] buf, out int bufOfs)
        {
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            if (ofs < 0 || ofs >= iter.ValueLen)
            {
                len = 0;
                buf = null;
                bufOfs = 0;
                return;
            }
            if (ofs >= iter.ValueLen - iter.ValueLenInline)
            {
                len = (int)(iter.ValueLen - ofs);
                buf = _currentKeySector.Data;
                bufOfs = iter.ValueOffset + iter.ValueLenInline - len;
                return;
            }
            SectorPtr dataSectorPtr = iter.ValueSectorPtr;
            long dataLen = iter.ValueLen - iter.ValueLenInline;
            Sector parentOfSector = _currentKeySector;
            Debug.Assert(ofs < dataLen);
            while (true)
            {
                Sector dataSector = _owner.TryGetSector(dataSectorPtr.Ptr);
                if (dataLen <= LowLevelDB.MaxLeafDataSectorSize)
                {
                    if (dataSector == null)
                    {
                        dataSector = _owner.ReadSector(dataSectorPtr, IsWriteTransaction());
                        dataSector.Type = SectorType.DataChild;
                        dataSector.Parent = parentOfSector;
                    }
                    buf = dataSector.Data;
                    bufOfs = (int)ofs;
                    len = (int)(dataSector.Length - ofs);
                    return;
                }
                if (dataSector == null)
                {
                    dataSector = _owner.ReadSector(dataSectorPtr, IsWriteTransaction());
                    dataSector.Type = SectorType.DataParent;
                    dataSector.Parent = parentOfSector;
                }
                int downPtrCount;
                long bytesInDownLevel = GetBytesInDownLevel(dataLen, out downPtrCount);
                var i = (int)(ofs / bytesInDownLevel);
                ofs = ofs % bytesInDownLevel;
                dataSectorPtr = SectorPtr.Unpack(dataSector.Data, i * LowLevelDB.PtrDownSize);
                if (i < downPtrCount - 1)
                {
                    dataLen = bytesInDownLevel;
                }
                else
                {
                    dataLen = dataLen % bytesInDownLevel;
                    if (dataLen == 0) dataLen = bytesInDownLevel;
                }
                parentOfSector = dataSector;
            }
        }

        public void ReadValue(long ofs, int len, byte[] buf, int bufOfs)
        {
            while (len > 0)
            {
                byte[] localBuf;
                int localBufOfs;
                int localOutLen;
                PeekValue(ofs, out localOutLen, out localBuf, out localBufOfs);
                if (localOutLen == 0) throw new BTDBException("Trying to read value outside of its boundary");
                Array.Copy(localBuf, localBufOfs, buf, bufOfs, Math.Min(len, localOutLen));
                ofs += localOutLen;
                bufOfs += localOutLen;
                len -= localOutLen;
            }
        }

        public void WriteValue(long ofs, int len, byte[] buf, int bufOfs)
        {
            if (len < 0) throw new ArgumentOutOfRangeException("len");
            if (ofs < 0) throw new ArgumentOutOfRangeException("ofs");
            if (len == 0) return;
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            UpgradeToWriteTransaction();
            if (ofs + len > GetValueSize()) SetValueSize(ofs + len);
            InternalWriteValue(ofs, len, buf, bufOfs);
        }

        void InternalWriteValue(long ofs, int len, byte[] buf, int bufOfs)
        {
            _currentKeySector = _owner.DirtizeSector(_currentKeySector, _currentKeySector.Parent);
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            var valueLen = iter.ValueLen;
            var valueLenInline = iter.ValueLenInline;
            if (ofs + len > valueLen - valueLenInline)
            {
                var inlineEnd = (int)(ofs + len - (valueLen - valueLenInline));
                var inlineStart = 0;
                if (ofs > valueLen - valueLenInline)
                {
                    inlineStart = (int)(ofs - (valueLen - valueLenInline));
                }
                if (buf != null)
                {
                    var inlineBufOfs = bufOfs + (int)(valueLen - valueLenInline + inlineStart - ofs);
                    Array.Copy(buf, inlineBufOfs, iter.Data, iter.ValueOffset + inlineStart, inlineEnd - inlineStart);
                }
                else
                {
                    Array.Clear(iter.Data, iter.ValueOffset + inlineStart, inlineEnd - inlineStart);
                }
                len -= inlineEnd - inlineStart;
                if (len == 0) return;
            }
            iter.ValueSectorPtr = RecursiveWriteValue(iter.ValueSectorPtr, valueLen - valueLenInline, ofs, len, buf, bufOfs, _currentKeySector);
        }

        SectorPtr RecursiveWriteValue(SectorPtr sectorPtr, long valueLen, long ofs, int len, byte[] buf, int bufOfs, Sector newParent)
        {
            if (ofs < 0) throw new ArgumentOutOfRangeException("ofs");
            if (ofs + len > valueLen) throw new ArgumentOutOfRangeException("ofs");
            Sector dataSector = _owner.TryGetSector(sectorPtr.Ptr);
            if (valueLen <= LowLevelDB.MaxLeafDataSectorSize)
            {
                if (dataSector == null)
                {
                    dataSector = _owner.ReadSector(sectorPtr, true);
                    dataSector.Type = SectorType.DataChild;
                    dataSector.Parent = newParent;
                }
                Debug.Assert(valueLen <= dataSector.Length);
                dataSector = _owner.DirtizeSector(dataSector, newParent);
                if (buf != null)
                {
                    Array.Copy(buf, bufOfs, dataSector.Data, (int)ofs, len);
                }
                else
                {
                    Array.Clear(dataSector.Data, (int)ofs, len);
                }
                return dataSector.ToPtrWithLen();
            }
            if (dataSector == null)
            {
                dataSector = _owner.ReadSector(sectorPtr, true);
                dataSector.Type = SectorType.DataParent;
                dataSector.Parent = newParent;
            }
            dataSector = _owner.DirtizeSector(dataSector, newParent);
            int downPtrCount;
            long bytesInDownLevel = GetBytesInDownLevel(valueLen, out downPtrCount);
            var i = (int)(ofs / bytesInDownLevel);
            while (i < downPtrCount)
            {
                long newofs = ofs - i * bytesInDownLevel;
                if (newofs + len <= 0) break;
                long downValueLen;
                if (i < downPtrCount - 1)
                {
                    downValueLen = bytesInDownLevel;
                }
                else
                {
                    downValueLen = valueLen % bytesInDownLevel;
                    if (downValueLen == 0) downValueLen = bytesInDownLevel;
                }
                SectorPtr downSectorPtr = SectorPtr.Unpack(dataSector.Data, i * LowLevelDB.PtrDownSize);
                int newBufOfs = bufOfs;
                int newlen = len;
                if (newofs < 0)
                {
                    newlen += (int)newofs;
                    newBufOfs -= (int)newofs;
                    newofs = 0;
                }
                if (downValueLen - newofs < newlen)
                {
                    newlen = (int)(downValueLen - newofs);
                }
                downSectorPtr = RecursiveWriteValue(downSectorPtr, downValueLen, newofs, newlen, buf, newBufOfs, dataSector);
                SectorPtr.Pack(dataSector.Data, i * LowLevelDB.PtrDownSize, downSectorPtr);
                i++;
            }
            return dataSector.ToPtrWithLen();
        }

        static long GetBytesInDownLevel(long len, out int downPtrCount)
        {
            if (len <= LowLevelDB.MaxLeafDataSectorSize)
            {
                downPtrCount = (int)len;
                return 1;
            }
            long leafSectors = len / LowLevelDB.MaxLeafDataSectorSize;
            if (len % LowLevelDB.MaxLeafDataSectorSize != 0) leafSectors++;
            long currentLevelLeafSectors = 1;
            while (currentLevelLeafSectors * LowLevelDB.MaxChildren < leafSectors)
                currentLevelLeafSectors *= LowLevelDB.MaxChildren;
            long bytesInDownLevel = currentLevelLeafSectors * LowLevelDB.MaxLeafDataSectorSize;
            downPtrCount = (int)((leafSectors + currentLevelLeafSectors - 1) / currentLevelLeafSectors);
            return bytesInDownLevel;
        }

        public void SetValueSize(long newSize)
        {
            if (newSize < 0) throw new ArgumentOutOfRangeException("newSize");
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            var iter = new BTreeChildIterator(_currentKeySector.Data);
            iter.MoveTo(_currentKeyIndexInLeaf);
            long oldSize = iter.ValueLen;
            if (oldSize == newSize) return;
            UpgradeToWriteTransaction();
            int oldInlineSize = BTreeChildIterator.CalcValueLenInline(oldSize);
            int newInlineSize = BTreeChildIterator.CalcValueLenInline(newSize);
            var newEndContent = new byte[newInlineSize];
            byte[] oldEndContent = null;
            long newEndContentOfs = newSize - newEndContent.Length;
            if (oldSize < newSize)
            {
                oldEndContent = new byte[oldInlineSize];
                ReadValue(oldSize - oldInlineSize, oldInlineSize, oldEndContent, 0);
            }
            else
            {
                ReadValue(newEndContentOfs, (int)Math.Min(newEndContent.Length, oldSize - newEndContentOfs), newEndContent, 0);
            }
            _currentKeySector = _owner.ResizeSectorWithUpdatePosition(_currentKeySector, iter.TotalLength - iter.CurrentEntrySize + BTreeChildIterator.CalcEntrySize(iter.KeyLen, newSize), _currentKeySector.Parent);
            iter.ResizeValue(_currentKeySector.Data, newSize);
            long oldDeepSize = oldSize - oldInlineSize;
            long newDeepSize = newSize - newInlineSize;
            if (oldDeepSize != newDeepSize)
            {
                if (oldDeepSize == 0)
                {
                    SectorPtr.Pack(_currentKeySector.Data, iter.ValueOffset + newInlineSize,
                                   CreateContentSector(newDeepSize, _currentKeySector));
                }
                else if (newDeepSize == 0)
                {
                    DeleteContentSector(iter.ValueSectorPtr, oldDeepSize, _currentKeySector);
                }
                else
                {
                    SectorPtr.Pack(_currentKeySector.Data, iter.ValueOffset + newInlineSize,
                                   ResizeContentSector(iter.ValueSectorPtr, oldDeepSize, _currentKeySector, newDeepSize));
                }
            }
            if (newEndContent.Length > 0) InternalWriteValue(newEndContentOfs, newEndContent.Length, newEndContent, 0);
            if (oldEndContent != null && oldEndContent.Length > 0) InternalWriteValue(oldSize - oldInlineSize, oldInlineSize, oldEndContent, 0);
        }

        SectorPtr ResizeContentSector(SectorPtr oldSectorPtr, long oldSize, Sector parentSector, long newSize)
        {
            Debug.Assert(oldSize != 0 && newSize != 0);
            if (oldSize == newSize) return oldSectorPtr;
            int oldDownPtrCount;
            var oldBytesInDownLevel = GetBytesInDownLevel(oldSize, out oldDownPtrCount);
            int newDownPtrCount;
            var newBytesInDownLevel = GetBytesInDownLevel(newSize, out newDownPtrCount);
            Sector sector;
            if (oldBytesInDownLevel < newBytesInDownLevel)
            {
                sector = _owner.NewSector();
                sector.SetLengthWithRound(newDownPtrCount * LowLevelDB.PtrDownSize);
                sector.Parent = parentSector;
                sector.Type = SectorType.DataParent;
                _owner.PublishSector(sector);
                SectorPtr.Pack(sector.Data, 0, ResizeContentSector(oldSectorPtr, oldSize, sector, newBytesInDownLevel));
                for (int i = 1; i < newDownPtrCount; i++)
                {
                    long downLevelSize = Math.Min(newSize - i * newBytesInDownLevel, newBytesInDownLevel);
                    SectorPtr.Pack(sector.Data, i * LowLevelDB.PtrDownSize, CreateContentSector(downLevelSize, sector));
                }
                return sector.ToPtrWithLen();
            }
            if (oldBytesInDownLevel > newBytesInDownLevel)
            {
                sector = _owner.TryGetSector(oldSectorPtr.Ptr);
                if (sector == null)
                {
                    sector = _owner.ReadSector(oldSectorPtr, true);
                    sector.Type = SectorType.DataParent;
                    sector.Parent = parentSector;
                }
                for (int i = 1; i < oldDownPtrCount; i++)
                {
                    long downLevelSize = Math.Min(oldSize - i * oldBytesInDownLevel, oldBytesInDownLevel);
                    DeleteContentSector(SectorPtr.Unpack(sector.Data, i * LowLevelDB.PtrDownSize), downLevelSize, sector);
                }
                _owner.DeallocateSector(sector);
                return ResizeContentSector(SectorPtr.Unpack(sector.Data, 0), oldBytesInDownLevel, parentSector, newSize);
            }
            byte[] oldData;
            if (oldBytesInDownLevel == 1)
            {
                sector = _owner.TryGetSector(oldSectorPtr.Ptr);
                if (sector == null)
                {
                    sector = _owner.ReadSector(oldSectorPtr, true);
                    sector.Type = SectorType.DataChild;
                    sector.Parent = parentSector;
                }
                oldData = sector.Data;
                sector = _owner.ResizeSectorNoUpdatePosition(sector, newDownPtrCount, parentSector);
                Array.Copy(oldData, 0, sector.Data, 0, Math.Min(oldDownPtrCount, newDownPtrCount));
                return sector.ToPtrWithLen();
            }
            sector = _owner.TryGetSector(oldSectorPtr.Ptr);
            if (sector == null)
            {
                sector = _owner.ReadSector(oldSectorPtr, true);
                sector.Type = SectorType.DataParent;
                sector.Parent = parentSector;
            }
            SectorPtr lastSectorPtr;
            long lastOffset;
            for (int i = newDownPtrCount + 1; i < oldDownPtrCount; i++)
            {
                lastOffset = i * oldBytesInDownLevel;
                lastSectorPtr = SectorPtr.Unpack(sector.Data, i * LowLevelDB.PtrDownSize);
                DeleteContentSector(lastSectorPtr, Math.Min(oldSize - lastOffset, oldBytesInDownLevel), sector);
            }
            var lastCommonPtrCount = Math.Min(oldDownPtrCount, newDownPtrCount) - 1;
            oldData = sector.Data;
            sector = _owner.ResizeSectorNoUpdatePosition(sector, newDownPtrCount * LowLevelDB.PtrDownSize, parentSector);
            Array.Copy(oldData, 0, sector.Data, 0, (lastCommonPtrCount + 1) * LowLevelDB.PtrDownSize);
            lastSectorPtr = SectorPtr.Unpack(sector.Data, lastCommonPtrCount * LowLevelDB.PtrDownSize);
            lastOffset = lastCommonPtrCount * newBytesInDownLevel;
            lastSectorPtr = ResizeContentSector(lastSectorPtr, Math.Min(oldSize - lastOffset, oldBytesInDownLevel), sector, Math.Min(newSize - lastOffset, newBytesInDownLevel));
            SectorPtr.Pack(sector.Data, lastCommonPtrCount * LowLevelDB.PtrDownSize, lastSectorPtr);
            for (int i = oldDownPtrCount; i < newDownPtrCount; i++)
            {
                lastOffset = i * oldBytesInDownLevel;
                lastSectorPtr = CreateContentSector(Math.Min(newSize - lastOffset, newBytesInDownLevel), sector);
                SectorPtr.Pack(sector.Data, i * LowLevelDB.PtrDownSize, lastSectorPtr);
            }
            return sector.ToPtrWithLen();
        }

        public void EraseCurrent()
        {
            if (_currentKeyIndexInLeaf < 0) throw new BTDBException("Current Key is invalid");
            EraseRange(_currentKeyIndex, _currentKeyIndex);
        }

        public void EraseAll()
        {
            EraseRange(0,long.MaxValue);
        }

        public void EraseRange(long firstKeyIndex, long lastKeyIndex)
        {
            if (firstKeyIndex < 0) firstKeyIndex = 0;
            if (lastKeyIndex >= GetKeyValueCount()) lastKeyIndex = _prefixKeyCount-1;
            if (lastKeyIndex < firstKeyIndex) return;
            UpgradeToWriteTransaction();
            firstKeyIndex += _prefixKeyStart;
            lastKeyIndex += _prefixKeyStart;
            throw new NotImplementedException();
        }

        public void Commit()
        {
            if (_readLink != null) return; // It is read only transaction nothing to commit
            _owner.CommitWriteTransaction();
        }

        public LowLevelDBStats CalculateStats()
        {
            return _owner.CalculateStats(_readLink);
        }
    }
}