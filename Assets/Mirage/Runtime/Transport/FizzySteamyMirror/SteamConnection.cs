#region Statements

using System;
using System.IO;
using System.Net;
using Cysharp.Threading.Tasks;
using Steamworks;
using UnityEngine;

#endregion

namespace Mirage.FizzySteam
{
    public class SteamConnection : SteamCommon, IConnection
    {
        private static readonly ILogger Logger = LogFactory.GetLogger(typeof(SteamConnection));

        #region Variables

        private byte[] _clientSendPoolData;
        private AutoResetUniTaskCompletionSource _connectedComplete;

        #endregion

        #region Class Specific

        /// <summary>
        ///     Connect to server.
        /// </summary>
        /// <returns></returns>
        public async UniTask ConnectAsync()
        {
            if (Logger.logEnabled) Logger.Log($"SteamConnection attempting connection to {Options.ConnectionAddress}");

            if(SteamNetworking.GetP2PSessionState(Options.ConnectionAddress, out P2PSessionState_t connectionState))
                if (bool.Parse(connectionState.m_bConnectionActive.ToString()))
                    SteamNetworking.CloseP2PSessionWithUser(Options.ConnectionAddress);

            try
            {
                // Send a message to server to initiate handshake connection
                SteamSend(Options.ConnectionAddress, InternalMessages.Connect);

                _connectedComplete = AutoResetUniTaskCompletionSource.Create();

                while (await UniTask.WhenAny(_connectedComplete.Task,
                           UniTask.Delay(TimeSpan.FromSeconds(Math.Max(1, Options.ConnectionTimeOut)))) != 0)
                {
                    if (Logger.logEnabled)
                        Logger.LogError(
                            $"SteamConnection connection to {Options.ConnectionAddress.m_SteamID} timed out.");

                    Error.Invoke(ErrorCodes.ConnectionFailed,
                        "SteamConnection connection to {Options.ConnectionAddress.m_SteamID} timed out.");

                    Disconnect();

                    return;
                }

                // Everything went good let's just return.
                // We need to switch to main thread for some reason.
                await UniTask.SwitchToMainThread();
            }
            catch (FormatException)
            {
                Error?.Invoke(ErrorCodes.IncorrectStringFormat, $"Connection string was not in the correct format.");

                if (Logger.logEnabled)
                    Logger.LogError("SteamConnection connection string was not in the right format. Did you enter a SteamId?");
            }
            catch (Exception ex)
            {
                Error?.Invoke(ErrorCodes.None, $"Error: {ex.Message}");

                if (Logger.logEnabled)
                    Logger.LogError($"SteamConnection error: {ex.Message}");

                Disconnect();
            }
        }

        #region Overrides of SteamCommon

        /// <summary>
        ///     Connection request has failed to connect to user.
        /// </summary>
        /// <param name="result">The information back from steam.</param>
        protected override void OnConnectionFailed(P2PSessionConnectFail_t result)
        {
            base.OnConnectionFailed(result);

            Disconnect();
        }

        #endregion

        /// <summary>
        ///     Process our internal messages away from mirror.
        /// </summary>
        /// <param name="type">The <see cref="InternalMessages"/> type message we received.</param>
        /// <param name="clientSteamId">The client id which the internal message came from.</param>
        protected override void OnReceiveInternalData(InternalMessages type, CSteamID clientSteamId)
        {
            if(!Connected) return;

            switch (type)
            {
                case InternalMessages.Accept:

                    if (Logger.logEnabled)
                        Logger.Log("Received internal message of server accepted our request to connect.");

                    _connectedComplete.TrySetResult();

                    break;
                case InternalMessages.Disconnect:

                        Disconnect();

                        if (Logger.logEnabled)
                            Logger.Log("Received internal message to disconnect steam user.");

                        break;
                case InternalMessages.TooManyUsers:

                    if (Logger.logEnabled)
                        Logger.Log("Received internal message that there are too many users connected to server.");

                    Error.Invoke(ErrorCodes.k_EP2PSessionErrorMax, "Too many users currently connected.");

                    break;
                default:
                    if (Logger.logEnabled)
                        Logger.Log(
                            $"SteamConnection cannot process internal message {type}. If this is anything other then {InternalMessages.Data} something has gone wrong.");
                    break;
            }
        }

