﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Protocol;
using NSubstitute;
using Stratis.Bitcoin.Consensus;
using Stratis.Bitcoin.Configuration;
using Stratis.Bitcoin.Features.BlockStore;
using Stratis.Bitcoin.Primitives;
using Stratis.Bitcoin.Features.PoA;
using Stratis.Bitcoin.Utilities;
using Stratis.Bitcoin.Utilities.JsonErrors;
using Stratis.FederatedPeg.Features.FederationGateway;
using Stratis.FederatedPeg.Features.FederationGateway.Controllers;
using Stratis.FederatedPeg.Features.FederationGateway.Interfaces;
using Stratis.FederatedPeg.Features.FederationGateway.Models;
using Stratis.FederatedPeg.Features.FederationGateway.SourceChain;
using Stratis.FederatedPeg.Tests.Utils;
using Stratis.Sidechains.Networks;
using Xunit;

namespace Stratis.FederatedPeg.Tests
{
    public class FederationGatewayControllerTests
    {
        private readonly Network network;

        private readonly ILoggerFactory loggerFactory;

        private readonly ILogger logger;

        private readonly IMaturedBlockReceiver maturedBlockReceiver;

        private readonly ILeaderProvider leaderProvider;

        private ConcurrentChain chain;

        private readonly IDepositExtractor depositExtractor;

        private readonly ILeaderReceiver leaderReceiver;

        private readonly IConsensusManager consensusManager;

        private readonly IFederationGatewaySettings federationGatewaySettings;

        private readonly FederationManager federationManager;

        private readonly IFederationWalletManager federationWalletManager;

        public FederationGatewayControllerTests()
        {
            this.network = FederatedPegNetwork.NetworksSelector.Regtest();

            this.loggerFactory = Substitute.For<ILoggerFactory>();
            this.logger = Substitute.For<ILogger>();
            this.loggerFactory.CreateLogger(null).ReturnsForAnyArgs(this.logger);
            this.maturedBlockReceiver = Substitute.For<IMaturedBlockReceiver>();
            this.leaderProvider = Substitute.For<ILeaderProvider>();
            this.depositExtractor = Substitute.For<IDepositExtractor>();
            this.leaderReceiver = Substitute.For<ILeaderReceiver>();
            this.consensusManager = Substitute.For<IConsensusManager>();

            this.federationGatewaySettings = Substitute.For<IFederationGatewaySettings>();
            this.federationWalletManager = Substitute.For<IFederationWalletManager>();
            this.federationManager = new FederationManager(NodeSettings.Default(this.network), this.network, this.loggerFactory);
        }

        private FederationGatewayController CreateController()
        {
            var controller = new FederationGatewayController(
                this.loggerFactory,
                this.network,
                this.maturedBlockReceiver,
                this.leaderProvider,
                this.GetMaturedBlocksProvider(),
                this.leaderReceiver,
                this.federationGatewaySettings,
                this.federationWalletManager,
                this.federationManager);

            return controller;
        }

        private MaturedBlocksProvider GetMaturedBlocksProvider()
        {
            var blockRepository = Substitute.For<IBlockRepository>();

            blockRepository.GetBlocksAsync(Arg.Any<List<uint256>>()).ReturnsForAnyArgs((x) =>
            {
                var hashes = x.ArgAt<List<uint256>>(0);
                var blocks = new List<Block>();

                foreach (uint256 hash in hashes)
                {
                    blocks.Add(this.network.CreateBlock());
                }

                return blocks;
            });

            return new MaturedBlocksProvider(
                this.loggerFactory,
                this.chain,
                this.depositExtractor,
                blockRepository,
                this.consensusManager);
        }

