// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Int256;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Address}->{Index}")]
    public readonly struct StorageCell : IEquatable<StorageCell>
    {
        public Address Address { get; }
        public UInt256 Index { get; }

        public StorageCell(Address address, in UInt256 index)
        {
            Address = address;
            Index = index;
        }

        public bool Equals(StorageCell other)
        {
            return Index.Equals(other.Index) && Address.Equals(other.Address);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            return obj is StorageCell address && Equals(address);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                ulong hash = (ulong)(Address.GetHashCode() * 397);
                hash ^= Index.u0 ^ Index.u1 ^ Index.u2 ^ Index.u3;
                return (int)(hash ^ (hash >> 32));
            }
        }

        public override string ToString()
        {
            return $"{Address}.{Index}";
        }
    }
}
