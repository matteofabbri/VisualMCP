# ReSharper C# context actions — the inventory

Generated from the installed engine, not from memory. Source: 
`JetBrains.ReSharper.Intentions.CSharp.dll` of JetBrains Rider 2026.2.1 (v777), types read through
`MetadataLoadContext` by base class.

Non-nested concrete classes in that assembly: **1458**

| Family | Count |
|---|---:|
| quick fix | 869 |
| context action | 307 |
| other | 202 |
| bulb action | 80 |

So the "1000+ context actions" is precise: **307 context actions + 869 quick fixes = 1176 actions** in the C# layer.

A quick fix is attached to an inspection (ReSharper ships ~2500 proprietary inspections);
a context action is the Alt+Enter menu on a code construct. Our coverage of the two is different:

- The IDE-style ones are `simplify_code`: 97 of the 126 ids of Microsoft's table, the other 29 delegated,
  not applicable to C#, or not a rewrite.
- The refactoring-shaped ones are the Refactoring tools (~38 of Rider's ~46 main refactorings).
- The analyzer-shaped ones are `apply_code_fix`, which runs the CodeFixProvider of the bundled packs.
- **The 14 rows marked below are `context_action`** (13 action ids: two ReSharper names share a raw-string
  conversion, and two share `wrap_in_using`), added because none of the three reached them.

Two of the 13 have no ReSharper *context action* behind them — `wrap_in_try_catch` and `wrap_in_if` mirror the
"Surround with" templates — so counting them here would be wrong. The 14 marked rows are the honest overlap
with the table below.

The **41 ids** `context_action` implements today are more than that first tranche: the guards, the wrap
actions, the statement and declaration shapes, nullable migration, the string conversions, LINQ in both
directions, StringBuilder, pattern merging and the C# 14 extension block. The census rows carry a status
only where the row maps one-to-one onto an id; a family that several ids cover is marked on its cluster.

**Visual Basic** (closed 2026-10-31): `context_action` routes a `.vb` document to
`VbContextActionService`, which implements **33 of the 41 ids** — every one that has a VB spelling. The
engine is a set of text edits over VB syntax under the same compiler gate, preview and edit history:
the argument guards (`If x Is Nothing Then Throw New ArgumentNullException(NameOf(x))`, `String.IsNullOrEmpty`,
`Count = 0`, `ContainsKey`, `[Enum].IsDefined`, the index bound), `Try`/`Catch`/`Finally`, `If`, `Using`
and `#Region` wrapping, `Imports` for `import_type`, `&`↔`$"…"`, digit separators, `name:=value` →
positional, `Dim x As Integer` ↔ `Dim x`, `Await x.ConfigureAwait(False)`, one `Dim` per declarator,
`&=` → `StringBuilder.Append`, nested `If` ↔ `AndAlso`, `If x IsNot Nothing Then x.M()` → `x?.M()`,
`If`/`ElseIf` → `Select Case`, `From … Select` ↔ `Where(Function(x) …)`, `For Each` ↔ indexed `For`, and
the descending `For`. The 8 ids that are C# syntax with no VB counterpart — raw/verbatim strings, braces,
`#nullable`, pattern combinators, the C# 14 extension block — are answered with that sentence instead of
being reported as unknown.

## The families not implemented

The 307 rows below are the census, not a to-do list: many are already reached by `simplify_code`, a
refactoring tool or an inspection fix. These are the clusters that are **not**, with what they would need:

| Cluster | Example actions | What it needs |
|---|---|---|
| if → switch expression | `IfToSwitchExpressionAction` | the statement form is `context_action if_to_switch`; the expression form is the IDE0066 rewrite, still absent |
| pattern merging | `MergeSequentialChecksAction`, `MergeIntoPatternAction`, `JoinNullCheckWithUsageAction` | ✅ `merge_sequential_checks`, `merge_patterns` (`x is A \|\| x is B` → `x is A or B`) and `join_null_check_with_usage` |
| LINQ shape | `QueryToExtensionMethodAction`, `FromLinqContextAction` | ✅ `linq_query_to_method` / `linq_method_to_query` over from/where/orderby/select; `join`, `let`, `group` and a second `from` are refused |
| loops | `ForeachToForAction`, `IterateViaForeachAction`, `IterateViaAwaitForeachAction` | `foreach_to_for`/`for_to_foreach` and `invert_loop` cover the first two; `await foreach` conversion is absent |
| string composition | `ConvertConcatenationToInterpolationAction`, `InterpolationToStringConcatAction`, `UseStringInterpolationAction`, the `PassStringFormat*` family | `concat_to_interpolated`/`interpolated_to_concat` cover the first two; the format family is absent |
| modifiers in bulk | `ChangeModifierAction` (visibility, `static`, `readonly`, `const`), `ChangeSetterKindAction`, `ChangeTypeKindAction` | one edit per modifier; the refactoring tools cover the single cases, not the menu |
| C# 14 extension blocks | `ConsolidateExtensionMembersAction`, `ConvertExtensionBlockToClassicExtensionMethodsAction` | ✅ `convert_to_extension_block` converts the classic form; consolidating existing blocks and the reverse conversion are absent |
| nullable migration | `MigrateToNullableEnableAction`, `AnnotateNullableAction`, `MarkNullableAction`, `AnnotateDataMembersAction` | `migrate_to_nullable` enables the context; the per-member annotations and their flow analysis are absent |
| XML doc generation | `AddInheritdocAction`, `AddExceptionDocCommentAction`, `CopyDocumentationAction` | `generate_docs` writes fresh docs, it does not derive them from a base |
| index/value checks | `CheckArrayIndexValueAction`, `CheckDictionaryKeyValueAction`, `CheckSpanIndexValueAction`, `NumericParameterCheckRangeAction` | `check_index_range`, `check_dictionary_key` and the numeric guards cover the first two; the span and range forms are absent |
| StringBuilder rewriting | the 5 `StringBuilderRewriting` actions | ✅ `concat_to_stringbuilder`; the Append-chain rewrites the family also holds are absent |
| ternary/guard shapes | `IfToTernaryAction`, `IfToGuardClauseAction`, `InvertTernaryConditionAction` | the IDE0045/0046 rewrites exist; the *statement* forms do not |

## The 307 context actions

`Status` is filled only where this repository implements the action. A blank status is not a
claim of absence: it is an action that overlaps `simplify_code`, a refactoring tool or an
inspection fix, or one that is not implemented — the table is the census, and the four
families above are the places to look before assuming the latter.

| ReSharper action | Status |
|---|---|
| `AddAnonymousFieldNameAction` |  |
| `AddArgumentNameContextAction` |  |
| `AddBracesAction` |  |
| `AddDeconstructionPatternComponentNameAction` |  |
| `AddExceptionDocCommentAction` |  |
| `AddFormatItemAction` |  |
| `AddInheritdocAction` |  |
| `AddPatternVariableAction` |  |
| `AddSecondAccessorAction` |  |
| `AddStringInterpolationArgumentAction` |  |
| `AddSwitchConditionComponentAction` |  |
| `AddSwitchSectionBracesAction` |  |
| `AddTupleComponentNameAction` |  |
| `AddUsingDeclarationAction` |  |
| `AllStringParameterIsNotNullOrEmptyCheckAction` | ✅ `context_action` → `check_string_not_null_or_empty` |
| `AllStringParameterIsNotNullOrWhitespaceCheckAction` |  |
| `AnnotateDataMembersAction` |  |
| `AnnotateEnumMembersAction` |  |
| `AnnotateHighlightingTypesAction` |  |
| `AnnotateOperationContractsAction` |  |
| `AnonymousMethodToLambdaExpressionAction` |  |
| `AnonymousMethodToLocalFunctionAction` |  |
| `AnonymousToDeclaredTypeAction` |  |
| `AnonymousToNamedMethodAction` |  |
| `AnonymousToTupleAction` |  |
| `AppendLineToSeparateAppendAction` |  |
| `AssertNotNullAction` |  |
| `AutoAccessorToExplicitAction` |  |
| `AutoPropertyToBackingFieldAction` |  |
| `AutoPropertyToFieldKeywordAction` |  |
| `BodyExpressionToStatementAction` |  |
| `BodyStatementToExpressionAction` |  |
| `CSharpInitializeAutoPropertyAction` |  |
| `CSharpInitializeFieldAction` |  |
| `CSharpInitializeNonPrivateFieldAction` |  |
| `CSharpUseEmptyStringLiteralAction` |  |
| `CSharpUseStringEmptyAction` |  |
| `CSharpXmlDocContextActionDataBuilder` |  |
| `CSharpXmlDocContextActionDataProvider` |  |
| `CaptureEntityNameFromStringLiteralAction` |  |
| `CatchDeclaredExceptionsAction` |  |
| `CatchExceptionToCatchAction` |  |
| `CatchToCatchExceptionAction` |  |
| `ChangeSetterKindAction` |  |
| `ChangeSignatureAction` |  |
| `ChangeTypeKindAction` |  |
| `CheckAllParamsNullAction` | ✅ `context_action` → `check_all_params_null` |
| `CheckArrayIndexValueAction` |  |
| `CheckCollectionIndexValueAction` |  |
| `CheckDictionaryKeyValueAction` |  |
| `CheckParamNullAction` | ✅ `context_action` → `check_param_null` |
| `CheckReferenceNullAction` |  |
| `CheckSpanIndexValueAction` |  |
| `CheckStringBuilderIndexValueAction` |  |
| `CheckStringIndexValueAction` |  |
| `CheckVariableNullAction` | ✅ `context_action` → `check_variable_null` |
| `ChopFormattingAction` |  |
| `CollectionInitializerToAddsAction` |  |
| `CollectionIsNotEmptyCheckAction` | ✅ `context_action` → `check_collection_not_empty` |
| `CollectionLengthCheckToListPatternAction` |  |
| `CompoundToRegularContextAction` |  |
| `ComputeConstantValueAction` |  |
| `ConditionalAccessToExpressionAction` |  |
| `ConditionalExpressionToAccessAction` |  |
| `ConditionalToNormalAccessAction` |  |
| `ConsolidateExtensionMembersAction` |  |
| `ConvertConcatenationToInterpolationAction` |  |
| `ConvertExtensionBlockToClassicExtensionMethodsAction` |  |
| `ConvertForEachCallToStatementAction` |  |
| `ConvertLiteralToBinAction` |  |
| `ConvertLiteralToDecAction` |  |
| `ConvertLiteralToHexAction` |  |
| `ConvertRegularStringInterpolationToVerbatimAction` |  |
| `ConvertRegularStringToVerbatimAction` | ✅ `context_action` → `string_to_verbatim` |
| `ConvertRegularToRawInterpolatedStringAction` |  |
| `ConvertRegularToRawStringAction` | ✅ `context_action` → `string_to_raw` |
| `ConvertStringEscapeSequenceAction` |  |
| `ConvertStringLiteralToInterpolationAction` |  |
| `ConvertToArrayCreationExpressionAction` |  |
| `ConvertToBlockScopedNamespaceAction` |  |
| `ConvertToClassicExtensionMethodAction` |  |
| `ConvertToCollectionInitializerAction` |  |
| `ConvertToDefaultExpressionAction` |  |
| `ConvertToExtensionBlockAction` |  |
| `ConvertToFileScopedNamespaceAction` |  |
| `ConvertToGlobalUsingAction` |  |
| `ConvertToLocalFunctionAction` |  |
| `ConvertToLogMethodAction` |  |
| `ConvertToPrimaryConstructorAction` |  |
| `ConvertToTryFinallyAction` | ✅ `context_action` → `wrap_in_try_finally` |
| `ConvertToUsingDeclarationAction` |  |
| `ConvertToUsingStatementAction` | ✅ `context_action` → `wrap_in_using` |
| `ConvertVerbatimStringInterpolationToRegularAction` |  |
| `ConvertVerbatimStringToRegularAction` | ✅ `context_action` → `string_to_regular` |
| `ConvertVerbatimToRawInterpolatedStringAction` |  |
| `ConvertVerbatimToRawStringAction` | ✅ `context_action` → `string_to_raw` |
| `CopyDocumentationAction` |  |
| `CopyTypeAction` |  |
| `CreateDerivedTypeAction` |  |
| `CreateMatchingCheckedOperatorAction` |  |
| `CreateNewTypePartAction` |  |
| `CreateOverloadAction` |  |
| `DeconstructingDeclarationToVarSyntaxAction` |  |
| `DeleteCommentAction` |  |
| `DisableImplicitUsingsAction` |  |
| `EnableImplicitUsingsAction` |  |
| `EncapsulateLazyFieldAction` |  |
| `EnumParameterIsDefinedCheckAction` |  |
| `EqualityOperatorToEqualsAction` |  |
| `EqualityOperatorToReferenceEqualsAction` |  |
| `EqualsToEqualityOperatorAction` |  |
| `EvaluateExpressionAction` |  |
| `ExplicitToImplicitMemberAction` |  |
| `ExtensionToStaticMethodAction` |  |
| `ExtractMethodAction` |  |
| `FieldKeywordToExplicitFieldAction` |  |
| `FillObjectInitializerAction` |  |
| `FixInvalidStringEscapingAction` |  |
| `FixRegularStringToVerbatimAction` |  |
| `FlipInvocationAction` |  |
| `FlipOperatorArgumentsAction` |  |
| `FlipRelationalExpressionAction` |  |
| `ForeachToForAction` |  |
| `GenerateFlagPresenceCheckExtensionAction` |  |
| `GenerateTypePatternsAction` |  |
| `IfAndMergeAction` |  |
| `IfNotNullToConditionalAccessAction` |  |
| `IfToGuardClauseAction` |  |
| `IfToNullPropagationAction` |  |
| `IfToTernaryAction` |  |
| `ImplementEventAction` |  |
| `ImplementNotifyPropertyChangedAction` |  |
| `ImplicitToExplicitMemberAction` |  |
| `ImportStaticMembersAction` |  |
| `IncludeIntoObjectPatternAction` |  |
| `InitializeMemberFromConstructorAction` |  |
| `InlineControlTransferStatementAction` |  |
| `InlineInsertToInterpolationAction` |  |
| `InlineOutVariableDeclarationAction` |  |
| `InlineTemporaryVariableAction` |  |
| `InlineUsingAliasAction` |  |
| `InlineVariableIntoConditionAction` |  |
| `InsertAnonymousMethodSignatureAction` |  |
| `InsertLambdaExpressionReturnTypeAction` |  |
| `InsertLambdaExpressionSignatureAction` |  |
| `InsertPragmaWarningDescriptionAction` |  |
| `InternalCreateStringsFileContextAction` |  |
| `InternalExportAnnotationsToExternal` |  |
| `InternalLocalizeContextAction` |  |
| `InterpolationToStringConcatAction` |  |
| `InterpolationToStringFormatAction` |  |
| `IntroduceParameterAction` |  |
| `IntroduceTypeAliasAction` |  |
| `IntroduceVariableAction` |  |
| `InvertIfAction` |  |
| `InvertTernaryConditionAction` |  |
| `InvokeAsStaticMethodAction` |  |
| `InvokeEventAction` |  |
| `IterateViaAwaitForeachAction` |  |
| `IterateViaForeachAction` |  |
| `Iterator2FunctionAction` |  |
| `JoinAttributesAction` |  |
| `JoinDeclarationAction` |  |
| `JoinDeconstructionDeclarationsAction` |  |
| `JoinNullCheckWithUsageAction` |  |
| `JoinStringsAction` |  |
| `LambdaExpressionToAnonymousMethodAction` |  |
| `LambdaExpressionToLocalFunctionAction` |  |
| `LambdaExpressionToStatementAction` |  |
| `LambdaStatementToExpressionAction` |  |
| `LambdaToNamedMethodAction` |  |
| `LiteralToStringBuilderAction` |  |
| `LiveTemplateBasedIterateViaForeachAction` |  |
| `LocalFunctionToNamedMethodAction` |  |
| `MergeDeconstructedDeclarationAction` |  |
| `MergeIntoPatternAction` |  |
| `MergeSequentialChecksAction` |  |
| `MethodToPropertyAction` |  |
| `MigrateToNullableEnableAction` |  |
| `MoveAnnotationOntoAnotherPartAction` |  |
| `MoveLocalFunctionToTheEndOfAScopeAction` |  |
| `MoveToAnotherPartAction` |  |
| `MoveToInitializerAction` |  |
| `MoveToOuterScopeAction` |  |
| `MultilineStringToAppendLineAction` |  |
| `NegateLogicalExpressionAction` |  |
| `NormalToConditionalAccessAction` |  |
| `NullCoalescingAssignmentToIfStatementAction` |  |
| `NullCoalescingToIfStatementAction` |  |
| `NullPropagationToIfStatementAction` |  |
| `NumericParameterCheckRangeAction` |  |
| `ObjectInitializerToAssignmentsAction` |  |
| `ObjectInitializerToConstructorAction` |  |
| `ParameterIsValidCheckAction` |  |
| `PassStringFormatAction` |  |
| `PassStringInterpolationAction` |  |
| `PropertyToMethodAction` |  |
| `QualifyReferenceAction` |  |
| `QualifyStaticMembersAction` |  |
| `QueryToExtensionMethodAction` |  |
| `RecursivePatternToAccessExpressionAction` |  |
| `ReferenceEqualsToEqualityOperatorAction` |  |
| `ReinterpretStringLiteralAsInterpolationAction` |  |
| `RemoveArgumentNameAction` |  |
| `RemoveBracesAction` |  |
| `RemoveDeconstructionPatternComponentNameAction` |  |
| `RemoveDigitSeparatorsAction` |  |
| `RemoveFormatItemAction` |  |
| `RemoveParamsModifierAction` |  |
| `RemoveParenthesisAction` |  |
| `RemovePreprocessorParenthesesAction` |  |
| `RemoveRegionAction` |  |
| `RemoveSwitchConditionComponentAction` |  |
| `RemoveTupleComponentNameAction` |  |
| `RenameFileToMatchTypeNameAction` |  |
| `ReplaceLogicalExpressionAction` |  |
| `ReturnValueAction` |  |
| `ReverseAssignmentAction` |  |
| `RevertForLoopAction` |  |
| `SeparateIntegralLiteralDigitsAction` |  |
| `SeparateLocalFunctionsWithJumpStatementAction` |  |
| `SeparateRealLiteralDigitsAction` |  |
| `SetDisposeAnalysisModeAction` |  |
| `SetParamsModifierAction` |  |
| `SimplifyLinqExpressionUseMinByAndMaxByAction` |  |
| `SortHighlightingTypesAction` |  |
| `SortUsingDirectivesAction` |  |
| `SpecifyEnumValuesAction` |  |
| `SplitAndInIfAction` |  |
| `SplitAndInReturnAction` |  |
| `SplitAndPatternAction` |  |
| `SplitAttributesAction` |  |
| `SplitConditionalAccessAction` |  |
| `SplitDeclarationAction` |  |
| `SplitDeclarationAndMoveToOuterScopeAction` |  |
| `SplitDeclarationsListAction` |  |
| `SplitFluentApiDivisionAction` |  |
| `SplitFluentApiUnchainAction` |  |
| `SplitIntoNestedPatternsAction` |  |
| `SplitListPatternAction` |  |
| `SplitOrInIfAction` |  |
| `SplitOrInReturnAction` |  |
| `SplitOrInSwitchCaseGuardAction` |  |
| `SplitOrPatternAction` |  |
| `SplitRecursivePatternAction` |  |
| `SplitReturnToIfAction` |  |
| `SplitStringAction` |  |
| `SplitTupleAssignmentAction` |  |
| `SplitVarPatternAction` |  |
| `StaticMethodToExtensionAction` |  |
| `StringEqualityToMethodInvocationAction` |  |
| `StringParameterIsNotNullOrEmptyCheckAction` | ✅ `context_action` → `check_string_not_null_or_empty` |
| `StringParameterIsNotNullOrWhitespaceCheckAction` |  |
| `StringToXElementCodeAction` |  |
| `SurroundWithFormatAction` |  |
| `SurroundWithQuotesAction` |  |
| `SwitchExpressionToIfElseAction` |  |
| `SwitchExpressionToStatementAction` |  |
| `SwitchExpressionToTernaryAction` |  |
| `SwitchStatementToExpressionAction` |  |
| `SwitchStatementToIfElseAction` |  |
| `TernaryToIfAction` |  |
| `TernaryToSwitchExpressionAction` |  |
| `TestInternalAction` |  |
| `ToAutoPropertyAction` |  |
| `ToComputedPropertyAction` |  |
| `ToDirectCastAction` |  |
| `ToExplicitConstructorAction` |  |
| `ToExplicitFieldAction` |  |
| `ToExplicitParamsArrayAction` |  |
| `ToExplicitParamsCollectionAction` |  |
| `ToExplicitProgramClassAction` |  |
| `ToExplicitPropertyAction` |  |
| `ToLocalVariableMutationAction` |  |
| `ToNamedPropertyPatternsAction` |  |
| `ToObjectPatternAction` |  |
| `ToParamsCollectionExpressionAction` |  |
| `ToPatternOverConstantValueCheckAction` |  |
| `ToSafeCastAction` |  |
| `ToStringBuilderConversionAction` |  |
| `ToStringBuilderSubstitutionAction` |  |
| `ToStringLiteralAction` |  |
| `ToTopLevelCodeAction` |  |
| `TransformParametersAction` |  |
| `TransformSingleParameterAction` |  |
| `TypeToVarAction` |  |
| `UncommentAction` |  |
| `UseArrayCreationExpressionAction` |  |
| `UseAsAndCheckForNullAction` |  |
| `UseCollectionExpressionAction` |  |
| `UseConfigureAwaitFalseAction` |  |
| `UseEnvironmentNewLineAction` |  |
| `UseExplicitArrayAction` |  |
| `UseExplicitDiscardDeclarationAction` |  |
| `UseExplicitTypeParametersAction` |  |
| `UseExplicitlyTypedNewAction` |  |
| `UseFormatStringAction` |  |
| `UseImplicitDiscardDeclarationAction` |  |
| `UseObjectInitializerAction` |  |
| `UsePositionalDeconstructionPatternAction` |  |
| `UseSeparateDeconstructionDeclarationsAction` |  |
| `UseSpanForStackAllocAction` |  |
| `UseStringInterpolationAction` |  |
| `UseTargetTypedNewAction` |  |
| `UseTypeAliasAction` |  |
| `VarToTypeAction` |  |
| `WrapTernaryExpressionInInterpolationWithParenthesesAction` |  |
