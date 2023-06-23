// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HealingWorldState : WorldState
{
    public HealingWorldState(ITrieStore? trieStore, IKeyValueStore? codeDb, ILogManager? logManager)
        : base(trieStore, codeDb, logManager, new HealingStateTree(trieStore, logManager), new HealingStorageTreeFactory())
    {
    }

    public void InitializeNetwork(ISyncPeerPool syncPeerPool)
    {
        ((HealingStateTree)_stateProvider._tree).InitializeNetwork(syncPeerPool);
        ((HealingStorageTreeFactory)_persistentStorageProvider._storageTreeFactory).InitializeNetwork(syncPeerPool);
    }
}