        [Fact]
        public async void GetMaturedBlockDeposits_Fails_When_Block_Not_In_Chain_Async()
        {
            this.chain = Substitute.For<ConcurrentChain>();

            FederationGatewayController controller = this.CreateController();

            ChainedHeader chainedHeader = this.BuildChain(3).GetBlock(2);
            this.chain.Tip.Returns(chainedHeader);

            IActionResult result = await controller.GetMaturedBlockDepositsAsync(new MaturedBlockRequestModel(1)).ConfigureAwait(false);

            result.Should().BeOfType<ErrorResult>();

            var error = result as ErrorResult;
            error.Should().NotBeNull();

            var errorResponse = error.Value as ErrorResponse;
            errorResponse.Should().NotBeNull();
            errorResponse.Errors.Should().HaveCount(1);

            errorResponse.Errors.Should().Contain(
                e => e.Status == (int)HttpStatusCode.BadRequest);

            errorResponse.Errors.Should().Contain(
                e => e.Message.Contains("was not found on the block chain"));
        }

        [Fact]
        public async void GetMaturedBlockDeposits_Fails_When_Block_Height_Greater_Than_Minimum_Deposit_Confirmations_Async()
        {
            // Chain header height : 4
            // 0 - 1 - 2 - 3 - 4
            this.chain = this.BuildChain(5);

            FederationGatewayController controller = this.CreateController();

            // Minimum deposit confirmations : 2
            this.depositExtractor.MinimumDepositConfirmations.Returns((uint)2);

            int maturedHeight = (int)(this.chain.Tip.Height - this.depositExtractor.MinimumDepositConfirmations);

            // Back online at block height : 3
            // 0 - 1 - 2 - 3
            ChainedHeader earlierBlock = this.chain.GetBlock(maturedHeight + 1);

            // Mature height = 2 (Chain header height (4) - Minimum deposit confirmations (2))
            IActionResult result = await controller.GetMaturedBlockDepositsAsync(new MaturedBlockRequestModel(earlierBlock.Height)).ConfigureAwait(false);

            // Block height (3) > Mature height (2) - returns error message
            result.Should().BeOfType<ErrorResult>();

            var error = result as ErrorResult;
            error.Should().NotBeNull();

            var errorResponse = error.Value as ErrorResponse;
            errorResponse.Should().NotBeNull();
            errorResponse.Errors.Should().HaveCount(1);

            errorResponse.Errors.Should().Contain(
                e => e.Status == (int)HttpStatusCode.BadRequest);

            errorResponse.Errors.Should().Contain(
                e => e.Message.Contains($"Block height {earlierBlock.Height} submitted is not mature enough. Blocks less than a height of {maturedHeight} can be processed."));
        }

        [Fact]
        public async void GetMaturedBlockDeposits_Gets_All_Matured_Block_Deposits_Async()
        {
            this.chain = this.BuildChain(10);

            FederationGatewayController controller = this.CreateController();

            ChainedHeader earlierBlock = this.chain.GetBlock(2);

            int minConfirmations = 2;
            this.depositExtractor.MinimumDepositConfirmations.Returns((uint)minConfirmations);

            int depositExtractorCallCount = 0;
            this.depositExtractor.ExtractBlockDeposits(Arg.Any<ChainedHeaderBlock>()).Returns(new MaturedBlockDepositsModel(null, null));
            this.depositExtractor.When(x => x.ExtractBlockDeposits(Arg.Any<ChainedHeaderBlock>())).Do(info =>
            {
                depositExtractorCallCount++;
            });

            IActionResult result = await controller.GetMaturedBlockDepositsAsync(new MaturedBlockRequestModel(earlierBlock.Height)).ConfigureAwait(false);

            result.Should().BeOfType<JsonResult>();

            // If the minConfirmations == 0 and this.chain.Height == earlierBlock.Height then expectedCallCount must be 1.
            int expectedCallCount = (this.chain.Height - minConfirmations) - earlierBlock.Height + 1;

            depositExtractorCallCount.Should().Be(expectedCallCount);
        }

