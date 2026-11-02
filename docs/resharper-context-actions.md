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
- **`context_action` implements 75 ids** — the guards, the wrap actions, the statement and declaration
  shapes, nullable migration, the string conversions, LINQ in both directions, StringBuilder, pattern
  merging, the C# 14 extension block, and the 21 census-tail ids added on 2026-11-02. A ReSharper name whose
  normalized form already is an id (`AddBracesAction` → `add_braces`) reaches it directly; 36 more names are
  aliases, and the table below marks them. A blank row is not a claim of absence: it is an action reached by
  `simplify_code`, a refactoring tool, an inspection fix, or one of the families below.

**Visual Basic** (closed 2026-10-31): `context_action` routes a `.vb` document to
`VbContextActionService`, which implements **33 of the ids** — every one that has a VB spelling. The
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
| loops | `ForeachToForAction`, `IterateViaForeachAction`, `IterateViaAwaitForeachAction` | ✅ `foreach_to_for`/`for_to_foreach` cover the first two and `await_foreach` covers the third; `invert_loop` is the separate descending conversion |
| string composition | `ConvertConcatenationToInterpolationAction`, `InterpolationToStringConcatAction`, `UseStringInterpolationAction`, the `PassStringFormat*` family | `concat_to_interpolated`/`interpolated_to_concat` cover the first two; the format family is absent |
| modifiers in bulk | `ChangeModifierAction` (visibility, `static`, `readonly`, `const`), `ChangeSetterKindAction`, `ChangeTypeKindAction` | one edit per modifier; the refactoring tools cover the single cases, not the menu |
| C# 14 extension blocks | `ConsolidateExtensionMembersAction`, `ConvertExtensionBlockToClassicExtensionMethodsAction` | ✅ `convert_to_extension_block` converts the classic form; consolidating existing blocks and the reverse conversion are absent |
| nullable migration | `MigrateToNullableEnableAction`, `AnnotateNullableAction`, `MarkNullableAction`, `AnnotateDataMembersAction` | `migrate_to_nullable` enables the context; the per-member annotations and their flow analysis are absent |
| XML doc generation | `AddInheritdocAction`, `AddExceptionDocCommentAction`, `CopyDocumentationAction` | ✅ `add_inheritdoc` and `add_exception_docs`; `CopyDocumentationAction` is still absent, because it needs the base member's documentation text |
| index/value checks | `CheckArrayIndexValueAction`, `CheckDictionaryKeyValueAction`, `CheckSpanIndexValueAction`, `NumericParameterCheckRangeAction` | ✅ `check_index_range`, `check_dictionary_key` and `check_span_index`; `NumericParameterCheckRangeAction` is still absent |
| StringBuilder rewriting | the 5 `StringBuilderRewriting` actions | ✅ `concat_to_stringbuilder`; the Append-chain rewrites the family also holds are absent |
| ternary/guard shapes | `IfToTernaryAction`, `IfToGuardClauseAction`, `InvertTernaryConditionAction` | ✅ `if_to_ternary` and `invert_ternary_condition`; `IfToGuardClauseAction` is still absent, because the tail branch has to be proven to be a tail |

## The 307 context actions

`Status` is filled only where this repository implements the action. A blank status is not a
claim of absence: it is an action that overlaps `simplify_code`, a refactoring tool or an
inspection fix, or one that is not implemented — the table is the census, and the four
families above are the places to look before assuming the latter.

