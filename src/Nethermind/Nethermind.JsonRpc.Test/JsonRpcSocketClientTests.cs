// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[TestFixture]
public class JsonRpcSocketClientTests
{
    private static readonly object _bigObject = BuildRandomBigObject(100_000);

    class UsingIpc
    {
        private static async Task<T> MockServer<T>(IPEndPoint ipEndPoint, Func<Socket, Task<T>> func)
        {
            using Socket socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(ipEndPoint);
            socket.Listen();

            Socket handler = await socket.AcceptAsync();

            return await func(handler);
        }

        [Test]
        [Explicit("Takes too long to run")]
        public async Task Can_handle_very_large_objects()
        {
            IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

            Task<int> receiveSerialized = MockServer(ipEndPoint, async socket =>
            {
                byte[] buffer = new byte[1024];
                int totalRead = 0;

                int read;
                while ((read = await socket.ReceiveAsync(buffer)) != 0)
                {
                    totalRead += read;
                }
                return totalRead;
            });

            Task<int> sendJsonRpcResult = Task.Run(async () =>
            {
                using Socket socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(ipEndPoint);

                ISocketHandler handler = new IpcSocketsHandler(socket);
                JsonRpcSocketsClient client = new JsonRpcSocketsClient(
                    clientName: "TestClient",
                    handler: handler,
                    endpointType: RpcEndpoint.IPC,
                    jsonRpcProcessor: null!,
                    jsonRpcService: null!,
                    jsonRpcLocalStats: null!,
                    jsonSerializer: new EthereumJsonSerializer(converters: new GethLikeTxTraceConverter())
                );
                JsonRpcResult result = JsonRpcResult.Single(
                    new JsonRpcSuccessResponse()
                    {
                        MethodName = "mock", Id = "42", Result = _bigObject
                    }, default);

                return await client.SendJsonRpcResult(result);
            });

            await Task.WhenAll(sendJsonRpcResult, receiveSerialized);
            int sent = sendJsonRpcResult.Result;
            int received = receiveSerialized.Result;
            Assert.That(sent, Is.EqualTo(received));
        }

        [Test]
        [TestCase(2)]
        [TestCase(5)]
        public async Task Can_send_multiple_messages(int messageCount)
        {
            IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

            Task<int> receiveSerialized = MockServer(ipEndPoint, async socket =>
            {
                byte[] buffer = new byte[1024];
                int totalRead = 0;

                int read;
                while ((read = await socket.ReceiveAsync(buffer)) != 0)
                {
                    totalRead += read;
                }
                return totalRead;
            });

            Task<int> sendJsonRpcResult = Task.Run(async () =>
            {
                using Socket socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(ipEndPoint);

                ISocketHandler handler = new IpcSocketsHandler(socket);
                JsonRpcSocketsClient client = new JsonRpcSocketsClient(
                    clientName: "TestClient",
                    handler: handler,
                    endpointType: RpcEndpoint.IPC,
                    jsonRpcProcessor: null!,
                    jsonRpcService: null!,
                    jsonRpcLocalStats: null!,
                    jsonSerializer: new EthereumJsonSerializer(converters: new GethLikeTxTraceConverter())
                );
                JsonRpcResult result = JsonRpcResult.Single(
                    new JsonRpcSuccessResponse()
                    {
                        MethodName = "mock", Id = "42", Result = BuildRandomBigObject(1000)
                    }, default);

                int totalBytesSent = 0;
                for (int i = 0; i < messageCount; i++)
                {
                    totalBytesSent += await client.SendJsonRpcResult(result);
                }

                return totalBytesSent;
            });

            await Task.WhenAll(sendJsonRpcResult, receiveSerialized);
            int sent = sendJsonRpcResult.Result;
            int received = receiveSerialized.Result;
            Assert.That(sent, Is.EqualTo(received));
        }
    }

    class UsingWebSockets
    {
        [Test]
        [Explicit("Requires background web sockets server")]
        [TestCase(2)]
        [TestCase(10)]
        public async Task Can_send_multiple_messages(int messageCount)
        {
            Task<int> sendMessages = Task.Run(async () =>
            {
                using ClientWebSocket socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri("ws://localhost:1337/"), CancellationToken.None);

                using ISocketHandler handler = new WebSocketHandler(socket, NullLogManager.Instance);
                using JsonRpcSocketsClient client = new JsonRpcSocketsClient(
                    clientName: "TestClient",
                    handler: handler,
                    endpointType: RpcEndpoint.Ws,
                    jsonRpcProcessor: null!,
                    jsonRpcService: null!,
                    jsonRpcLocalStats: null!,
                    jsonSerializer: new EthereumJsonSerializer()
                );
                JsonRpcResult result = JsonRpcResult.Single(
                    new JsonRpcSuccessResponse
                    {
                        MethodName = "mock", Id = "42", Result = BuildRandomBigObject(1000)
                    }, default);

                for (int i = 0; i < messageCount; i++)
                {
                    await client.SendJsonRpcResult(result);
                }

                return messageCount;
            });

            await Task.WhenAll(sendMessages);
            int sent = sendMessages.Result;
            // TODO: Run local websocket server and check that we're getting exactly the same number of messages
            // that we're sending.
            Assert.That(sent, Is.EqualTo(messageCount));
        }
    }

    private static object BuildRandomBigObject(int length)
    {
        string[] strings = BuildRandomStringArray(length);
        return new GethLikeTxTrace()
        {
            Entries =
            {
                new GethTxTraceEntry
                {
                    Stack = strings, Memory = strings,
                }
            }
        };
    }

    private static string[] BuildRandomStringArray(int length)
    {
        string[] array = new string[length];
        for (int i = 0; i < length; i++)
        {
            array[i] = BuildRandomString(length);
            if (i % 100 == 0)
            {
                GC.Collect();
            }
        }
        return array;
    }

    private static string BuildRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        char[] stringChars = new char[length];
        Random random = new();

        for (int i = 0; i < stringChars.Length; i++)
        {
            stringChars[i] = chars[random.Next(chars.Length)];
        }

        return new string(stringChars);
    }
}