        [Fact]
        public void ReceiveCurrentBlockTip_Should_Call_LeaderProdvider_Update()
        {
            FederationGatewayController controller = this.CreateController();

            var model = new BlockTipModel(TestingValues.GetUint256(), TestingValues.GetPositiveInt(), TestingValues.GetPositiveInt());

            int leaderProviderCallCount = 0;
            this.leaderProvider.When(x => x.Update(Arg.Any<BlockTipModel>())).Do(info =>
            {
                leaderProviderCallCount++;
            });

            IActionResult result = controller.PushCurrentBlockTip(model);

            result.Should().BeOfType<OkResult>();
            leaderProviderCallCount.Should().Be(1);
        }

        [Fact]
        public void ReceiveMaturedBlock_Should_Call_ReceivedMatureBlockDeposits()
        {
            FederationGatewayController controller = this.CreateController();

            HashHeightPair hashHeightPair = TestingValues.GetHashHeightPair();
            var deposits = new MaturedBlockDepositsModel(new MaturedBlockInfoModel()
                { BlockHash = hashHeightPair.Hash, BlockHeight = hashHeightPair.Height },
                new[] { new Deposit(0, Money.COIN * 10000, "TTMM7qGGxD5c77pJ8puBg7sTLAm2zZNBwK",
                    hashHeightPair.Height, hashHeightPair.Hash) });

            int callCount = 0;
            this.maturedBlockReceiver.When(x => x.PushMaturedBlockDeposits(Arg.Any<IMaturedBlockDeposits[]>())).Do(info =>
            {
                callCount++;
            });

            controller.PushMaturedBlock(deposits);
            callCount.Should().Be(1);
        }

        [Fact]
        public void Call_Sidechain_Gateway_Get_Info()
        {
            string redeemScript = "2 02fad5f3c4fdf4c22e8be4cfda47882fff89aaa0a48c1ccad7fa80dc5fee9ccec3 02503f03243d41c141172465caca2f5cef7524f149e965483be7ce4e44107d7d35 03be943c3a31359cd8e67bedb7122a0898d2c204cf2d0119e923ded58c429ef97c 3 OP_CHECKMULTISIG";
            string federationIps = "127.0.0.1:36201,127.0.0.1:36202,127.0.0.1:36203";
            string multisigPubKey = "03be943c3a31359cd8e67bedb7122a0898d2c204cf2d0119e923ded58c429ef97c";
            string[] args = new[] { "-sidechain", "-regtest", $"-federationips={federationIps}", $"-redeemscript={redeemScript}", $"-publickey={multisigPubKey}", "-mincoinmaturity=1", "-mindepositconfirmations=1" };
            NodeSettings nodeSettings = new NodeSettings(FederatedPegNetwork.NetworksSelector.Regtest(), ProtocolVersion.ALT_PROTOCOL_VERSION, args: args);

            this.federationWalletManager.IsFederationActive().Returns(true);

            FederationGatewaySettings settings = new FederationGatewaySettings(nodeSettings);

            var controller = new FederationGatewayController(
                this.loggerFactory,
                this.network,
                this.maturedBlockReceiver,
                this.leaderProvider,
                this.GetMaturedBlocksProvider(),
                this.leaderReceiver,
                settings,
                this.federationWalletManager,
                this.federationManager);

            IActionResult result = controller.GetInfo();

            result.Should().BeOfType<JsonResult>();
            ((JsonResult)result).Value.Should().BeOfType<FederationGatewayInfoModel>();

            FederationGatewayInfoModel model = ((JsonResult)result).Value as FederationGatewayInfoModel;
            model.IsMainChain.Should().BeFalse();
            model.FederationMiningPubKeys.Should().Equal(((PoAConsensusOptions)FederatedPegNetwork.NetworksSelector.Regtest().Consensus.Options).FederationPublicKeys.Select(keys => keys.ToString()));
            model.MultiSigRedeemScript.Should().Be(redeemScript);
            string.Join(",", model.FederationNodeIpEndPoints).Should().Be(federationIps);
            model.IsActive.Should().BeTrue();
            model.MinCoinMaturity.Should().Be(1);
            model.MinimumDepositConfirmations.Should().Be(1);
            model.MultisigPublicKey.Should().Be(multisigPubKey);
        }

