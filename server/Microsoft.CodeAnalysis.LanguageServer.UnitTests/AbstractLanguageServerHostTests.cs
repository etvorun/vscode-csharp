﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public abstract class AbstractLanguageServerHostTests
{
    protected ILogger TestOutputLogger { get; }

    protected AbstractLanguageServerHostTests(ITestOutputHelper testOutputHelper)
    {
        TestOutputLogger = new TestOutputLogger(testOutputHelper);
    }

    protected Task<TestLspServer> CreateLanguageServerAsync()
    {
        return TestLspServer.CreateAsync(new ClientCapabilities(), TestOutputLogger);
    }

    protected sealed class TestLspServer : IAsyncDisposable
    {
        private readonly LanguageServerHost _languageServerHost;
        private readonly Task _languageServerHostCompletionTask;
        private readonly JsonRpc _clientRpc;

        public static async Task<TestLspServer> CreateAsync(ClientCapabilities clientCapabilities, ILogger logger)
        {
            var exportProvider = await ExportProviderBuilder.CreateExportProviderAsync();
            var testLspServer = new TestLspServer(exportProvider, logger);
            var initializeResponse = await testLspServer.ExecuteRequestAsync<InitializeParams, InitializeResult>(Methods.InitializeName, new InitializeParams { Capabilities = clientCapabilities }, CancellationToken.None);
            Assert.NotNull(initializeResponse?.Capabilities);

            await testLspServer.ExecuteRequestAsync<InitializedParams, object>(Methods.InitializedName, new InitializedParams(), CancellationToken.None);

            return testLspServer;
        }

        private TestLspServer(ExportProvider exportProvider, ILogger logger)
        {
            var (clientStream, serverStream) = FullDuplexStream.CreatePair();
            _languageServerHost = new LanguageServerHost(serverStream, serverStream, exportProvider, logger);

            _clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, new JsonMessageFormatter()))
            {
                ExceptionStrategy = ExceptionProcessing.ISerializable,
            };

            _clientRpc.StartListening();

            // This task completes when the server shuts down.  We store it so that we can wait for completion
            // when we dispose of the test server.
            _languageServerHost.Start();

            _languageServerHostCompletionTask = _languageServerHost.WaitForExitAsync();
        }

        public async Task<TResponseType?> ExecuteRequestAsync<TRequestType, TResponseType>(string methodName, TRequestType request, CancellationToken cancellationToken) where TRequestType : class
        {
            var result = await _clientRpc.InvokeWithParameterObjectAsync<TResponseType>(methodName, request, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            await _clientRpc.InvokeAsync(Methods.ShutdownName).ConfigureAwait(false);
            await _clientRpc.NotifyAsync(Methods.ExitName).ConfigureAwait(false);

            // The language server host task should complete once shutdown and exit are called.
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            await _languageServerHostCompletionTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks

            _clientRpc.Dispose();
        }
    }
}
