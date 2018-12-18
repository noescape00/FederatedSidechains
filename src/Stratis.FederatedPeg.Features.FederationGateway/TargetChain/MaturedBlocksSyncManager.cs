﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stratis.FederatedPeg.Features.FederationGateway.Interfaces;
using Stratis.FederatedPeg.Features.FederationGateway.Models;
using Stratis.FederatedPeg.Features.FederationGateway.RestClients;

namespace Stratis.FederatedPeg.Features.FederationGateway.TargetChain
{
    //TODO add tests coverage


    /// <summary>
    /// Handles block syncing between gateways on 2 chains. This node will request
    /// blocks from another chain to look for cross chain deposit transactions.
    /// </summary>
    public interface IMaturedBlocksSyncManager : IDisposable
    {
        /// <summary>Starts requesting blocks from another chain.</summary>
        void Initialize();
    }

    /// <inheritdoc cref="IMaturedBlocksSyncManager"/>
    public class MaturedBlocksSyncManager : IMaturedBlocksSyncManager
    {
        private readonly ICrossChainTransferStore store;

        private readonly IFederationGatewayClient federationGatewayClient;

        private readonly ILogger logger;

        private readonly CancellationTokenSource cancellation;

        private Task blockRequestingTask;

        /// <summary>The maximum amount of blocks to request at a time from alt chain.</summary>
        private const int MaxBlocksToRequest = 1000;

        /// <summary>When we are fully synced we stop asking for more blocks for this amount of time.</summary>
        private const int RefreshDelayMs = 10_000;

        public MaturedBlocksSyncManager(ICrossChainTransferStore store, IFederationGatewayClient federationGatewayClient, ILoggerFactory loggerFactory)
        {
            this.store = store;
            this.federationGatewayClient = federationGatewayClient;

            this.cancellation = new CancellationTokenSource();
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
        }

        /// <inheritdoc />
        public void Initialize()
        {
            this.blockRequestingTask = RequestMaturedBlocksContinouslyAsync();
        }

        /// <summary>Continuously requests matured blocks from another chain.</summary>
        private async Task RequestMaturedBlocksContinouslyAsync()
        {
            while (!this.cancellation.IsCancellationRequested)
            {
                bool delayRequired = await this.AskForBlocksAsync().ConfigureAwait(false);

                if (delayRequired)
                {
                    // Since we are synced or had a problem syncing there is no need to ask for more blocks right away.
                    // Therefore awaiting for a delay during which new block might be accepted on the alternative chain
                    // or alt chain node might be started.
                    await Task.Delay(RefreshDelayMs).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Asks for blocks from another gateway node and then processes them.</summary>
        /// <returns><c>true</c> if delay between next time we should ask for blocks is required; <c>false</c> otherwise.</returns>
        protected async Task<bool> AskForBlocksAsync()
        {
            int blocksToRequest = 1;

            // TODO why are we asking for max of 1 block and if it's not suspended then 1000? investigate this logic in maturedBlocksProvider
            if (!this.store.HasSuspended())
                blocksToRequest = MaxBlocksToRequest;

            // TODO investigate if we can ask for blocks that are reorgable. If so it's a problem and an attack vector.
            // API method that provides blocks should't give us blocks that are not mature!
            var model = new MaturedBlockRequestModel(this.store.NextMatureDepositHeight, blocksToRequest);

            this.logger.LogDebug("Request model created: {0}:{1}, {2}:{3}.", nameof(model.BlockHeight), model.BlockHeight,
                nameof(model.MaxBlocksToSend), model.MaxBlocksToSend);

            // Ask for blocks.
            IList<MaturedBlockDepositsModel> matureBlockDeposits = await this.federationGatewayClient.GetMaturedBlockDepositsAsync(model).ConfigureAwait(false);

            bool delayRequired = true;

            if (matureBlockDeposits != null)
            {
                // Log what we've received.
                foreach (MaturedBlockDepositsModel maturedBlockDeposit in matureBlockDeposits)
                {
                    foreach (IDeposit deposit in maturedBlockDeposit.Deposits)
                    {
                        this.logger.LogDebug("New deposit received BlockNumber={0}, TargetAddress='{1}', depositId='{2}', Amount='{3}'.",
                            deposit.BlockNumber, deposit.TargetAddress, deposit.Id, deposit.Amount);
                    }
                }

                if (matureBlockDeposits.Count > 0)
                {
                    bool success = await this.store.RecordLatestMatureDepositsAsync(matureBlockDeposits).ConfigureAwait(false);

                    // If we received a portion of blocks we can ask for new portion without any delay.
                    if (success)
                        delayRequired = false;
                }
                else
                {
                    this.logger.LogDebug("Considering ourselves fully synced since no blocks were received");

                    // If we've received nothing we assume we are at the tip and should flush.
                    // Same mechanic as with syncing headers protocol.
                    await this.store.SaveCurrentTipAsync().ConfigureAwait(false);
                }
            }

            return delayRequired;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.cancellation.Cancel();
            this.blockRequestingTask?.GetAwaiter().GetResult();
        }
    }
}
