﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Roslyn.Diagnostics.Analyzers
{
    public abstract class SymbolDeclaredEventAnalyzer<TSyntaxKind> : DiagnosticAnalyzer
        where TSyntaxKind : struct
    {
        private static LocalizableString localizableTitle = new LocalizableResourceString(nameof(RoslynDiagnosticsResources.SymbolDeclaredEventRuleTitle), RoslynDiagnosticsResources.ResourceManager, typeof(RoslynDiagnosticsResources));
        private static LocalizableString localizableMessage = new LocalizableResourceString(nameof(RoslynDiagnosticsResources.SymbolDeclaredEventRuleMessage), RoslynDiagnosticsResources.ResourceManager, typeof(RoslynDiagnosticsResources));
        private static LocalizableString localizableDescription = new LocalizableResourceString(nameof(RoslynDiagnosticsResources.SymbolDeclaredEventRuleDescription), RoslynDiagnosticsResources.ResourceManager, typeof(RoslynDiagnosticsResources));
        private static readonly string fullNameOfSymbol = typeof(ISymbol).FullName;
        
        internal static readonly DiagnosticDescriptor SymbolDeclaredEventRule = new DiagnosticDescriptor(
            RoslynDiagnosticIds.SymbolDeclaredEventRuleId,
            localizableTitle,
            localizableMessage,
            "Reliability",
            DiagnosticSeverity.Error,
            isEnabledByDefault: false,
            description: localizableDescription,
            customTags: WellKnownDiagnosticTags.Telemetry);

        public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get
            {
                return ImmutableArray.Create(SymbolDeclaredEventRule);
            }
        }

        public sealed override void Initialize(AnalysisContext context)
        {
            context.RegisterCompilationStartAction(compilationContext =>
            {
                var symbolType = compilationContext.Compilation.GetTypeByMetadataName(fullNameOfSymbol);
                if (symbolType != null)
                {
                    var compilationAnalyzer = GetCompilationAnalyzer(compilationContext.Compilation, symbolType);
                    if (compilationAnalyzer != null)
                    {
                        compilationContext.RegisterSyntaxNodeAction(compilationAnalyzer.AnalyzeNode, InvocationExpressionSyntaxKind);
                        compilationContext.RegisterSymbolAction(compilationAnalyzer.AnalyzeNamedType, SymbolKind.NamedType);
                        compilationContext.RegisterCompilationEndAction(compilationAnalyzer.AnalyzeCompilationEnd);
                    }
                }
            });
        }

        protected abstract TSyntaxKind InvocationExpressionSyntaxKind { get; }
        protected abstract CompilationAnalyzer GetCompilationAnalyzer(Compilation compilation, INamedTypeSymbol symbolType);

        protected abstract class CompilationAnalyzer
        {
            private readonly INamedTypeSymbol symbolType;
            private readonly INamedTypeSymbol compilationType;           
            private readonly HashSet<INamedTypeSymbol> sourceSymbolsToCheck = new HashSet<INamedTypeSymbol>();
            private readonly HashSet<INamedTypeSymbol> typesWithSymbolDeclaredEventInvoked = new HashSet<INamedTypeSymbol>();

            private const string SymbolDeclaredEventName = "SymbolDeclaredEvent";
            
            protected CompilationAnalyzer(INamedTypeSymbol symbolType, INamedTypeSymbol compilationType)
            {
                this.symbolType = symbolType;
                this.compilationType = compilationType;

                // If the below assert fire then probably the definition of "SymbolDeclaredEvent" has changed and we need to fix this analyzer.
                var symbolDeclaredEvent = compilationType.GetMembers(SymbolDeclaredEventName).Single();
                Debug.Assert(symbolDeclaredEvent.GetParameters().Count() == 1);
            }

            protected abstract SyntaxNode GetFirstArgumentOfInvocation(SyntaxNode invocation);
            protected abstract HashSet<string> SymbolTypesWithExpectedSymbolDeclaredEvent { get; }

            internal void AnalyzeNode(SyntaxNodeAnalysisContext context)
            {
                var invocationSymbol = context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol;
                if (invocationSymbol != null &&
                    invocationSymbol.Kind == SymbolKind.Method)
                {
                    AnalyzeMethodInvocation((IMethodSymbol)invocationSymbol, context);
                }
            }

            internal virtual void AnalyzeMethodInvocation(IMethodSymbol invocationSymbol, SyntaxNodeAnalysisContext context)
            {
                if (invocationSymbol.Name.Equals(SymbolDeclaredEventName) &&
                    compilationType.Equals(invocationSymbol.ContainingType))
                {
                    var argument = GetFirstArgumentOfInvocation(context.Node);
                    AnalyzeSymbolDeclaredEventInvocation(argument, context);
                }
            }

            protected bool AnalyzeSymbolDeclaredEventInvocation(SyntaxNode argument, SyntaxNodeAnalysisContext context)
            {
                if (argument != null)
                {
                    var argumentType = context.SemanticModel.GetTypeInfo(argument, context.CancellationToken).Type;
                    return AnalyzeSymbolDeclaredEventInvocation(argumentType);
                }

                return false;
            }

            private bool AnalyzeSymbolDeclaredEventInvocation(ISymbol type)
            {
                if (type != null &&
                    type.Kind == SymbolKind.NamedType &&
                    !type.Name.Equals("Symbol"))
                {
                    var namedType = (INamedTypeSymbol)type;
                    if (namedType.AllInterfaces.Contains(symbolType))
                    {
                        typesWithSymbolDeclaredEventInvoked.Add(namedType);
                        return true;
                    }
                }

                return false;
            }

            internal void AnalyzeNamedType(SymbolAnalysisContext context)
            {
                var namedType = (INamedTypeSymbol)context.Symbol;
                if (!namedType.IsAbstract &&
                    namedType.Name.StartsWith("Source") &&
                    !namedType.Name.Contains("Backing") &&
                    namedType.AllInterfaces.Contains(symbolType) &&
                    namedType.GetBaseTypesAndThis().Any(b => SymbolTypesWithExpectedSymbolDeclaredEvent.Contains(b.Name, StringComparer.Ordinal)))
                {
                    sourceSymbolsToCheck.Add(namedType);
                }
            }

            internal void AnalyzeCompilationEnd(CompilationEndAnalysisContext context)
            {
                foreach (var sourceSymbol in sourceSymbolsToCheck)
                {
                    var found = false;
                    foreach (var type in sourceSymbol.GetBaseTypesAndThis())
                    {
                        if (typesWithSymbolDeclaredEventInvoked.Contains(type))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        var diagnostic = Diagnostic.Create(SymbolDeclaredEventRule, sourceSymbol.Locations[0], sourceSymbol.Name, compilationType.Name, SymbolDeclaredEventName);
                        context.ReportDiagnostic(diagnostic);
                    }
                }
            }
        }
    }
}
