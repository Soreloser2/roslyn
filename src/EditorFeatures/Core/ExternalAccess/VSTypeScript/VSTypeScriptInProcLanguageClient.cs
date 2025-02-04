﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.Implementation.LanguageClient;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.ExternalAccess.VSTypeScript.Api;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.ExternalAccess.VSTypeScript
{
    /// <summary>
    /// Language client to handle TS LSP requests.
    /// Allows us to move features to LSP without being blocked by TS as well
    /// as ensures that TS LSP features use correct solution snapshots.
    /// </summary>
    [ContentType(ContentTypeNames.TypeScriptContentTypeName)]
    [ContentType(ContentTypeNames.JavaScriptContentTypeName)]
    [Export(typeof(ILanguageClient))]
    internal class VSTypeScriptInProcLanguageClient : AbstractInProcLanguageClient
    {
        private readonly IVSTypeScriptCapabilitiesProvider? _typeScriptCapabilitiesProvider;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, true)]
        public VSTypeScriptInProcLanguageClient(
            [Import(AllowDefault = true)] IVSTypeScriptCapabilitiesProvider? typeScriptCapabilitiesProvider,
            VSTypeScriptLspServiceProvider lspServiceProvider,
            IGlobalOptionService globalOptions,
            IAsynchronousOperationListenerProvider listenerProvider,
            ILspLoggerFactory lspLoggerFactory,
            IThreadingContext threadingContext)
            : base(lspServiceProvider, globalOptions, listenerProvider, lspLoggerFactory, threadingContext)
        {
            _typeScriptCapabilitiesProvider = typeScriptCapabilitiesProvider;
        }

        protected override ImmutableArray<string> SupportedLanguages => ImmutableArray.Create(InternalLanguageNames.TypeScript);

        public override ServerCapabilities GetCapabilities(ClientCapabilities clientCapabilities)
        {
            var serverCapabilities = GetTypeScriptServerCapabilities(clientCapabilities);

            serverCapabilities.TextDocumentSync = new TextDocumentSyncOptions
            {
                Change = TextDocumentSyncKind.Incremental,
                OpenClose = true,
            };

            serverCapabilities.ProjectContextProvider = true;

            var isPullDiagnostics = GlobalOptions.IsPullDiagnostics(InternalDiagnosticsOptions.NormalDiagnosticMode);
            if (isPullDiagnostics)
            {
                serverCapabilities.SupportsDiagnosticRequests = true;
            }

            return serverCapabilities;
        }

        /// <summary>
        /// When pull diagnostics is enabled, ensure that initialization failures are displayed to the user as
        /// they will get no diagnostics.  When not enabled we don't show the failure box (failure will still be recorded in the task status center)
        /// as the failure is not catastrophic.
        /// </summary>
        public override bool ShowNotificationOnInitializeFailed => GlobalOptions.IsPullDiagnostics(InternalDiagnosticsOptions.NormalDiagnosticMode);

        public override WellKnownLspServerKinds ServerKind => WellKnownLspServerKinds.RoslynTypeScriptLspServer;

        private VSInternalServerCapabilities GetTypeScriptServerCapabilities(ClientCapabilities clientCapabilities)
        {
            if (_typeScriptCapabilitiesProvider != null)
            {
                var serializedClientCapabilities = JsonConvert.SerializeObject(clientCapabilities);
                var serializedServerCapabilities = _typeScriptCapabilitiesProvider.GetServerCapabilities(serializedClientCapabilities);
                var typeScriptServerCapabilities = JsonConvert.DeserializeObject<VSInternalServerCapabilities>(serializedServerCapabilities);
                Contract.ThrowIfNull(typeScriptServerCapabilities);
                return typeScriptServerCapabilities;
            }
            else
            {
                return new VSInternalServerCapabilities();
            }
        }
    }
}
