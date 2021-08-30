﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.MoveStaticMembers;
using Microsoft.CodeAnalysis.PullMemberUp;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.LanguageServices.Implementation.PullMemberUp;
using Microsoft.VisualStudio.LanguageServices.Implementation.Utilities;
using Microsoft.VisualStudio.Utilities;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.MoveStaticMembers
{
    [ExportWorkspaceService(typeof(IMoveStaticMembersOptionsService), ServiceLayer.Host), Shared]
    internal class VisualStudioMoveStaticMembersOptionsService : IMoveStaticMembersOptionsService
    {
        private readonly IGlyphService _glyphService;
        private readonly IUIThreadOperationExecutor _uiThreadOperationExecutor;

        private const int HistorySize = 3;

        public readonly LinkedList<(string, string)> History = new();

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public VisualStudioMoveStaticMembersOptionsService(
            IGlyphService glyphService,
            IUIThreadOperationExecutor uiThreadOperationExecutor)
        {
            _glyphService = glyphService;
            _uiThreadOperationExecutor = uiThreadOperationExecutor;
        }

        public MoveStaticMembersOptions GetMoveMembersToTypeOptions(Document document, INamedTypeSymbol selectedType, ISymbol? selectedNodeSymbol)
        {
            var viewModel = GetViewModel(document, selectedType, selectedNodeSymbol, History, _glyphService, _uiThreadOperationExecutor);

            var dialog = new MoveStaticMembersDialog(viewModel);

            var result = dialog.ShowModal();

            if (result.GetValueOrDefault())
            {
                return GenerateOptions(document.Project.Language, viewModel);
            }

            return MoveStaticMembersOptions.Cancelled;
        }

        // internal for testing purposes
        internal static MoveStaticMembersOptions GenerateOptions(string language, MoveStaticMembersDialogViewModel viewModel)
        {
            // if the destination name contains extra namespaces, we want the last one as that is the real type name
            var typeName = viewModel.DestinationName.TypeName.Split('.').Last();
            var newFileName = Path.ChangeExtension(typeName, language == LanguageNames.CSharp ? ".cs" : ".vb");
            return new MoveStaticMembersOptions(
                newFileName,
                viewModel.DestinationName.TypeName,
                viewModel.MemberSelectionViewModel.CheckedMembers.SelectAsArray(vm => vm.Symbol));
        }

        // internal for testing purposes, get the view model
        internal static MoveStaticMembersDialogViewModel GetViewModel(
            Document document,
            INamedTypeSymbol selectedType,
            ISymbol? selectedNodeSymbol,
            LinkedList<(string, string)> history,
            IGlyphService? glyphService,
            IUIThreadOperationExecutor uiThreadOperationExecutor)
        {
            var membersInType = selectedType.GetMembers().
               WhereAsArray(member => MemberAndDestinationValidator.IsMemberValid(member) && member.IsStatic);

            var memberViewModels = membersInType
                .SelectAsArray(member =>
                    new SymbolViewModel<ISymbol>(member, glyphService)
                    {
                        // The member user selected will be checked at the beginning.
                        IsChecked = SymbolEquivalenceComparer.Instance.Equals(selectedNodeSymbol, member),
                    });

            using var cancellationTokenSource = new CancellationTokenSource();
            var memberToDependentsMap = SymbolDependentsBuilder.FindMemberToDependentsMap(membersInType, document.Project, cancellationTokenSource.Token);

            var existingTypeNames = MakeTypeNameItems(
                selectedType.ContainingNamespace,
                selectedType,
                document,
                history,
                cancellationTokenSource.Token);

            var candidateName = selectedType.Name + "Helpers";
            var defaultTypeName = NameGenerator.GenerateUniqueName(candidateName, name => !existingTypeNames.Contains(tName => tName.TypeName == name));

            var containingNamespaceDisplay = selectedType.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : selectedType.ContainingNamespace.ToDisplayString();

            var selectMembersViewModel = new StaticMemberSelectionViewModel(
                uiThreadOperationExecutor,
                memberViewModels,
                memberToDependentsMap);

            return new MoveStaticMembersDialogViewModel(selectMembersViewModel,
                defaultTypeName,
                existingTypeNames,
                selectedType.Name,
                document.GetRequiredLanguageService<ISyntaxFactsService>());
        }

        private static string GetFile(Location loc) => PathUtilities.GetFileName(loc.SourceTree!.FilePath);

        /// <summary>
        /// Construct all the type names declared in the project, 
        /// </summary>
        private static ImmutableArray<TypeNameItem> MakeTypeNameItems(
            INamespaceSymbol currentNamespace,
            INamedTypeSymbol currentType,
            Document currentDocument,
            LinkedList<(string, string)> history,
            CancellationToken cancellationToken)
        {
            return currentNamespace.GetAllTypes(cancellationToken).SelectMany(t =>
            {
                // for partially declared classes, we may want multiple entries for a single type.
                // filter to those actually in a real file, and that is not our current location.
                return t.Locations
                    .Where(l => l.IsInSource &&
                        (currentType.Name != t.Name || GetFile(l) != currentDocument.Name))
                    .Select(l => new TypeNameItem(
                        history.Contains((t.Name, currentDocument.Name)),
                        GetFile(l),
                        t));
            })
            .ToImmutableArrayOrEmpty()
            .Sort(comparison: TypeNameItem.CompareTo);
        }
    }
}