        [Fact]
        public void Call_Mainchain_Gateway_Get_Info()
        {
            string redeemScript = "2 02fad5f3c4fdf4c22e8be4cfda47882fff89aaa0a48c1ccad7fa80dc5fee9ccec3 02503f03243d41c141172465caca2f5cef7524f149e965483be7ce4e44107d7d35 03be943c3a31359cd8e67bedb7122a0898d2c204cf2d0119e923ded58c429ef97c 3 OP_CHECKMULTISIG";
            string federationIps = "127.0.0.1:36201,127.0.0.1:36202,127.0.0.1:36203";
            string multisigPubKey = "03be943c3a31359cd8e67bedb7122a0898d2c204cf2d0119e923ded58c429ef97c";
            string[] args = new[] { "-mainchain", "-testnet", $"-federationips={federationIps}", $"-redeemscript={redeemScript}", $"-publickey={multisigPubKey}", "-mincoinmaturity=1", "-mindepositconfirmations=1" };
            NodeSettings nodeSettings = new NodeSettings(FederatedPegNetwork.NetworksSelector.Regtest(), ProtocolVersion.ALT_PROTOCOL_VERSION, args: args);

            this.federationWalletManager.IsFederationActive().Returns(true);

            FederationGatewaySettings settings = new FederationGatewaySettings(nodeSettings);

            var controller = new FederationGatewayController(
                this.loggerFactory,
                this.network,
                this.maturedBlockReceiver,
                this.leaderProvider,
                this.GetMaturedBlocksProvider(),
                this.leaderReceiver,
                settings,
                this.federationWalletManager,
                this.federationManager);

            IActionResult result = controller.GetInfo();

            result.Should().BeOfType<JsonResult>();
            ((JsonResult)result).Value.Should().BeOfType<FederationGatewayInfoModel>();

            FederationGatewayInfoModel model = ((JsonResult)result).Value as FederationGatewayInfoModel;
            model.IsMainChain.Should().BeTrue();
            model.FederationMiningPubKeys.Should().BeNull();
            model.MiningPublicKey.Should().BeNull();
            model.MultiSigRedeemScript.Should().Be(redeemScript);
            string.Join(",", model.FederationNodeIpEndPoints).Should().Be(federationIps);
            model.IsActive.Should().BeTrue();
            model.MinCoinMaturity.Should().Be(1);
            model.MinimumDepositConfirmations.Should().Be(1);
            model.MultisigPublicKey.Should().Be(multisigPubKey);
        }

        private ConcurrentChain BuildChain(int blocks)
        {
            var chain = new ConcurrentChain(this.network);

            for(int i = 0; i < blocks - 1; i++)
            {
                this.AppendBlock(chain);
            }

            return chain;
        }

        private ChainedHeader AppendBlock(ChainedHeader previous, params ConcurrentChain[] chains)
        {
            ChainedHeader last = null;
            uint nonce = RandomUtils.GetUInt32();
            foreach (ConcurrentChain chain in chains)
            {
                Block block = this.network.CreateBlock();
                block.AddTransaction(this.network.CreateTransaction());
                block.UpdateMerkleRoot();
                block.Header.HashPrevBlock = previous == null ? chain.Tip.HashBlock : previous.HashBlock;
                block.Header.Nonce = nonce;
                if (!chain.TrySetTip(block.Header, out last))
                    throw new InvalidOperationException("Previous not existing");
            }
            return last;
        }

        private ChainedHeader AppendBlock(params ConcurrentChain[] chains)
        {
            ChainedHeader index = null;
            return this.AppendBlock(index, chains);
        }
    }
}