| ReSharper action | Status |
|---|---|
| `AddAnonymousFieldNameAction` |  |
| `AddArgumentNameContextAction` | ✅ `context_action` → `add_argument_name` |
| `AddBracesAction` | ✅ `context_action` → `add_braces` |
| `AddDeconstructionPatternComponentNameAction` |  |
| `AddExceptionDocCommentAction` | ✅ `context_action` → `add_exception_docs` |
| `AddFormatItemAction` |  |
| `AddInheritdocAction` | ✅ `context_action` → `add_inheritdoc` |
| `AddPatternVariableAction` |  |
| `AddSecondAccessorAction` |  |
| `AddStringInterpolationArgumentAction` |  |
| `AddSwitchConditionComponentAction` |  |
| `AddSwitchSectionBracesAction` | ✅ `context_action` → `add_switch_section_braces` |
| `AddTupleComponentNameAction` | ✅ `context_action` → `add_tuple_component_name` |
| `AddUsingDeclarationAction` |  |
| `AllStringParameterIsNotNullOrEmptyCheckAction` | ✅ `context_action` → `check_string_not_null_or_empty` |
| `AllStringParameterIsNotNullOrWhitespaceCheckAction` | ✅ `context_action` → `check_string_not_null_or_whitespace` |
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
| `BodyExpressionToStatementAction` | ✅ `context_action` → `expression_body_to_block` |
| `BodyStatementToExpressionAction` | ✅ `context_action` → `block_body_to_expression` |
| `CSharpInitializeAutoPropertyAction` |  |
| `CSharpInitializeFieldAction` |  |
| `CSharpInitializeNonPrivateFieldAction` |  |
| `CSharpUseEmptyStringLiteralAction` | ✅ `context_action` → `empty_string_to_string_empty` |
| `CSharpUseStringEmptyAction` | ✅ `context_action` → `string_empty_to_empty_string` |
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
| `CheckArrayIndexValueAction` | ✅ `context_action` → `check_index_range` |
| `CheckCollectionIndexValueAction` |  |
| `CheckDictionaryKeyValueAction` | ✅ `context_action` → `check_dictionary_key` |
| `CheckParamNullAction` | ✅ `context_action` → `check_param_null` |
| `CheckReferenceNullAction` |  |
| `CheckSpanIndexValueAction` | ✅ `context_action` → `check_span_index` |
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
| `ConvertConcatenationToInterpolationAction` | ✅ `context_action` → `concat_to_interpolated` |
| `ConvertExtensionBlockToClassicExtensionMethodsAction` |  |
| `ConvertForEachCallToStatementAction` |  |
| `ConvertLiteralToBinAction` | ✅ `context_action` → `convert_literal_to_binary` |
| `ConvertLiteralToDecAction` | ✅ `context_action` → `convert_literal_to_decimal` |
| `ConvertLiteralToHexAction` | ✅ `context_action` → `convert_literal_to_hex` |
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
| `ConvertToDefaultExpressionAction` | ✅ `context_action` → `convert_to_default_expression` |
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
| `EnumParameterIsDefinedCheckAction` | ✅ `context_action` → `check_enum_defined` |
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
| `FlipRelationalExpressionAction` | ✅ `context_action` → `flip_relational_expression` |
| `ForeachToForAction` | ✅ `context_action` → `foreach_to_for` |
| `GenerateFlagPresenceCheckExtensionAction` |  |
| `GenerateTypePatternsAction` |  |
| `IfAndMergeAction` |  |
| `IfNotNullToConditionalAccessAction` |  |
| `IfToGuardClauseAction` |  |
| `IfToNullPropagationAction` |  |
| `IfToTernaryAction` | ✅ `context_action` → `if_to_ternary` |
| `ImplementEventAction` |  |
| `ImplementNotifyPropertyChangedAction` |  |
| `ImplicitToExplicitMemberAction` |  |
| `ImportStaticMembersAction` | ✅ `context_action` → `import_static_member` |
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
| `InterpolationToStringConcatAction` | ✅ `context_action` → `interpolated_to_concat` |
| `InterpolationToStringFormatAction` |  |
| `IntroduceParameterAction` |  |
| `IntroduceTypeAliasAction` |  |
| `IntroduceVariableAction` |  |
| `InvertIfAction` |  |
| `InvertTernaryConditionAction` | ✅ `context_action` → `invert_ternary_condition` |
| `InvokeAsStaticMethodAction` |  |
| `InvokeEventAction` |  |
| `IterateViaAwaitForeachAction` | ✅ `context_action` → `await_foreach` |
| `IterateViaForeachAction` |  |
| `Iterator2FunctionAction` |  |
| `JoinAttributesAction` |  |
| `JoinDeclarationAction` |  |
| `JoinDeconstructionDeclarationsAction` |  |
| `JoinNullCheckWithUsageAction` | ✅ `context_action` → `join_null_check_with_usage` |
| `JoinStringsAction` | ✅ `context_action` → `join_string` |
| `LambdaExpressionToAnonymousMethodAction` |  |
| `LambdaExpressionToLocalFunctionAction` |  |
| `LambdaExpressionToStatementAction` |  |
| `LambdaStatementToExpressionAction` |  |
| `LambdaToNamedMethodAction` |  |
| `LiteralToStringBuilderAction` |  |
| `LiveTemplateBasedIterateViaForeachAction` |  |
| `LocalFunctionToNamedMethodAction` |  |
| `MergeDeconstructedDeclarationAction` |  |
| `MergeIntoPatternAction` | ✅ `context_action` → `merge_patterns` |
| `MergeSequentialChecksAction` |  |
| `MethodToPropertyAction` |  |
| `MigrateToNullableEnableAction` | ✅ `context_action` → `migrate_to_nullable` |
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
| `QualifyStaticMembersAction` | ✅ `context_action` → `qualify_member` |
| `QueryToExtensionMethodAction` |  |
| `RecursivePatternToAccessExpressionAction` |  |
| `ReferenceEqualsToEqualityOperatorAction` |  |
| `ReinterpretStringLiteralAsInterpolationAction` |  |
| `RemoveArgumentNameAction` | ✅ `context_action` → `remove_argument_name` |
| `RemoveBracesAction` | ✅ `context_action` → `remove_braces` |
| `RemoveDeconstructionPatternComponentNameAction` |  |
| `RemoveDigitSeparatorsAction` | ✅ `context_action` → `remove_digit_separators` |
| `RemoveFormatItemAction` |  |
| `RemoveParamsModifierAction` | ✅ `context_action` → `remove_params_modifier` |
| `RemoveParenthesisAction` |  |
| `RemovePreprocessorParenthesesAction` |  |
| `RemoveRegionAction` | ✅ `context_action` → `remove_region` |
| `RemoveSwitchConditionComponentAction` |  |
| `RemoveTupleComponentNameAction` | ✅ `context_action` → `remove_tuple_component_name` |
| `RenameFileToMatchTypeNameAction` |  |
| `ReplaceLogicalExpressionAction` |  |
| `ReturnValueAction` |  |
| `ReverseAssignmentAction` |  |
| `RevertForLoopAction` |  |
| `SeparateIntegralLiteralDigitsAction` | ✅ `context_action` → `digit_separators` |
| `SeparateLocalFunctionsWithJumpStatementAction` |  |
| `SeparateRealLiteralDigitsAction` | ✅ `context_action` → `digit_separators` |
| `SetDisposeAnalysisModeAction` |  |
| `SetParamsModifierAction` | ✅ `context_action` → `set_params_modifier` |
| `SimplifyLinqExpressionUseMinByAndMaxByAction` |  |
| `SortHighlightingTypesAction` |  |
| `SortUsingDirectivesAction` |  |
| `SpecifyEnumValuesAction` |  |
| `SplitAndInIfAction` | ✅ `context_action` → `split_and_in_if` |
| `SplitAndInReturnAction` |  |
| `SplitAndPatternAction` |  |
| `SplitAttributesAction` |  |
| `SplitConditionalAccessAction` |  |
| `SplitDeclarationAction` | ✅ `context_action` → `split_declaration` |
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
| `StringParameterIsNotNullOrWhitespaceCheckAction` | ✅ `context_action` → `check_string_not_null_or_whitespace` |
| `StringToXElementCodeAction` |  |
| `SurroundWithFormatAction` |  |
| `SurroundWithQuotesAction` | ✅ `context_action` → `wrap_in_quotes` |
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
| `TypeToVarAction` | ✅ `context_action` → `use_var` |
| `UncommentAction` |  |
| `UseArrayCreationExpressionAction` |  |
| `UseAsAndCheckForNullAction` |  |
| `UseCollectionExpressionAction` |  |
| `UseConfigureAwaitFalseAction` | ✅ `context_action` → `use_configure_await_false` |
| `UseEnvironmentNewLineAction` | ✅ `context_action` → `use_environment_newline` |
| `UseExplicitArrayAction` |  |
| `UseExplicitDiscardDeclarationAction` |  |
| `UseExplicitTypeParametersAction` |  |
| `UseExplicitlyTypedNewAction` | ✅ `context_action` → `use_explicitly_typed_new` |
| `UseFormatStringAction` |  |
| `UseImplicitDiscardDeclarationAction` |  |
| `UseObjectInitializerAction` |  |
| `UsePositionalDeconstructionPatternAction` |  |
| `UseSeparateDeconstructionDeclarationsAction` |  |
| `UseSpanForStackAllocAction` |  |
| `UseStringInterpolationAction` |  |
| `UseTargetTypedNewAction` | ✅ `context_action` → `use_target_typed_new` |
| `UseTypeAliasAction` |  |
| `VarToTypeAction` | ✅ `context_action` → `use_explicit_type` |
| `WrapTernaryExpressionInInterpolationWithParenthesesAction` |  |
