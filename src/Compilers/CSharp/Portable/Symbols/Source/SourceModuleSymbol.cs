﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Emit;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// Represents the primary module of an assembly being built by compiler.
    /// </summary>
    internal sealed class SourceModuleSymbol : NonMissingModuleSymbol, IAttributeTargetSymbol
    {
        /// <summary>
        /// Owning assembly.
        /// </summary>
        private readonly SourceAssemblySymbol _assemblySymbol;

        private ImmutableArray<AssemblySymbol> _lazyAssembliesToEmbedTypesFrom;

        private ThreeState _lazyContainsExplicitDefinitionOfNoPiaLocalTypes = ThreeState.Unknown;

        /// <summary>
        /// The declarations corresponding to the source files of this module.
        /// </summary>
        private readonly DeclarationTable _sources;

        private SymbolCompletionState _state;
        private CustomAttributesBag<CSharpAttributeData> _lazyCustomAttributesBag;
        private ImmutableArray<Location> _locations;
        private NamespaceSymbol _globalNamespace;

        private bool _hasBadAttributes;

        private ThreeState _lazyUseUpdatedEscapeRules;
        private ThreeState _lazyRequiresRefSafetyRulesAttribute;

        internal SourceModuleSymbol(
            SourceAssemblySymbol assemblySymbol,
            DeclarationTable declarations,
            string moduleName)
        {
            Debug.Assert((object)assemblySymbol != null);

            _assemblySymbol = assemblySymbol;
            _sources = declarations;
            _name = moduleName;
        }

        internal void RecordPresenceOfBadAttributes()
        {
            _hasBadAttributes = true;
        }

        internal bool HasBadAttributes
        {
            get
            {
                return _hasBadAttributes;
            }
        }

        internal override int Ordinal
        {
            get
            {
                return 0;
            }
        }

        internal override Machine Machine
        {
            get
            {
                switch (DeclaringCompilation.Options.Platform)
                {
                    case Platform.Arm:
                        return Machine.ArmThumb2;
                    case Platform.X64:
                        return Machine.Amd64;
                    case Platform.Arm64:
                        return Machine.Arm64;
                    case Platform.Itanium:
                        return Machine.IA64;
                    default:
                        return Machine.I386;
                }
            }
        }

        internal override bool Bit32Required
        {
            get
            {
                return DeclaringCompilation.Options.Platform == Platform.X86;
            }
        }

        internal bool AnyReferencedAssembliesAreLinked
        {
            get
            {
                return GetAssembliesToEmbedTypesFrom().Length > 0;
            }
        }

        internal bool MightContainNoPiaLocalTypes()
        {
            return AnyReferencedAssembliesAreLinked ||
                ContainsExplicitDefinitionOfNoPiaLocalTypes;
        }

        internal ImmutableArray<AssemblySymbol> GetAssembliesToEmbedTypesFrom()
        {
            if (_lazyAssembliesToEmbedTypesFrom.IsDefault)
            {
                AssertReferencesInitialized();
                var buffer = ArrayBuilder<AssemblySymbol>.GetInstance();

                foreach (AssemblySymbol asm in this.GetReferencedAssemblySymbols())
                {
                    if (asm.IsLinked)
                    {
                        buffer.Add(asm);
                    }
                }

                ImmutableInterlocked.InterlockedCompareExchange(ref _lazyAssembliesToEmbedTypesFrom,
                                                    buffer.ToImmutableAndFree(),
                                                    default(ImmutableArray<AssemblySymbol>));
            }

            Debug.Assert(!_lazyAssembliesToEmbedTypesFrom.IsDefault);
            return _lazyAssembliesToEmbedTypesFrom;
        }

        internal bool ContainsExplicitDefinitionOfNoPiaLocalTypes
        {
            get
            {
                if (_lazyContainsExplicitDefinitionOfNoPiaLocalTypes == ThreeState.Unknown)
                {
                    _lazyContainsExplicitDefinitionOfNoPiaLocalTypes = NamespaceContainsExplicitDefinitionOfNoPiaLocalTypes(GlobalNamespace).ToThreeState();
                }

                Debug.Assert(_lazyContainsExplicitDefinitionOfNoPiaLocalTypes != ThreeState.Unknown);
                return _lazyContainsExplicitDefinitionOfNoPiaLocalTypes == ThreeState.True;
            }
        }

        private static bool NamespaceContainsExplicitDefinitionOfNoPiaLocalTypes(NamespaceSymbol ns)
        {
            foreach (Symbol s in ns.GetMembersUnordered())
            {
                switch (s.Kind)
                {
                    case SymbolKind.Namespace:
                        if (NamespaceContainsExplicitDefinitionOfNoPiaLocalTypes((NamespaceSymbol)s))
                        {
                            return true;
                        }

                        break;

                    case SymbolKind.NamedType:
                        if (((NamedTypeSymbol)s).IsExplicitDefinitionOfNoPiaLocalType)
                        {
                            return true;
                        }

                        break;
                }
            }

            return false;
        }

        public override NamespaceSymbol GlobalNamespace
        {
            get
            {
                if ((object)_globalNamespace == null)
                {
                    var diagnostics = BindingDiagnosticBag.GetInstance();
                    var globalNS = new SourceNamespaceSymbol(
                        this, this, DeclaringCompilation.MergedRootDeclaration, diagnostics);

                    if (Interlocked.CompareExchange(ref _globalNamespace, globalNS, null) == null)
                    {
                        this.AddDeclarationDiagnostics(diagnostics);
                    }

                    diagnostics.Free();
                }

                return _globalNamespace;
            }
        }

        internal sealed override bool RequiresCompletion
        {
            get { return true; }
        }

        internal sealed override bool HasComplete(CompletionPart part)
        {
            return _state.HasComplete(part);
        }

        internal override void ForceComplete(SourceLocation locationOpt, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var incompletePart = _state.NextIncompletePart;
                switch (incompletePart)
                {
                    case CompletionPart.Attributes:
                        GetAttributes();
                        break;

                    case CompletionPart.StartValidatingReferencedAssemblies:
                        {
                            BindingDiagnosticBag diagnostics = null;

                            if (AnyReferencedAssembliesAreLinked)
                            {
                                diagnostics = BindingDiagnosticBag.GetInstance();
                                ValidateLinkedAssemblies(diagnostics, cancellationToken);
                            }

                            if (_state.NotePartComplete(CompletionPart.StartValidatingReferencedAssemblies))
                            {
                                if (diagnostics != null)
                                {
                                    _assemblySymbol.AddDeclarationDiagnostics(diagnostics);
                                }

                                _state.NotePartComplete(CompletionPart.FinishValidatingReferencedAssemblies);
                            }

                            if (diagnostics != null)
                            {
                                diagnostics.Free();
                            }
                        }
                        break;

                    case CompletionPart.FinishValidatingReferencedAssemblies:
                        // some other thread has started validating references (otherwise we would be in the case above) so
                        // we just wait for it to both finish and report the diagnostics.
                        Debug.Assert(_state.HasComplete(CompletionPart.StartValidatingReferencedAssemblies));
                        _state.SpinWaitComplete(CompletionPart.FinishValidatingReferencedAssemblies, cancellationToken);
                        break;

                    case CompletionPart.MembersCompleted:
                        this.GlobalNamespace.ForceComplete(locationOpt, cancellationToken);

                        if (this.GlobalNamespace.HasComplete(CompletionPart.MembersCompleted))
                        {
                            _state.NotePartComplete(CompletionPart.MembersCompleted);
                        }
                        else
                        {
                            Debug.Assert(locationOpt != null, "If no location was specified, then the namespace members should be completed");
                            return;
                        }

                        break;

                    case CompletionPart.None:
                        return;

                    default:
                        // any other values are completion parts intended for other kinds of symbols
                        _state.NotePartComplete(incompletePart);
                        break;
                }

                _state.SpinWaitComplete(incompletePart, cancellationToken);
            }
        }

        private void ValidateLinkedAssemblies(BindingDiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            foreach (AssemblySymbol a in GetReferencedAssemblySymbols())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!a.IsMissing && a.IsLinked)
                {
                    bool hasGuidAttribute = false;
                    bool hasImportedFromTypeLibOrPrimaryInteropAssemblyAttribute = false;

                    foreach (var attrData in a.GetAttributes())
                    {
                        if (attrData.IsTargetAttribute(a, AttributeDescription.GuidAttribute))
                        {
                            string guidString;
                            if (attrData.TryGetGuidAttributeValue(out guidString))
                            {
                                hasGuidAttribute = true;
                            }
                        }
                        else if (attrData.IsTargetAttribute(a, AttributeDescription.ImportedFromTypeLibAttribute))
                        {
                            if (attrData.CommonConstructorArguments.Length == 1)
                            {
                                hasImportedFromTypeLibOrPrimaryInteropAssemblyAttribute = true;
                            }
                        }
                        else if (attrData.IsTargetAttribute(a, AttributeDescription.PrimaryInteropAssemblyAttribute))
                        {
                            if (attrData.CommonConstructorArguments.Length == 2)
                            {
                                hasImportedFromTypeLibOrPrimaryInteropAssemblyAttribute = true;
                            }
                        }

                        if (hasGuidAttribute && hasImportedFromTypeLibOrPrimaryInteropAssemblyAttribute)
                        {
                            break;
                        }
                    }

                    if (!hasGuidAttribute)
                    {
                        // ERRID_PIAHasNoAssemblyGuid1/ERR_NoPIAAssemblyMissingAttribute
                        diagnostics.Add(ErrorCode.ERR_NoPIAAssemblyMissingAttribute, NoLocation.Singleton, a, AttributeDescription.GuidAttribute.FullName);
                    }

                    if (!hasImportedFromTypeLibOrPrimaryInteropAssemblyAttribute)
                    {
                        // ERRID_PIAHasNoTypeLibAttribute1/ERR_NoPIAAssemblyMissingAttributes
                        diagnostics.Add(ErrorCode.ERR_NoPIAAssemblyMissingAttributes, NoLocation.Singleton, a,
                                                   AttributeDescription.ImportedFromTypeLibAttribute.FullName,
                                                   AttributeDescription.PrimaryInteropAssemblyAttribute.FullName);
                    }
                }
            }
        }

        public override ImmutableArray<Location> Locations
        {
            get
            {
                if (_locations.IsDefault)
                {
                    ImmutableInterlocked.InterlockedInitialize(
                        ref _locations,
                        DeclaringCompilation.MergedRootDeclaration.Declarations.SelectAsArray(d => (Location)d.Location));
                }

                return _locations;
            }
        }

        /// <summary>
        /// The name (contains extension)
        /// </summary>
        private readonly string _name;

        public override string Name
        {
            get
            {
                return _name;
            }
        }

        public override Symbol ContainingSymbol
        {
            get
            {
                return _assemblySymbol;
            }
        }

        public override AssemblySymbol ContainingAssembly
        {
            get
            {
                return _assemblySymbol;
            }
        }

        internal SourceAssemblySymbol ContainingSourceAssembly
        {
            get
            {
                return _assemblySymbol;
            }
        }

        /// <remarks>
        /// This override is essential - it's a base case of the recursive definition.
        /// </remarks>
        internal override CSharpCompilation DeclaringCompilation
        {
            get
            {
                return _assemblySymbol.DeclaringCompilation;
            }
        }

        internal override ICollection<string> TypeNames
        {
            get
            {
                return _sources.TypeNames;
            }
        }

        internal override ICollection<string> NamespaceNames
        {
            get
            {
                return _sources.NamespaceNames;
            }
        }

        IAttributeTargetSymbol IAttributeTargetSymbol.AttributesOwner
        {
            get { return _assemblySymbol; }
        }

        AttributeLocation IAttributeTargetSymbol.DefaultAttributeLocation
        {
            get { return AttributeLocation.Module; }
        }

        AttributeLocation IAttributeTargetSymbol.AllowedAttributeLocations
        {
            get
            {
                return ContainingAssembly.IsInteractive ? AttributeLocation.None : AttributeLocation.Assembly | AttributeLocation.Module;
            }
        }

        /// <summary>
        /// Returns a bag of applied custom attributes and data decoded from well-known attributes. Returns null if there are no attributes applied on the symbol.
        /// </summary>
        /// <remarks>
        /// Forces binding and decoding of attributes.
        /// </remarks>
        private CustomAttributesBag<CSharpAttributeData> GetAttributesBag()
        {
            if (_lazyCustomAttributesBag == null || !_lazyCustomAttributesBag.IsSealed)
            {
                var mergedAttributes = ((SourceAssemblySymbol)this.ContainingAssembly).GetAttributeDeclarations();
                if (LoadAndValidateAttributes(OneOrMany.Create(mergedAttributes), ref _lazyCustomAttributesBag))
                {
                    var completed = _state.NotePartComplete(CompletionPart.Attributes);
                    Debug.Assert(completed);
                }
            }

            return _lazyCustomAttributesBag;
        }

        /// <summary>
        /// Gets the attributes applied on this symbol.
        /// Returns an empty array if there are no attributes.
        /// </summary>
        /// <remarks>
        /// NOTE: This method should always be kept as a sealed override.
        /// If you want to override attribute binding logic for a sub-class, then override <see cref="GetAttributesBag"/> method.
        /// </remarks>
        public sealed override ImmutableArray<CSharpAttributeData> GetAttributes()
        {
            return this.GetAttributesBag().Attributes;
        }

        /// <summary>
        /// Returns data decoded from well-known attributes applied to the symbol or null if there are no applied attributes.
        /// </summary>
        /// <remarks>
        /// Forces binding and decoding of attributes.
        /// </remarks>
        private ModuleWellKnownAttributeData GetDecodedWellKnownAttributeData()
        {
            var attributesBag = _lazyCustomAttributesBag;
            if (attributesBag == null || !attributesBag.IsDecodedWellKnownAttributeDataComputed)
            {
                attributesBag = this.GetAttributesBag();
            }

            return (ModuleWellKnownAttributeData)attributesBag.DecodedWellKnownAttributeData;
        }

        protected override void DecodeWellKnownAttributeImpl(ref DecodeWellKnownAttributeArguments<AttributeSyntax, CSharpAttributeData, AttributeLocation> arguments)
        {
            Debug.Assert((object)arguments.AttributeSyntaxOpt != null);

            var attribute = arguments.Attribute;
            Debug.Assert(!attribute.HasErrors);
            Debug.Assert(arguments.SymbolPart == AttributeLocation.None);

            if (attribute.IsTargetAttribute(this, AttributeDescription.DefaultCharSetAttribute))
            {
                CharSet charSet = attribute.GetConstructorArgument<CharSet>(0, SpecialType.System_Enum);
                if (!ModuleWellKnownAttributeData.IsValidCharSet(charSet))
                {
                    CSharpSyntaxNode attributeArgumentSyntax = attribute.GetAttributeArgumentSyntax(0, arguments.AttributeSyntaxOpt);
                    ((BindingDiagnosticBag)arguments.Diagnostics).Add(ErrorCode.ERR_InvalidAttributeArgument, attributeArgumentSyntax.Location, arguments.AttributeSyntaxOpt.GetErrorDisplayName());
                }
                else
                {
                    arguments.GetOrCreateData<ModuleWellKnownAttributeData>().DefaultCharacterSet = charSet;
                }
            }
            else if (ReportExplicitUseOfReservedAttributes(in arguments,
                ReservedAttributes.NullableContextAttribute | ReservedAttributes.NullablePublicOnlyAttribute | ReservedAttributes.RefSafetyRulesAttribute))
            {
            }
            else if (attribute.IsTargetAttribute(this, AttributeDescription.SkipLocalsInitAttribute))
            {
                CSharpAttributeData.DecodeSkipLocalsInitAttribute<ModuleWellKnownAttributeData>(DeclaringCompilation, ref arguments);
            }
        }