        /// <summary>
        ///     Process data incoming from steam backend.
        /// </summary>
        /// <param name="data">The data that has come in.</param>
        /// <param name="clientSteamId">The client the data came from.</param>
        /// <param name="channel">The channel the data was received on.</param>
        internal override void OnReceiveData(byte[] data, CSteamID clientSteamId, int channel)
        {
            if(!Connected) return;

            var clientQueuePoolData = new Message(clientSteamId, InternalMessages.Data, data, channel);

            if (Logger.logEnabled)
                Logger.Log(
                    $"SteamConnection: Queue up message Event Type: {clientQueuePoolData.eventType} data: {BitConverter.ToString(clientQueuePoolData.data)}");

            QueuedData.Enqueue(clientQueuePoolData);
        }

        /// <summary>
        ///     Send data through steam network.
        /// </summary>
        /// <param name="host">The person we want to send data to.</param>
        /// <param name="msgBuffer">The data we are sending.</param>
        /// <param name="channel">The channel we are going to send data on.</param>
        /// <returns></returns>
        private bool Send(CSteamID host, byte[] msgBuffer, int channel)
        {
            return Connected && SteamNetworking.SendP2PPacket(host, msgBuffer, (uint)msgBuffer.Length, Options.Channels[channel], channel);
        }

        /// <summary>
        ///     Initialize <see cref="SteamConnection"/>
        /// </summary>
        /// <param name="options"></param>
        /// <param name="serverControlled">Is the data being processed by server or client.</param>
        public SteamConnection(SteamOptions options, bool serverControlled) : base(options)
        {
            Options = options;
            SteamNetworking.AllowP2PPacketRelay(Options.AllowSteamRelay);

            if (serverControlled) return;

            UniTask.Run(ProcessIncomingMessages).Forget();
        }

        #endregion

        #region Implementation of IConnection

        /// <summary>
        ///     Check if we have data in the pipe line that we need to process
        /// </summary>
        /// <param name="buffer">The buffer we need to write data too.</param>
        /// <returns></returns>
        public async UniTask<int> ReceiveAsync(MemoryStream buffer)
        {
            try
            {
                if (!Connected) throw new EndOfStreamException();

                while (QueuedData.Count <= 0)
                {
                    // Due to how steam works we have no connection state to be able to
                    // know when server disconnects us truly. So when steam sends a internal disconnect
                    // message we disconnect as normal but the _cancellation Token will trigger and we can exit cleanly
                    // using mirror.
                    if (!Connected) throw new EndOfStreamException();

                    await UniTask.Delay(1);
                }

                QueuedData.TryDequeue(out var clientReceivePoolData);

                buffer.SetLength(0);

                if (Logger.logEnabled)
                    Logger.Log(
                        $"SteamConnection processing message: {BitConverter.ToString(clientReceivePoolData.data)}");

                await buffer.WriteAsync(clientReceivePoolData.data, 0, clientReceivePoolData.data.Length);

                return clientReceivePoolData.Channel;
            }
            catch (EndOfStreamException)
            {
                throw new EndOfStreamException();
            }
        }

        /// <summary>
        ///     Disconnect steam user and close P2P session.
        /// </summary>
        public override async void Disconnect()
        {
            if(!Connected) return;

            if (Logger.logEnabled)
                Logger.Log("SteamConnection shutting down.");

            _clientSendPoolData = null;

            SteamSend(Options.ConnectionAddress, InternalMessages.Disconnect);

            // Wait 1 seconds to make sure the disconnect message gets fired.
            await UniTask.Delay(1000);

            base.Disconnect();

            SteamNetworking.CloseP2PSessionWithUser(Options.ConnectionAddress);

            _connectedComplete?.TrySetCanceled();
        }

        /// <summary>
        ///     Get the network address using steams id.
        /// </summary>
        /// <returns></returns>
        public EndPoint GetEndPointAddress()
        {
            return new DnsEndPoint(Options.ConnectionAddress.m_SteamID.ToString(), 0);
        }

        /// <summary>
        ///     Send data on a specific channel.
        /// </summary>
        /// <param name="data">The data we want to send.</param>
        /// <param name="channel">The channel we want to send it on.</param>
        /// <returns></returns>
        public UniTask SendAsync(ArraySegment<byte> data, int channel)
        {
            if (!Connected) return UniTask.CompletedTask;

            _clientSendPoolData = new byte[data.Count];

            Array.Copy(data.Array, data.Offset, _clientSendPoolData, 0, data.Count);

            Send(Options.ConnectionAddress, _clientSendPoolData, channel);

            return UniTask.CompletedTask;
        }

        #endregion
    }
}
