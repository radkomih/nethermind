using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Baseline.JsonRpc;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Baseline.Test.JsonRpc
{
    [TestFixture]
    public class BaselineModuleTests
    {
        private IAbiEncoder _abiEncoder = new AbiEncoder();
        private IFileSystem _fileSystem;

        [SetUp]
        public void SetUp()
        {
            _abiEncoder = new AbiEncoder();
            _fileSystem = Substitute.For<IFileSystem>();
            const string expectedFilePath = "contracts/MerkleTreeSHA.bin";
            _fileSystem.File.ReadAllLinesAsync(expectedFilePath).Returns(File.ReadAllLines(expectedFilePath));
        }

        [Test]
        public async Task deploy_deploys_the_contract()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());

            BaselineModule baselineModule = new BaselineModule(testRpc.TxPoolBridge, _abiEncoder, _fileSystem, LimboLogs.Instance);
            var result = await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA");
            result.Data.Should().NotBe(null);
            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);
            
            await testRpc.AddBlock();

            testRpc.BlockTree.Head.Number.Should().Be(5);
            testRpc.BlockTree.Head.Transactions.Should().Contain(tx => tx.IsContractCreation);

            var code = testRpc.StateReader
                .GetCode(testRpc.BlockTree.Head.StateRoot, ContractAddress.From(TestItem.Addresses[0], 0));

            code.Should().NotBeEmpty();
        }
        
        [Test]
        public async Task deploy_returns_an_error_when_file_is_missing()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());

            BaselineModule baselineModule = new BaselineModule(testRpc.TxPoolBridge, _abiEncoder, _fileSystem, LimboLogs.Instance);
            var result = await baselineModule.baseline_deploy(TestItem.Addresses[0], "MissingContract");
            result.Data.Should().Be(null);
            result.ErrorCode.Should().NotBe(0);
            result.Result.Error.Should().NotBeEmpty();
            result.Result.ResultType.Should().Be(ResultType.Failure);
        }

        [Test]
        public async Task insert_leaf_given_hash_is_emitting_an_event()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(testRpc.TxPoolBridge, _abiEncoder, _fileSystem, LimboLogs.Instance);
            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;
            
            Keccak insertLeafTxHash = (await baselineModule.baseline_insertLeaf(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH)).Data;
            await testRpc.AddBlock();
            
            ReceiptForRpc insertLeafReceipt = (await testRpc.EthModule.eth_getTransactionReceipt(insertLeafTxHash)).Data;
            insertLeafReceipt.Logs.Should().HaveCount(1);
        }
        
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(128)]
        public async Task insert_leaves_given_hash_is_emitting_an_event(int leafCount)
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(testRpc.TxPoolBridge, _abiEncoder, _fileSystem, LimboLogs.Instance);
            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            Keccak[] leaves = Enumerable.Repeat(TestItem.KeccakH, leafCount).ToArray();
            Keccak insertLeavesTxHash = (await baselineModule.baseline_insertLeaves(TestItem.Addresses[1], receipt.ContractAddress, leaves)).Data;
            await testRpc.AddBlock();
            
            ReceiptForRpc insertLeafReceipt = (await testRpc.EthModule.eth_getTransactionReceipt(insertLeavesTxHash)).Data;
            insertLeafReceipt.Logs.Should().HaveCount(1);
            insertLeafReceipt.Logs[0].Data.Length.Should().Be(128 + leafCount * 32);
        }
        
        [Test]
        public async Task can_get_siblings_after_leaf_is_added()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(testRpc.TxPoolBridge, _abiEncoder, _fileSystem, LimboLogs.Instance);
            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertLeaf(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            var result = await baselineModule.baseline_getSiblings(1);
            await testRpc.AddBlock();
            
            result.Result.ResultType.Should().Be(ResultType.Success);
            result.Result.Error.Should().Be(null);
            result.ErrorCode.Should().Be(0);
            result.Data.Should().HaveCount(32);
        }
        
        [TestCase(-1L)]
        [TestCase(uint.MaxValue + 1L)]
        public async Task can_get_siblings_is_protected_against_overflow(long leafIndex)
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(testRpc.TxPoolBridge, _abiEncoder, _fileSystem, LimboLogs.Instance);
            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertLeaf(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            var result = await baselineModule.baseline_getSiblings(leafIndex);
            await testRpc.AddBlock();
            
            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.Result.Error.Should().NotBeNull();
            result.ErrorCode.Should().NotBe(0);
            result.Data.Should().BeNull();
        }
    }
}