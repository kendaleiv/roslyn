﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.UseNullPropagation;

namespace Microsoft.CodeAnalysis.CSharp.UseNullPropagation
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = PredefinedCodeFixProviderNames.UseNullPropagation), Shared]
    internal class CSharpUseNullPropagationCodeFixProvider : AbstractUseNullPropagationCodeFixProvider<
        SyntaxKind,
        ExpressionSyntax,
        StatementSyntax,
        ConditionalExpressionSyntax,
        BinaryExpressionSyntax,
        InvocationExpressionSyntax,
        ConditionalAccessExpressionSyntax,
        ElementAccessExpressionSyntax,
        MemberAccessExpressionSyntax,
        ElementBindingExpressionSyntax,
        IfStatementSyntax,
        ExpressionStatementSyntax,
        BracketedArgumentListSyntax>
    {
        [ImportingConstructor]
        [SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
        public CSharpUseNullPropagationCodeFixProvider()
        {
        }

        protected override ElementBindingExpressionSyntax ElementBindingExpression(BracketedArgumentListSyntax argumentList)
            => SyntaxFactory.ElementBindingExpression(argumentList);
    }
}
