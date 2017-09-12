﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics.Telemetry;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Workspaces.Diagnostics;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Remote.Diagnostics
{
    internal class DiagnosticComputer
    {
        private readonly Project _project;
        private readonly Dictionary<DiagnosticAnalyzer, HashSet<DiagnosticData>> _exceptions;

        public DiagnosticComputer(Project project)
        {
            _project = project;
            _exceptions = new Dictionary<DiagnosticAnalyzer, HashSet<DiagnosticData>>();
        }

        public async Task<DiagnosticAnalysisResultMap<string, DiagnosticAnalysisResultBuilder>> GetDiagnosticsAsync(
            IEnumerable<AnalyzerReference> hostAnalyzers,
            OptionSet options,
            IEnumerable<string> analyzerIds,
            bool reportSuppressedDiagnostics,
            bool logAnalyzerExecutionTime,
            CancellationToken cancellationToken)
        {
            var analyzerMap = CreateAnalyzerMap(hostAnalyzers, _project);
            var analyzers = GetAnalyzers(analyzerMap, analyzerIds);

            if (analyzers.Length == 0)
            {
                return DiagnosticAnalysisResultMap.Create(ImmutableDictionary<string, DiagnosticAnalysisResultBuilder>.Empty, ImmutableDictionary<string, AnalyzerTelemetryInfo>.Empty);
            }

            var cacheService = _project.Solution.Workspace.Services.GetService<IProjectCacheService>();
            using (var cache = cacheService.EnableCaching(_project.Id))
            {
                return await AnalyzeAsync(analyzerMap, analyzers, options, reportSuppressedDiagnostics, logAnalyzerExecutionTime, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<DiagnosticAnalysisResultMap<string, DiagnosticAnalysisResultBuilder>> AnalyzeAsync(
            BidirectionalMap<string, DiagnosticAnalyzer> analyzerMap,
            ImmutableArray<DiagnosticAnalyzer> analyzers,
            OptionSet options,
            bool reportSuppressedDiagnostics,
            bool logAnalyzerExecutionTime,
            CancellationToken cancellationToken)
        {
            // flag that controls concurrency
            var useConcurrent = true;

            // get original compilation
            var compilation = await _project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);

            // fork compilation with concurrent build. this is okay since WithAnalyzers will fork compilation
            // anyway to attach event queue. this should make compiling compilation concurrent and make things
            // faster
            compilation = compilation.WithOptions(compilation.Options.WithConcurrentBuild(useConcurrent));

            // We need this to fork soluton, otherwise, option is cached at document.
            // all this can go away once we do this - https://github.com/dotnet/roslyn/issues/19284
            var temporaryWorksapce = new TemporaryWorkspace(_project.Solution);

            // TODO: can we support analyzerExceptionFilter in remote host? 
            //       right now, host doesn't support watson, we might try to use new NonFatal watson API?
            var analyzerOptions = new CompilationWithAnalyzersOptions(
                    options: new WorkspaceAnalyzerOptions(_project.AnalyzerOptions, MergeOptions(_project.Solution.Options, options), temporaryWorksapce.CurrentSolution),
                    onAnalyzerException: OnAnalyzerException,
                    analyzerExceptionFilter: null,
                    concurrentAnalysis: useConcurrent,
                    logAnalyzerExecutionTime: logAnalyzerExecutionTime,
                    reportSuppressedDiagnostics: reportSuppressedDiagnostics);

            var analyzerDriver = compilation.WithAnalyzers(analyzers, analyzerOptions);

            // PERF: Run all analyzers at once using the new GetAnalysisResultAsync API.
            var analysisResult = await analyzerDriver.GetAnalysisResultAsync(cancellationToken).ConfigureAwait(false);

            var builderMap = analysisResult.ToResultBuilderMap(_project, VersionStamp.Default, compilation, analysisResult.Analyzers, cancellationToken);

            return DiagnosticAnalysisResultMap.Create(
                builderMap.ToImmutableDictionary(kv => GetAnalyzerId(analyzerMap, kv.Key), kv => kv.Value),
                analysisResult.AnalyzerTelemetryInfo.ToImmutableDictionary(kv => GetAnalyzerId(analyzerMap, kv.Key), kv => kv.Value),
                _exceptions.ToImmutableDictionary(kv => GetAnalyzerId(analyzerMap, kv.Key), kv => kv.Value.ToImmutableArray()));
        }

        private void OnAnalyzerException(Exception exception, DiagnosticAnalyzer analyzer, Diagnostic diagnostic)
        {
            lock (_exceptions)
            {
                var list = _exceptions.GetOrAdd(analyzer, _ => new HashSet<DiagnosticData>());
                list.Add(DiagnosticData.Create(_project, diagnostic));
            }
        }

        private string GetAnalyzerId(BidirectionalMap<string, DiagnosticAnalyzer> analyzerMap, DiagnosticAnalyzer analyzer)
        {
            var analyzerId = analyzerMap.GetKeyOrDefault(analyzer);
            Contract.ThrowIfNull(analyzerId);

            return analyzerId;
        }

        private ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(BidirectionalMap<string, DiagnosticAnalyzer> analyzerMap, IEnumerable<string> analyzerIds)
        {
            // TODO: this probably need to be cached as well in analyzer service?
            var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();

            foreach (var analyzerId in analyzerIds)
            {
                if (analyzerMap.TryGetValue(analyzerId, out var analyzer))
                {
                    builder.Add(analyzer);
                }
            }

            return builder.ToImmutable();
        }

        private BidirectionalMap<string, DiagnosticAnalyzer> CreateAnalyzerMap(IEnumerable<AnalyzerReference> hostAnalyzers, Project project)
        {
            // TODO: probably need something like analyzer service so that we don't do this repeatedly?
            return new BidirectionalMap<string, DiagnosticAnalyzer>(
                hostAnalyzers.Concat(project.AnalyzerReferences).SelectMany(r => r.GetAnalyzers(project.Language)).Select(a => KeyValuePair.Create(a.GetAnalyzerId(), a)));
        }

        private OptionSet MergeOptions(OptionSet workspaceOptions, OptionSet userOptions)
        {
            var newOptions = workspaceOptions;
            foreach (var key in userOptions.GetChangedOptions(workspaceOptions))
            {
                newOptions = newOptions.WithChangedOption(key, userOptions.GetOption(key));
            }

            return newOptions;
        }
    }
}
