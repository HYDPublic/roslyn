﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.FindReferences
{
    internal interface IDefinitionsAndReferencesFactory : IWorkspaceService
    {
        DefinitionsAndReferences CreateDefinitionsAndReferences(
            Solution solution, IEnumerable<ReferencedSymbol> referencedSymbols);
    }

    [ExportWorkspaceService(typeof(IDefinitionsAndReferencesFactory)), Shared]
    internal class DefaultDefinitionsAndReferencesFactory : IDefinitionsAndReferencesFactory
    {
        public DefinitionsAndReferences CreateDefinitionsAndReferences(
            Solution solution, IEnumerable<ReferencedSymbol> referencedSymbols)
        {
            var definitions = ImmutableArray.CreateBuilder<DefinitionItem>();
            var references = ImmutableArray.CreateBuilder<SourceReferenceItem>();

            var uniqueLocations = new HashSet<DocumentLocation>();

            // Order the symbols by precedence, then create the appropriate
            // definition item per symbol and all refernece items for its
            // reference locations.
            foreach (var referencedSymbol in referencedSymbols.OrderBy(GetPrecedence))
            {
                ProcessReferencedSymbol(
                    solution, referencedSymbol, definitions, references, uniqueLocations);
            }

            return new DefinitionsAndReferences(definitions.ToImmutable(), references.ToImmutable());
        }

        /// <summary>
        /// Reference locations are deduplicated across the entire find references result set
        /// Order the definitions so that references to multiple definitions appear under the
        /// desired definition (e.g. constructor references should prefer the constructor method
        /// over the type definition). Note that this does not change the order in which
        /// definitions are displayed in Find Symbol Results, it only changes which definition
        /// a given reference should appear under when its location is a reference to multiple
        /// definitions.
        /// </summary>
        private static int GetPrecedence(ReferencedSymbol referencedSymbol)
        {
            switch (referencedSymbol.Definition.Kind)
            {
            case SymbolKind.Event:
            case SymbolKind.Field:
            case SymbolKind.Label:
            case SymbolKind.Local:
            case SymbolKind.Method:
            case SymbolKind.Parameter:
            case SymbolKind.Property:
            case SymbolKind.RangeVariable:
                return 0;

            case SymbolKind.ArrayType:
            case SymbolKind.DynamicType:
            case SymbolKind.ErrorType:
            case SymbolKind.NamedType:
            case SymbolKind.PointerType:
                return 1;

            default:
                return 2;
            }
        }

        private void ProcessReferencedSymbol(
            Solution solution,
            ReferencedSymbol referencedSymbol,
            ImmutableArray<DefinitionItem>.Builder definitions,
            ImmutableArray<SourceReferenceItem>.Builder references,
            HashSet<DocumentLocation> uniqueLocations)
        {
            // See if this is a symbol we even want to present to the user.  If not,
            // ignore it entirely (including all its reference locations).
            if (!referencedSymbol.ShouldShow())
            {
                return;
            }

            var definitionItem = referencedSymbol.Definition.ToDefinitionItem(solution);
            definitions.Add(definitionItem);

            // Now, create the SourceReferenceItems for all the reference locations
            // for this definition.
            CreateReferences(referencedSymbol, references, definitionItem, uniqueLocations);

            // Finally, see if there are any third parties that want to add their
            // own result to our collection.
            var thirdPartyItem = GetThirdPartyDefinitionItem(solution, referencedSymbol.Definition);
            if (thirdPartyItem != null)
            {
                definitions.Add(thirdPartyItem);
            }
        }

        /// <summary>
        /// Provides an extension point that allows for other workspace layers to add additional
        /// results to the results found by the FindReferences engine.
        /// </summary>
        protected virtual DefinitionItem GetThirdPartyDefinitionItem(
            Solution solution, ISymbol definition)
        {
            return null;
        }

        private static void CreateReferences(
            ReferencedSymbol referencedSymbol,
            ImmutableArray<SourceReferenceItem>.Builder references,
            DefinitionItem definitionItem,
            HashSet<DocumentLocation> uniqueLocations)
        {
            foreach (var referenceLocation in referencedSymbol.Locations)
            {
                var sourceReferenceItem = referenceLocation.TryCreateSourceReferenceItem(definitionItem);
                if (sourceReferenceItem == null)
                {
                    continue;
                }

                if (uniqueLocations.Add(sourceReferenceItem.Location))
                {
                    references.Add(sourceReferenceItem);
                }
            }
        }
    }

    internal static class DefinitionItemExtensions
    {
        public static DefinitionItem ToDefinitionItem(
            this ISymbol definition,
            Solution solution)
        {
            var definitionLocations = ConvertDefinitionLocations(solution, definition);
            var displayParts = definition.ToDisplayParts(s_definitionDisplayFormat).ToTaggedText();

            return new DefinitionItem(
                GlyphTags.GetTags(definition.GetGlyph()),
                displayParts,
                definitionLocations,
                definition.ShouldShowWithNoReferenceLocations());
        }

        private static ImmutableArray<DefinitionLocation> ConvertDefinitionLocations(
            Solution solution, ISymbol definition)
        {
            var result = ImmutableArray.CreateBuilder<DefinitionLocation>();

            // If it's a namespace, don't create any normal lcoation.  Namespaces
            // come from many different sources, but we'll only show a single 
            // root definition node for it.  That node won't be navigable.
            if (definition.Kind != SymbolKind.Namespace)
            {
                foreach (var location in definition.Locations)
                {
                    if (location.IsInMetadata)
                    {
                        result.Add(DefinitionLocation.CreateSymbolLocation(solution, definition));
                    }
                    else if (location.IsVisibleSourceLocation())
                    {
                        var document = solution.GetDocument(location.SourceTree);
                        if (document != null)
                        {
                            result.Add(DefinitionLocation.CreateDocumentLocation(
                                new DocumentLocation(document, location.SourceSpan)));
                        }
                    }
                }
            }

            if (result.Count == 0)
            {
                // If we got no definition locations, then create a sentinel one
                // that we can display but which will not allow navigation.
                result.Add(DefinitionLocation.CreateNonNavigatingLocation(
                    DefinitionLocation.GetOriginationParts(definition)));
            }

            return result.ToImmutable();
        }

        public static SourceReferenceItem TryCreateSourceReferenceItem(
            this ReferenceLocation referenceLocation,
            DefinitionItem definitionItem)
        {
            var location = referenceLocation.Location;

            Debug.Assert(location.IsInSource);
            if (!location.IsVisibleSourceLocation())
            {
                return null;
            }

            return new SourceReferenceItem(definitionItem, 
                new DocumentLocation(referenceLocation.Document, location.SourceSpan));
        }

        private static readonly SymbolDisplayFormat s_definitionDisplayFormat =
            new SymbolDisplayFormat(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                parameterOptions: SymbolDisplayParameterOptions.IncludeType,
                propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
                delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
                kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword | SymbolDisplayKindOptions.IncludeNamespaceKeyword | SymbolDisplayKindOptions.IncludeTypeKeyword,
                localOptions: SymbolDisplayLocalOptions.IncludeType,
                memberOptions:
                    SymbolDisplayMemberOptions.IncludeContainingType |
                    SymbolDisplayMemberOptions.IncludeExplicitInterface |
                    SymbolDisplayMemberOptions.IncludeModifiers |
                    SymbolDisplayMemberOptions.IncludeParameters |
                    SymbolDisplayMemberOptions.IncludeType,
                miscellaneousOptions:
                    SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                    SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
    }
}