#nullable enable

        internal bool RequiresRefSafetyRulesAttribute()
        {
            if (_lazyRequiresRefSafetyRulesAttribute == ThreeState.Unknown)
            {
                bool value = UseUpdatedEscapeRules && namespaceIncludesRefs(GlobalNamespace);
                _lazyRequiresRefSafetyRulesAttribute = value.ToThreeState();
            }
            return _lazyRequiresRefSafetyRulesAttribute.Value();

            static bool namespaceIncludesRefs(NamespaceSymbol ns)
            {
                foreach (var member in ns.GetMembersUnordered())
                {
                    switch (member.Kind)
                    {
                        case SymbolKind.Namespace:
                            if (namespaceIncludesRefs((NamespaceSymbol)member))
                            {
                                return true;
                            }
                            break;
                        case SymbolKind.NamedType:
                            if (typeIncludesRefs((NamedTypeSymbol)member))
                            {
                                return true;
                            }
                            break;
                        default:
                            throw ExceptionUtilities.UnexpectedValue(member.Kind);
                    }
                }
                return false;
            }

            static bool typeIncludesRefs(NamedTypeSymbol type)
            {
                if (type.IsRefLikeType)
                {
                    return true;
                }
                foreach (var member in type.GetMembersUnordered())
                {
                    switch (member.Kind)
                    {
                        case SymbolKind.NamedType:
                            if (typeIncludesRefs((NamedTypeSymbol)member))
                            {
                                return true;
                            }
                            break;
                        case SymbolKind.Method:
                            if (methodIncludesRefs((MethodSymbol)member))
                            {
                                return true;
                            }
                            break;
                    }
                }
                return false;
            }

            static bool methodIncludesRefs(MethodSymbol method)
            {
                if (method.RefKind != RefKind.None ||
                    method.ReturnType.IsRefLikeType)
                {
                    return true;
                }
                return method.Parameters.Any(p => parameterIncludesRef(p));
            }

            static bool parameterIncludesRef(ParameterSymbol parameter)
            {
                return parameter.RefKind != RefKind.None ||
                    parameter.Type.IsRefLikeType;
            }
        }

        internal override void AddSynthesizedAttributes(PEModuleBuilder moduleBuilder, ref ArrayBuilder<SynthesizedAttributeData> attributes)
        {
            base.AddSynthesizedAttributes(moduleBuilder, ref attributes);

            var compilation = _assemblySymbol.DeclaringCompilation;
            if (compilation.Options.AllowUnsafe)
            {
                // NOTE: GlobalAttrBind::EmitCompilerGeneratedAttrs skips attribute if the well-known type isn't available.
                if (!(compilation.GetWellKnownType(WellKnownType.System_Security_UnverifiableCodeAttribute) is MissingMetadataTypeSymbol))
                {
                    AddSynthesizedAttribute(ref attributes, compilation.TrySynthesizeAttribute(
                        WellKnownMember.System_Security_UnverifiableCodeAttribute__ctor));
                }
            }

            if (RequiresRefSafetyRulesAttribute())
            {
                var version = ImmutableArray.Create(new TypedConstant(compilation.GetSpecialType(SpecialType.System_Int32), TypedConstantKind.Primitive, 11));
                AddSynthesizedAttribute(ref attributes, moduleBuilder.SynthesizeRefSafetyRulesAttribute(version));
            }

            if (moduleBuilder.ShouldEmitNullablePublicOnlyAttribute())
            {
                var includesInternals = ImmutableArray.Create(
                    new TypedConstant(compilation.GetSpecialType(SpecialType.System_Boolean), TypedConstantKind.Primitive, _assemblySymbol.InternalsAreVisible));
                AddSynthesizedAttribute(ref attributes, moduleBuilder.SynthesizeNullablePublicOnlyAttribute(includesInternals));
            }
        }

        internal override bool HasAssemblyCompilationRelaxationsAttribute
        {
            get
            {
                CommonAssemblyWellKnownAttributeData<NamedTypeSymbol> decodedData = ((SourceAssemblySymbol)this.ContainingAssembly).GetSourceDecodedWellKnownAttributeData();
                return decodedData != null && decodedData.HasCompilationRelaxationsAttribute;
            }
        }

        internal override bool HasAssemblyRuntimeCompatibilityAttribute
        {
            get
            {
                CommonAssemblyWellKnownAttributeData<NamedTypeSymbol> decodedData = ((SourceAssemblySymbol)this.ContainingAssembly).GetSourceDecodedWellKnownAttributeData();
                return decodedData != null && decodedData.HasRuntimeCompatibilityAttribute;
            }
        }

        internal override CharSet? DefaultMarshallingCharSet
        {
            get
            {
                var data = GetDecodedWellKnownAttributeData();
                return data != null && data.HasDefaultCharSetAttribute ? data.DefaultCharacterSet : (CharSet?)null;
            }
        }

        public sealed override bool AreLocalsZeroed
        {
            get
            {
                var data = GetDecodedWellKnownAttributeData();
                return data?.HasSkipLocalsInitAttribute != true;
            }
        }

        public override ModuleMetadata? GetMetadata() => null;

        internal override bool UseUpdatedEscapeRules
        {
            get
            {
                if (_lazyUseUpdatedEscapeRules == ThreeState.Unknown)
                {
                    var compilation = _assemblySymbol.DeclaringCompilation;
                    bool value = compilation.IsFeatureEnabled(MessageID.IDS_FeatureRefFields) || _assemblySymbol.RuntimeSupportsByRefFields;
                    _lazyUseUpdatedEscapeRules = value.ToThreeState();
                }
                return _lazyUseUpdatedEscapeRules == ThreeState.True;
            }
        }
    }
}
