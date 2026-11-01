# ReSharper — the whole inspection catalogue

Every configurable inspection id the local Rider installation declares, grouped by the languages the
rule applies to — including the groups that never apply to C# — each with the capability here that
answers it, or `—` when there is none. This is the full inventory behind `resharper_inspection_map`:
the `—` rows are the rules that would have to be reimplemented over Roslyn, the `✅` rows are the part
the curated map already answers.
The ✅ side of the same comparison is [ReSharper & CodeMaid — Feature Coverage](resharper-codemaid-coverage.md);
the gaps that are not catalogue ids are in [What is missing](gaps.md).

| | |
|---|---|
| Installation | JetBrains Rider 2026.2.1 |
| Read on | 2026-11-01 |
| Ids declared | 1,560 |
| Answered by a capability here | 49 |
| **With no capability** | **1,511** |

The ids are read from the installation's own metadata — each `ConfigurableSeverityHighlighting`
argument, linked to the `ElementProblemAnalyzer` that produces it — so the denominator is the engine's
catalogue, not an estimate. The ~2,500 inspections the coverage pages quote is JetBrains' own count
across the product's languages and releases; what one installation declares in its metadata today is
the number above. An id with `—` is a rule that would have to be reimplemented over Roslyn, not
registered. Groups that do not apply to C# are listed too: the catalogue is the catalogue.

A row whose description is `—` carries none in this installation's resources; the analyzer that
produces it is reported by `resharper_inspection_map`.

| Language group | Ids | Answered | With no capability |
|---|---:|---:|---:|
| `CSHARP` | 796 | 37 | 759 |
| `CPP` | 274 | 0 | 274 |
| `CSHARP,VBASIC` | 105 | 10 | 95 |
| `JAVA_SCRIPT` | 105 | 0 | 105 |
| `XAML` | 58 | 0 | 58 |
| `VBASIC` | 53 | 0 | 53 |
| `TYPE_SCRIPT` | 39 | 0 | 39 |
| `ASPX` | 23 | 0 | 23 |
| `CSS` | 22 | 0 | 22 |
| `RouteTemplate` | 17 | 0 | 17 |
| `HTML` | 15 | 0 | 15 |
| `Web.Config` | 13 | 0 | 13 |
| `MSBUILD_BUILD_SCRIPT` | 12 | 0 | 12 |
| `RESX` | 10 | 0 | 10 |
| `CSHARP,VBASIC,ASPX` | 3 | 0 | 3 |
| `Razor` | 3 | 0 | 3 |
| `Razor,CSHARP` | 2 | 0 | 2 |
| `XML,RESX,XAML,HTML,Razor,ASPX,ASXX` | 2 | 0 | 2 |
| `ASXX` | 1 | 0 | 1 |
| `Angular2HTML` | 1 | 0 | 1 |
| `Blazor` | 1 | 0 | 1 |
| `CSHARP,VBASIC,JAVA_SCRIPT,Razor,ASPX,ASXX,TYPE_SCRIPT,CPP` | 1 | 1 | 0 |
| `CSHARP,VBASIC,Razor,ASPX,ASXX,XAML,JAVA_SCRIPT,TYPE_SCRIPT,CPP` | 1 | 0 | 1 |
| `CSHARP,VBASIC,Razor,ASPX,ASXX,XML,XAML,JAVA_SCRIPT,TYPE_SCRIPT,RESX,CPP` | 1 | 0 | 1 |
| `CSHARP,VBASIC,XAML,JAVA_SCRIPT` | 1 | 1 | 0 |
| `REGULAR_EXPRESSION` | 1 | 0 | 1 |
| **Total** | **1,560** | **49** | **1,511** |

## C# — 796 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `AccessRightsInText` | Cannot access {0} '{1}' here | — |
| `AccessToStaticMemberViaDerivedType` | Access to a static member of a type via a derived type | — |
| `AddressOfMarshalByRefObject` | Passing '{0}' as '{1}' argument may cause a runtime exception because it is a field of a marshal-by-reference class | — |
| `AllUnderscoreLocalParameterName` | The '{0}' name is typically reserved for parameters without usages | — |
| `AnnotateCanBeNullParameter` | Annotate {0} '{1}' with [{2}] attribute | — |
| `AnnotateCanBeNullTypeMember` | Annotate {0} '{1}' with [{2}] attribute | — |
| `AnnotateNotNullParameter` | Annotate {0} '{1}' with [{2}] attribute | — |
| `AnnotateNotNullTypeMember` | Annotate {0} '{1}' with [{2}] attribute | — |
| `AnnotationConflictInHierarchy` | Nullness annotation conflicts with annotation in super type | — |
| `AnnotationRedundancyAtValueType` | — | — |
| `AnnotationRedundancyInHierarchy` | Base declaration has the same annotation | — |
| `AppendToCollectionExpression` | Append to collection expression | — |
| `ArgumentsStyleAnonymousFunction` | — | — |
| `ArgumentsStyleLiteral` | — | — |
| `ArgumentsStyleNamedExpression` | — | — |
| `ArgumentsStyleOther` | — | — |
| `ArgumentsStyleStringLiteral` | — | — |
| `ArrangeAccessorOwnerBody` | — | — |
| `ArrangeAccessorsOrder` | Arrange accessors order | — |
| `ArrangeAttributes` | Inconsistent attributes grouping style | — |
| `ArrangeConstructorOrDestructorBody` | — | — |
| `ArrangeDefaultValueWhenTypeEvident` | — | — |
| `ArrangeDefaultValueWhenTypeNotEvident` | — | — |
| `ArrangeEmptyString` | — | — |
| `ArrangeLocalFunctionBody` | — | — |
| `ArrangeMethodOrOperatorBody` | — | — |
| `ArrangeMissingParentheses` | Non-obvious precedence | — |
| `ArrangeModifiersOrder` | Inconsistent modifiers declaration order | ✅ |
| `ArrangeNamespaceBody` | — | — |
| `ArrangeNullCheckingPattern` | Inconsistent null checking pattern style | — |
| `ArrangeObjectCreationWhenTypeEvident` | — | — |
| `ArrangeObjectCreationWhenTypeNotEvident` | — | — |
| `ArrangeRedundantParentheses` | Redundant parentheses | — |
| `ArrangeStaticMemberQualifier` | Qualifier is redundant | — |
| `ArrangeThisQualifier` | Qualifier '{0}.' is redundant | ✅ |
| `ArrangeTrailingCommaInMultilineLists` | — | — |
| `ArrangeTrailingCommaInSinglelineLists` | — | — |
| `ArrangeTypeMemberModifiers` | Inconsistent modifiers style: {0} | — |
| `ArrangeTypeModifiers` | Inconsistent modifiers style: {0} | — |
| `ArrangeVarKeywordsInDeconstructingDeclaration` | — | — |
| `AssignNullToNotNullAttribute` | Possible '{0}' assignment to non-nullable entity | — |
| `AssignmentInConditionalExpression` | Assignment in conditional expression; did you mean to use '==' instead of '='? | — |
| `AssignmentInsteadOfDiscard` | The expression looks like a discard pattern but actually assigns a local variable '_' | — |
| `AssignmentIsFullyDiscarded` | Assignment results are fully discarded | — |
| `AsyncIteratorInvocationWithoutAwaitForeach` | Because the result of asynchronous iterator call is not being iterated, execution of the current method continues before the call is completed. Consider using the 'await foreach' over the result of the call. | — |
| `AsyncMethodWithoutAwait` | This async method lacks 'await' operators and will run synchronously. Consider using 'await' for non-blocking API calls, or remove the 'async' modifier to avoid generating a state machine for this method. | — |
| `AsyncVoidEventHandlerMethod` | — | — |
| `AsyncVoidLambda` | Avoid using 'async' lambda when delegate type returns 'void': any exceptions unhandled by the lambda might lead to the process crash | — |
| `AsyncVoidMethod` | — | — |
| `AsyncVoidMethodWithoutAwait` | This async method lacks 'await' operators and will run synchronously. Consider using 'await' for non-blocking API calls, or remove the 'async' modifier to avoid generating a state machine for this method. | — |
| `AsyncVoidThrowException` | Any exceptions unhandled by the 'async void' {0} might lead to the process crash | — |
| `AzureFunctions.TimerTriggerCronExpression` | — | — |
| `BaseMemberHasParams` | Base {0} '{1}' last parameter has 'params' modifier | — |
| `BaseMethodCallWithDefaultParameter` | Call to base member with implicit default parameters | — |
| `BaseObjectEqualsIsObjectEquals` | Call to 'base.Equals(...)' is reference equality | — |
| `BaseObjectGetHashCodeCallInGetHashCode` | Overridden '{0}()' calls base '{1}.{0}()' | — |
| `BitwiseOperatorOnEnumWithoutFlags` | Bitwise operation on enum that is not marked by the [Flags] attribute | — |
| `BothContextCallDeclaration.Global` | {0} is called from both CQRS contexts | — |
| `BothContextCallUsage.Global` | {0} is called from both CQRS contexts | — |
| `BuiltInTypeReferenceStyle` | Built-in type reference is inconsistent with code style settings | — |
| `BuiltInTypeReferenceStyleForMemberAccess` | Built-in type reference is inconsistent with code style settings | — |
| `ByRefArgumentIsVolatileField` | '{0}' argument is 'volatile' field, reference to it will not be treated as 'volatile' | — |
| `CSharp14OverloadResolutionWithSpanBreakingChange` | The resolution for this invocation has changed in C# 14 due to a breaking change in overload resolution with spans. | — |
| `CSharpBuildCSInvalidModuleName` | {0} is not a name of any module | — |
| `CSharpMissingPluginDependency` | Dependency for the plugin '{0}' is missing in the project file | — |
| `CSharpWarnings::CA2252` | — | — |
| `CSharpWarnings::CA2254` | The logging message template should not vary between calls to '{0}' | — |
| `CSharpWarnings::CS0078` | The 'l' suffix is easily confused with the digit '1', use 'L' for clarity | — |
| `CSharpWarnings::CS0108,CS0114` | The keyword '{0}' is required on '{1}' because it hides {2} '{3}' | — |
| `CSharpWarnings::CS0109` | The keyword '{0}' is redundant because {1} '{2}' hides nothing | — |
| `CSharpWarnings::CS0162` | Code is unreachable | — |
| `CSharpWarnings::CS0183` | The expression is always of the provided type | — |
| `CSharpWarnings::CS0184` | The given expression is never of the provided type | — |
| `CSharpWarnings::CS0197` | Passing '{0}' as '{1}' argument may cause a runtime exception because it is a field of a marshal-by-reference class | — |
| `CSharpWarnings::CS0252,CS0253` | Possible unintended reference comparison. To get a value comparison, cast the {0} hand side to type '{1}'. | — |
| `CSharpWarnings::CS0282` | There is no defined ordering between fields in multiple declarations of partial struct '{0}'. To specify an ordering, all instance fields must be in the same declaration. | — |
| `CSharpWarnings::CS0420` | '{0}' argument is 'volatile' field, reference to it will not be treated as 'volatile' | — |
| `CSharpWarnings::CS0458` | The result of the expression is always 'null' of type '{0}' | — |
| `CSharpWarnings::CS0464` | Comparing with null of '{0}' type always produces 'false' | — |
| `CSharpWarnings::CS0465` | Introducing a '{0}' method can interfere with destructor invocation. Did you intend to declare a destructor? | — |
| `CSharpWarnings::CS0469` | The 'goto case' value is not implicitly convertible to type '{0}' | — |
| `CSharpWarnings::CS0472` | The result of the expression is always '{0}' because a value of type '{1}' is never equal to 'null' of type '{2}' | — |
| `CSharpWarnings::CS0612` | — | — |
| `CSharpWarnings::CS0618` | — | — |
| `CSharpWarnings::CS0628` | Declaring new protected {0} '{1}' in sealed {2} is the same as declaring it as private | — |
| `CSharpWarnings::CS0642` | Possible mistaken empty statement | — |
| `CSharpWarnings::CS0652` | Comparison to integral constant is useless; the constant is outside the range of type '{0}' | — |
| `CSharpWarnings::CS0657` | '{0}' is not a valid attribute location for this declaration. All attributes in this block will be ignored. | — |
| `CSharpWarnings::CS0658` | '{0}' is not a recognized attribute location. All attributes in this block will be ignored. | — |
| `CSharpWarnings::CS0659` | '{0}' overrides '{1}.{2}({3} o)' but does not override '{1}.{4}()' | — |
| `CSharpWarnings::CS0660,CS0661` | '{0}' defines operator '==' or operator '!=' but does not override 'Object.Equals(object o)' and 'Object.GetHashCode()' | — |
| `CSharpWarnings::CS0665` | Assignment in conditional expression; did you mean to use '==' instead of '='? | — |
| `CSharpWarnings::CS0672` | {0} '{1}' overrides obsolete {2} '{3}'. Add the [{4}] attribute to '{5}'. | — |
| `CSharpWarnings::CS0675` | Bitwise-or operator used on a sign-extended operand; consider casting to a smaller unsigned type first | — |
| `CSharpWarnings::CS0693` | Type parameter '{0}' has the same name as the type parameter from outer {1} '{2}' | — |
| `CSharpWarnings::CS0728` | Possibly incorrect assignment to {0} '{1}', which is the argument to a 'using' or 'lock' statement. The 'Dispose' method call or unlocking will happen on the original value of the local. | — |
| `CSharpWarnings::CS0809` | Obsolete {0} '{1}' overrides non-obsolete {2} '{3}'. | — |
| `CSharpWarnings::CS1030` | — | — |
| `CSharpWarnings::CS1058` | A previous catch clause already catches all exceptions. All non-exceptions thrown will be wrapped in a System.Runtime.CompilerServices.RuntimeWrappedException. | — |
| `CSharpWarnings::CS1066` | The default value specified for parameter '{0}' will have no effect because it applies to a member that is used in contexts that do not allow optional arguments | — |
| `CSharpWarnings::CS1522` | Empty switch block | — |
| `CSharpWarnings::CS1570` | — | — |
| `CSharpWarnings::CS1571` | XML comment on '{0}' has a duplicate param tag for '{1}' | — |
| `CSharpWarnings::CS1572` | Cannot resolve parameter '{0}' | — |
| `CSharpWarnings::CS1573` | Parameter '{0}' has no matching param tag in the XML comment for {1} (but other parameters do) | — |
| `CSharpWarnings::CS1574` | Ambiguous reference '{0}' in documentation.{1}Candidates are:{2} | — |
| `CSharpWarnings::CS1574,CS1584,CS1581,CS1580` | Cannot resolve symbol '{0}' | — |
| `CSharpWarnings::CS1580` | Incorrect signature of '{0}' | — |
| `CSharpWarnings::CS1584` | Syntax error | — |
| `CSharpWarnings::CS1587` | XML comment is not placed on a valid language element | — |
| `CSharpWarnings::CS1589` | Unable to include XML fragment: {0} | — |
| `CSharpWarnings::CS1590` | Invalid XML include element: {0} | — |
| `CSharpWarnings::CS1591` | Missing XML comment for publicly visible type or member '{0}' | — |
| `CSharpWarnings::CS1592` | Badly formed XML in included comments file | — |
| `CSharpWarnings::CS1687` | The line number must be in range 1-16,707,565 to be correctly represented in the PDB | — |
| `CSharpWarnings::CS1710` | XML comment on '{0}' has a duplicate typeparam tag for '{1}' | — |
| `CSharpWarnings::CS1711` | Cannot resolve type parameter '{0}' | — |
| `CSharpWarnings::CS1712` | Type parameter '{0}' has no matching typeparam tag in the XML comment on '{1}' (but other type parameters do) | — |
| `CSharpWarnings::CS1717` | Assignment made to same variable; did you mean to assign something else? | — |
| `CSharpWarnings::CS1723` | XML comment has cref attribute that refers to a type parameter | — |
| `CSharpWarnings::CS1911` | Access to '{0}' through 'base' keyword from anonymous method, lambda expression, query expression, or iterator results in unverifiable code | — |
| `CSharpWarnings::CS1957` | {0} overrides '{1}'. There are multiple override candidates at run-time. The method that will be called depends on the implementation. | — |
| `CSharpWarnings::CS1981` | Using 'is' to test compatibility with 'dynamic' is essentially identical to testing compatibility with 'Object' and will succeed for all non-null values | — |
| `CSharpWarnings::CS1998` | This async method lacks 'await' operators and will run synchronously. Consider using the 'await' operator to await non-blocking API calls, or 'await Task.Run(...)' to do CPU-bound work on a background thread. | — |
| `CSharpWarnings::CS4014` | Because this call is not awaited, execution of the current method continues before the call is completed. Consider applying the 'await' operator to the result of the call. | — |
| `CSharpWarnings::CS4024` | The [{0}] attribute applied to the parameter '{1}' will have no effect because it applies to a member that is used in contexts that do not allow optional arguments | — |
| `CSharpWarnings::CS4025` | The [{0}] attribute applied to the parameter '{1}' will have no effect because it applies to a member that is used in contexts that do not allow optional arguments | — |
| `CSharpWarnings::CS4026` | The [{0}] attribute applied to the parameter '{1}' will have no effect because it applies to a member that is used in contexts that do not allow optional arguments | — |
| `CSharpWarnings::CS7022` | The '{0}' {1} will not be used as an entry point because compilation unit with top-level statements was found | — |
| `CSharpWarnings::CS7023` | The second operand of an 'is' or 'as' operator cannot be static type '{0}' | — |
| `CSharpWarnings::CS7080` | The [{0}] attribute applied to the parameter '{1}' will have no effect because it is overridden by [{2}] | — |
| `CSharpWarnings::CS7081` | The [{0}] attribute applied to the parameter '{1}' will have no effect because it is overridden by [{2}] | — |
| `CSharpWarnings::CS7082` | The [{0}] attribute applied to the parameter '{1}' will have no effect because it is overridden by [{2}] | — |
| `CSharpWarnings::CS7095` | Filter expression is a constant, consider removing the filter | — |
| `CSharpWarnings::CS8073` | The result of the expression is always '{0}' because a value of type '{1}' is never equal to 'null' of type '{2}' | — |
| `CSharpWarnings::CS8094` | Alignment value {0} has a magnitude greater than {1} and may result in a large formatted string | — |
| `CSharpWarnings::CS8123` | The tuple element name '{0}' is ignored because a different name or no name is specified by the target type '{1}' | — |
| `CSharpWarnings::CS8305` | — | — |
| `CSharpWarnings::CS8383` | The tuple element name '{0}' is ignored because a different name or no name is specified on the other side of the tuple == or != operator | — |
| `CSharpWarnings::CS8424` | The 'EnumeratorCancellation' attribute applied to parameter '{0}' has no effect. The attribute is only effective on a parameter of type 'CancellationToken' in an async-iterator method returning 'IAsyncEnumerable<>'. | — |
| `CSharpWarnings::CS8425` | Async-iterator '{0}' has one or more parameters of type 'CancellationToken' but none of them is decorated with the 'EnumeratorCancellation' attribute, so the cancellation token parameter from the generated 'IAsyncEnumerable<>.GetAsyncEnumerator' will be unconsumed | — |
| `CSharpWarnings::CS8500` | This takes the address of, gets the size of, or declares a pointer to a managed type ('{0}') | — |
| `CSharpWarnings::CS8509` | The 'switch' expression does not handle all possible inputs (it is not exhaustive). For example, the pattern '{0}' is not covered. | — |
| `CSharpWarnings::CS8519` | The expression never matches the provided pattern | — |
| `CSharpWarnings::CS8520` | The expression always matches the provided constant | — |
| `CSharpWarnings::CS8524` | The switch expression does not handle some values of its input type (it is not exhaustive) involving an unnamed enum value. For example, the pattern '{0}' is not covered. | — |
| `CSharpWarnings::CS8597` | Thrown value can be null | — |
| `CSharpWarnings::CS8600` | Converting null literal or possible null value into non-nullable type | — |
| `CSharpWarnings::CS8601` | Possible {0} reference assignment | — |
| `CSharpWarnings::CS8602` | Dereference of a possibly null reference | — |
| `CSharpWarnings::CS8603` | Possible {0} reference return | — |
| `CSharpWarnings::CS8604` | Possible '{0}' reference argument for parameter '{1}' in '{2}' | — |
| `CSharpWarnings::CS8605` | Unboxing a possibly null value | — |
| `CSharpWarnings::CS8607` | A possible null value cannot be used for a type marked with [NotNull] or [DisallowNull] | — |
| `CSharpWarnings::CS8608` | Nullability of reference types in {0} does not match overridden member '{1}' | — |
| `CSharpWarnings::CS8609` | Nullability of reference types in return type of {0} does not match overridden member '{1}' | — |
| `CSharpWarnings::CS8610` | Nullability of reference types in type of parameter '{0}' in {1} does not match overridden member '{2}' | — |
| `CSharpWarnings::CS8611` | Nullability of reference types in type of parameter '{0}' does not match between method declaration parts | — |
| `CSharpWarnings::CS8612` | Nullability of reference types in type does not match implicitly implemented member '{0}' | — |
| `CSharpWarnings::CS8613` | Nullability of reference types in return type does not match implicitly implemented member '{0}' | — |
| `CSharpWarnings::CS8614` | Nullability of reference types in type of parameter '{0}' does not match implicitly implemented member '{1}' | — |
| `CSharpWarnings::CS8615` | Nullability of reference types in type does not match implemented member '{0} | — |
| `CSharpWarnings::CS8616` | Nullability of reference types in return type does not match implemented member '{0}' | — |
| `CSharpWarnings::CS8617` | Nullability of reference types in type of parameter '{0}' does not match implemented member '{1}' | — |
| `CSharpWarnings::CS8618` | — | — |
| `CSharpWarnings::CS8619` | Nullability of reference types in value of type '{0}' does not match target type '{1}' | — |
| `CSharpWarnings::CS8620` | Argument of type '{0}' cannot be used for parameter '{2}' of type '{1}' in '{3}' because of differences in the nullability of reference types | — |
| `CSharpWarnings::CS8621` | Nullability of reference types in return type '{0}' does not match the return type '{1}' of target delegate '{2}' (possibly because of nullability attributes) | — |
| `CSharpWarnings::CS8622` | Nullability of reference types in type of parameter '{0} {1}' of '{2}' does not match the parameter type '{3}' of target delegate '{4}' (possibly because of nullability attributes) | — |
| `CSharpWarnings::CS8624` | Argument of type '{0}' cannot be used as an output of type '{1}' for parameter '{2}' in '{3}' because of differences in the nullability of reference types | — |
| `CSharpWarnings::CS8625` | Cannot convert null literal to non-nullable reference type | — |
| `CSharpWarnings::CS8629` | Nullable value type can be '{0}' | — |
| `CSharpWarnings::CS8631` | Nullability of type argument '{0}' must match constraint type '{1}' constraint in order to use it as parameter '{2}' | — |
| `CSharpWarnings::CS8632` | The annotation for nullable reference types should only be used in code within a '#nullable' annotations context | — |
| `CSharpWarnings::CS8633` | Nullability in constraints for type parameter '{0}' of method '{1}' doesn't match the constraints for type parameter 'T' of interface method '{2}'. Consider using an explicit interface implementation instead. | — |
| `CSharpWarnings::CS8634` | Nullability of type argument '{0}' must match '{1}' constraint in order to use it as parameter '{2}' | — |
| `CSharpWarnings::CS8643` | Nullability of reference types in explicit interface specifier does not match interface implemented by the type | — |
| `CSharpWarnings::CS8644` | '{0}' does not implement interface member '{1}'. Nullability of reference types in interface implemented by the base type does not match. | — |
| `CSharpWarnings::CS8645` | '{0}' is already listed in the interface list on type '{1}' with different nullability of reference types | — |
| `CSharpWarnings::CS8655` | The 'switch' expression does not handle some null inputs (it is not exhaustive). For example, the pattern '{0}' is not covered. | — |
| `CSharpWarnings::CS8656` | Call to non-readonly {0} from a 'readonly' member results in an implicit copy of 'this' | — |
| `CSharpWarnings::CS8667` | Partial method declarations of '{0}' have inconsistent nullability for type parameter '{1}' | — |
| `CSharpWarnings::CS8669` | The annotation for nullable reference types should only be used in code within a '#nullable' annotations context. Auto-generated code requires an explicit '#nullable' directive in source. | — |
| `CSharpWarnings::CS8670` | Object or collection initializer implicitly dereferences possibly '{0}' member '{1}.{2}' | — |
| `CSharpWarnings::CS8714` | Nullability of type argument '{0}' must match '{1}' constraint in order to use it as parameter '{2}' | — |
| `CSharpWarnings::CS8762` | — | — |
| `CSharpWarnings::CS8763` | A method marked [DoesNotReturn] should not return | — |
| `CSharpWarnings::CS8764` | Nullability of return type of {0} does not match overridden member '{1}' (possibly because of nullability attributes) | — |
| `CSharpWarnings::CS8765` | Nullability of type of parameter '{0}' in {1} does not match overridden member '{2}' (possibly because of nullability attributes) | — |
| `CSharpWarnings::CS8766` | Nullability of return type does not match implicitly implemented member '{0}' (possibly because of nullability attributes) | — |
| `CSharpWarnings::CS8767` | Nullability of type of parameter '{0}' does not match implicitly implemented member '{1}' (possibly because of nullability attributes) | — |
| `CSharpWarnings::CS8768` | Nullability of reference types in return type does not match implemented member '{0}' (possibly because of nullability attributes) | — |
| `CSharpWarnings::CS8769` | Nullability of type of parameter '{0}' does not match implemented member '{1}' (possibly because of nullability attributes) | — |
| `CSharpWarnings::CS8770` | — | — |
| `CSharpWarnings::CS8774` | — | — |
| `CSharpWarnings::CS8775` | — | — |
| `CSharpWarnings::CS8776` | — | — |
| `CSharpWarnings::CS8777` | — | — |
| `CSharpWarnings::CS8794` | An expression of type '{0}' always matches the provided pattern | — |
| `CSharpWarnings::CS8819` | Nullability of reference types in return type does not match between method declaration parts | — |
| `CSharpWarnings::CS8824` | Parameter '{0}' must have a non-{2} value when exiting because parameter '{1}' is non-{2} | — |
| `CSharpWarnings::CS8825` | Return value must be non-null because parameter '{0}' is non-null | — |
| `CSharpWarnings::CS8846` | The 'switch' expression does not handle all possible inputs (it is not exhaustive). For example, the pattern '{0}' is not covered. However, a pattern with a 'when' clause might successfully match this value. | — |
| `CSharpWarnings::CS8847` | The 'switch' expression does not handle some null inputs (it is not exhaustive). For example, the pattern '{0}' is not covered. However, a pattern with a 'when' clause might successfully match this value. | — |
| `CSharpWarnings::CS8851` | '{0}' defines 'Equals' but does not overrides 'GetHashCode' | — |
| `CSharpWarnings::CS8860` | Types and aliases should not be named 'record' | — |
| `CSharpWarnings::CS8892` | Method '{0}' will not be used as an entry point because a synchronous entry point '{1}' was found | — |
| `CSharpWarnings::CS8947` | Parameter '{0}' occurs after '{1}' in the parameter list, but is used as an argument for interpolated string handler conversions. This will require the caller to reorder parameters with named arguments at the call site. Consider putting the interpolated string handler parameter after all arguments involved. | — |
| `CSharpWarnings::CS8960` | The [{0}] attribute applied to the parameter '{1}' will have no effect because it is overridden by [{2}] | — |
| `CSharpWarnings::CS8961` | The [{0}] attribute applied to the parameter '{1}' will have no effect because it is overridden by [{2}] | — |
| `CSharpWarnings::CS8962` | The [{0}] attribute applied to the parameter '{1}' will have no effect because it is overridden by [{2}] | — |
| `CSharpWarnings::CS8963` | The [{0}] attribute applied to the parameter '{1}' will have no effect because it is applied with an invalid parameter name | — |
| `CSharpWarnings::CS8965` | The [{0}] attribute applied to the parameter '{1}' will have no effect because it is self-referential | — |
| `CSharpWarnings::CS8966` | The [{0}] attribute applied to the parameter '{1}' will have no effect because it applies to a member that is used in contexts that do not allow optional arguments | — |
| `CSharpWarnings::CS8971` | [{0}] has no effect when applied to lambda parameters and will be ignored at the call site | — |
| `CSharpWarnings::CS8974` | Converting method group '{0}' to non-delegate type '{1}'. Did you intend to invoke the method? | — |
| `CSharpWarnings::CS8981` | The type name '{0}' only contains lower-cased ASCII characters. Such names may become reserved for the language | — |
| `CSharpWarnings::CS9042` | Required member '{0}' should not be attributed with '{1}' unless the containing type is obsolete or all constructors are obsolete | — |
| `CSharpWarnings::CS9073` | The 'scoped' modifier of parameter '{0}' doesn't match target '{1}' | — |
| `CSharpWarnings::CS9074` | The 'scoped' modifier of parameter '{0}' doesn't match overridden or implemented {1} '{2}.{3}' | — |
| `CSharpWarnings::CS9080` | — | — |
| `CSharpWarnings::CS9081` | A result of a stackalloc expression in this context may be exposed outside of the containing method | — |
| `CSharpWarnings::CS9082` | Local '{0}' is returned by reference but was initialized to a value that cannot be returned by reference | — |
| `CSharpWarnings::CS9083` | A member of '{0}' is returned by reference but was initialized to a value that cannot be returned by reference | — |
| `CSharpWarnings::CS9084` | Struct member returns '{0}' or other instance members by reference | — |
| `CSharpWarnings::CS9085` | This ref-assigns '{1}' to '{0}' but '{1}' has a narrower escape scope than '{0}' | — |
| `CSharpWarnings::CS9086` | The branches of the ref conditional operator refer to variables with incompatible declaration scopes | — |
| `CSharpWarnings::CS9087` | This returns a {0} '{1}' by reference, but it is not a '{2}' parameter | — |
| `CSharpWarnings::CS9088` | This returns {0} '{1}' by reference but it is scoped to the current method | — |
| `CSharpWarnings::CS9089` | This returns by reference a member of {0} '{1}' that is not a '{2}' or '{3}' parameter | — |
| `CSharpWarnings::CS9090` | This returns by reference a member of {0} '{1}' that is scoped to the current method | — |
| `CSharpWarnings::CS9091` | This returns {0} '{1}' by reference but it is not a '{2}' local | — |
| `CSharpWarnings::CS9092` | This returns a member of {0} '{1}' by reference but it is not a '{2}' local | — |
| `CSharpWarnings::CS9093` | This ref-assigns '{1}' to '{0}' but '{2}' can only escape the current method through a return statement | — |
| `CSharpWarnings::CS9094` | This returns {0} '{1}' by reference through a '{2}' parameter; but it can only safely be returned in a '{3}' statement | — |
| `CSharpWarnings::CS9095` | This returns by reference a member of {0} '{1}' through a '{2}' parameter; but it can only safely be returned in a '{3}' statement | — |
| `CSharpWarnings::CS9097` | This ref-assigns '{1}' to '{0}' but '{1}' has a wider value escape scope than '{0}' allowing assignment through '{0}' of values with narrower escapes scopes than '{1}' | — |
| `CSharpWarnings::CS9099` | Parameter {0} has default value '{1}' in lambda but '{2}' in the target delegate type | — |
| `CSharpWarnings::CS9100` | Parameter {0} has '{1}' modifier in lambda but not in the target delegate type | — |
| `CSharpWarnings::CS9107` | Parameter '{0}' is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well | — |
| `CSharpWarnings::CS9123` | The '&' operator should not be used on parameters or local variables in async methods | — |
| `CSharpWarnings::CS9124` | Parameter '{0}' is captured into the state of the enclosing type and its value is also used to initialize a field, property, or event | — |
| `CSharpWarnings::CS9154` | Intercepting a call to '{0}' with interceptor '{1}', but the signatures do not match | — |
| `CSharpWarnings::CS9158` | Nullability of reference types in type of parameter '{0}' in interceptor method '{1}' doesn't match interceptable method '{2}' | — |
| `CSharpWarnings::CS9159` | Nullability of reference types in return type of interceptor method '{0}' doesn't match interceptable method '{1}' | — |
| `CSharpWarnings::CS9179` | Primary constructor parameter '{0}' is shadowed by a member from base | — |
| `CSharpWarnings::CS9181` | Inline array indexer will not be used for element access expression | — |
| `CSharpWarnings::CS9182` | Inline array '{0}' method will not be used for element access expression | — |
| `CSharpWarnings::CS9183` | Inline array conversion operator will not be used for conversion from expression of the declaring type | — |
| `CSharpWarnings::CS9184` | 'Inline arrays' language feature is not supported for an inline array type that is not valid as a type argument, or has element type that is not valid as a type argument | — |
| `CSharpWarnings::CS9191` | The 'ref' modifier for argument {0} corresponding to 'in' parameter is equivalent to 'in'. Consider using 'in' instead. | — |
| `CSharpWarnings::CS9192` | Argument {0} should be passed with 'ref' or 'in' keyword | — |
| `CSharpWarnings::CS9193` | Argument {0} should be a variable because it is passed to a 'ref readonly' parameter | — |
| `CSharpWarnings::CS9195` | Argument {0} should be passed with the 'in' keyword | — |
| `CSharpWarnings::CS9196` | Reference kind modifier of parameter '{0} {1} {2}' doesn't match the corresponding parameter '{3} {4} {5}' in overridden or implemented member | — |
| `CSharpWarnings::CS9197` | Reference kind modifier of parameter '{0} {1} {2}' doesn't match the corresponding parameter '{3} {4} {5}' in hidden member | — |
| `CSharpWarnings::CS9198` | Reference kind modifier of parameter '{0} {1} {2}' doesn't match the corresponding parameter '{3} {4} {5}' in target | — |
| `CSharpWarnings::CS9200` | A default value is specified for '{0}' parameter '{1}', but '{0}' should be used only for references. Consider declaring the parameter as '{2}' | — |
| `CSharpWarnings::CS9204` | — | — |
| `CSharpWarnings::CS9208` | Collection expression of type '{0}' may incur unexpected heap allocations. Consider explicitly creating an array, then converting to '{0}' to make the allocation explicit. | — |
| `CSharpWarnings::CS9209` | Collection expression of type '{0}' may incur unexpected heap allocations due to the use of '..' spreads. Consider explicitly creating an array, then converting to '{0}' to make the allocation explicit. | — |
| `CSharpWarnings::CS9216` | A value of type 'System.Threading.Lock' converted to a different type will use likely unintended monitor-based locking in 'lock' statement | — |
| `CSharpWarnings::CS9256` | Nullability of reference types in type of parameter '{0}' does not match between method declaration parts | — |
| `CSharpWarnings::CS9258` | In language version preview, the '{0}' keyword binds to a synthesized backing field for the property. To avoid generating a synthesized backing field, and to refer to the existing member, use '{1}.{2}' or '{3}' instead. | — |
| `CSharpWarnings::CS9264` | — | — |
| `CSharpWarnings::CS9266` | The '{0}' accessor of property '{1}' should use '{2}' because the other accessor is using it. | — |
| `CSharpWarnings::CS9377` | The 'unsafe' modifier does not have any effect here under the current memory safety rules | — |
| `CSharpWarnings::SYSLIB1014` | Non-existing argument in message template | — |
| `CSharpWarnings::WME006` | Namespace '{0}' should be the default namespace of this project | — |
| `CanReplaceCastWithLambdaReturnType` | Cast expression can be replaced with statically typed language construct | — |
| `CanReplaceCastWithShorterTypeArgument` | Cast expression can be replaced with simplified type arguments | — |
| `CanReplaceCastWithTypeArgument` | Cast expression can be replaced with statically typed language construct | — |
| `CanReplaceCastWithVariableType` | Cast expression can be replaced with statically typed language construct | — |
| `CanSimplifyDictionaryLookupWithTryAdd` | Dictionary lookup can be simplified with '{0}' | — |
| `CanSimplifyDictionaryLookupWithTryGetValue` | Dictionary lookup can be simplified with '{0}' | — |
| `CanSimplifyDictionaryRemovingWithSingleCall` | Item removal can be simplified with single 'Remove' | — |
| `CanSimplifyDictionaryTryGetValueWithGetValueOrDefault` | Dictionary lookup can be simplified with '{0}' | — |
| `CanSimplifyIsAssignableFrom` | Simplify '{0}()' invocation | — |
| `CanSimplifyIsInstanceOfType` | Simplify '{0}()' invocation | — |
| `CanSimplifySetAddingWithSingleCall` | Redundant '{0}' before '{1}' | — |
| `CanSimplifyStringEscapeSequence` | Escape sequence can be simplified | — |
| `CanUseHashCodeCombine` | Use 'HashCode.Combine' to calculate the hash code | — |
| `CannotApplyEqualityOperatorToType` | Cannot apply equality operator to type marked by 'CannotApplyEqualityOperatorAttribute' attribute | — |
| `CapturedPrimaryConstructorParameterIsMutable` | Primary constructor parameter '{0}' is captured and mutated by instance member(s), consider using explicit mutable field instead | — |
| `ChangeFieldTypeToSystemThreadingLock` | Consider changing the type of '{0}' field to '{1}' to express the field's intent | — |
| `ClassCannotBeInstantiated` | Class cannot be instantiated | — |
| `CoVariantArrayConversion` | Co-variant array conversion from {0} to {1} can cause run-time exception on write operation | — |
| `CollectionAccessAnnotationMissing` | — | — |
| `CommandInvasionDeclaration.Global` | CQRS context intersection. {0} is called from the Query context but belongs to the Command context. | — |
| `CommandInvasionUsage.Global` | CQRS context intersection. {0} is called from the Query context but belongs to the Command context. | — |
| `CompareNonConstrainedGenericWithNull` | Possible comparison of value type with '{0}' | — |
| `CompareOfFloatsByEqualityOperator` | Equality comparison of floating point numbers. {0} | — |
| `ComponentCanBeMarkedDeadlockSafe` | — | — |
| `ComponentCanBeMarkedDeadlockUnsafe` | — | — |
| `ComponentDoesNotMeetDeadlockSafetyRequirement` | — | — |
| `ComponentDoesNotMeetDeadlockSafetyRequirementFromDeadlockSafeFor` | — | — |
| `ComponentDoesNotMeetSyncAccessRequirement` | — | — |
| `ComponentNotAvailableForContainerFromDeadlockSafeFor` | — | — |
| `ComponentsFromContainerANotAvailableForContainerB` | — | — |
| `ConditionIsAlwaysTrueOrFalse` | Expression is always {0}{1} | ✅ |
| `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | Expression is always {0} according to nullable reference types' annotations | — |
| `ConditionalAccessQualifierIsNonNullableAccordingToAPIContract` | Conditional access qualifier expression is never null according to nullable reference types' annotations | — |
| `ConditionalTernaryEqualBranch` | '?:' expression has identical true and false branches | — |
| `ConflictCqrsAttribute` | — | — |
| `ConfusingCharAsIntegerInConstructor` | Char is possibly unintentionally used as integer where overload with string is available | — |
| `ConstantConditionalAccessQualifier` | Conditional access qualifier expression is known to be not null | — |
| `ConstantExpected` | {0} attribute is required due to method annotation of implemented or overriden method | — |
| `ConstantNullCoalescingCondition` | '??' left operand is never '{0}' | — |
| `ConstructorInitializerLoop` | Possible cyclic constructor call | — |
| `ConstructorParameterTypeHasUnknownDeadlockSafety` | — | — |
| `ConstructorParameterTypeIsDeadlockUnsafe` | — | — |
| `ConstructorWithMustDisposeResourceAttributeBaseIsNotAnnotated` | [MustDisposeResource] annotation is not inherited from the base constructor | — |
| `ContainerAnnotationRedundancy` | Applying container nullness annotation to non-container type is meaningless | — |
| `ContextValueIsProvided` | Use value of '{0}' type provided by {1} | — |
| `ContractAnnotationNotParsed` | — | — |
| `ConvertClosureToMethodGroup` | Convert into method group | — |
| `ConvertConditionalTernaryExpressionToSwitchExpression` | Convert chain of conditional ?: expressions into '{0}' expression | — |
| `ConvertConstructorToMemberInitializers` | Convert constructor into member initializers | — |
| `ConvertIfDoToWhile` | Convert into 'while' loop | — |
| `ConvertIfStatementToConditionalTernaryExpression` | Convert into '?:' expression | ✅ |
| `ConvertIfStatementToNullCoalescingAssignment` | Convert into '??=' | — |
| `ConvertIfStatementToNullCoalescingExpression` | Convert into '??' expression | — |
| `ConvertIfStatementToReturnStatement` | Convert into '{0}' statement | — |
| `ConvertIfStatementToSwitchStatement` | — | — |
| `ConvertIfToOrExpression` | Convert into '\|\|' expression | — |
| `ConvertNullableToShortForm` | Rewrite as '{0}' | — |
| `ConvertSwitchStatementToSwitchExpression` | Convert '{0}' statement to '{0}' expression | — |
| `ConvertToAutoProperty` | Convert into auto-property | ✅ |
| `ConvertToAutoPropertyWhenPossible` | Convert into auto-property | — |
| `ConvertToAutoPropertyWithPrivateSetter` | Convert into auto-property with private setter | — |
| `ConvertToCompoundAssignment` | Convert into compound assignment | ✅ |
| `ConvertToExtensionBlock` | Convert to extension block | — |
| `ConvertToLambdaExpression` | Use lambda expression | — |
| `ConvertToLocalFunction` | Use local function | — |
| `ConvertToNullCoalescingCompoundAssignment` | Convert into compound assignment | — |
| `ConvertToPrimaryConstructor` | Convert into primary constructor | ✅ |
| `ConvertToStaticClass` | Convert into {0} class | — |
| `ConvertToUsingDeclaration` | Convert into '{0}' declaration | ✅ |
| `ConvertTypeCheckPatternToNullCheck` | Use not null pattern instead of a type check succeeding on any not-null value | — |
| `ConvertTypeCheckToNullCheck` | Use null check instead of a type check succeeding on any not-null value | — |
| `CqrsDebug` | — | — |
| `DefaultValueAttributeForOptionalParameter` | Possibly misleading '{0}' usage to define optional parameter value. '{1}' must be used instead. | — |
| `DisposeOnUsingVariable` | Variable '{0}' is captured by the 'using' statement and will be automatically disposed when the execution gets out of the scope | — |
| `DoubleNegationInPattern` | Simplify negated pattern | — |
| `DoubleNegationOperator` | Double negation is meaningless | — |
| `DuplicateKeyCollectionInitialization` | Duplicate key '{0}' in the initialization of a '{1}' collection | — |
| `DuplicatedChainedIfBodies` | Duplicated 'if' branches | — |
| `DuplicatedSequentialIfBodies` | Duplicated 'if' branches | — |
| `DuplicatedStatements` | Duplicated statements | — |
| `DuplicatedSwitchExpressionArms` | Duplicated 'switch' arms | — |
| `DuplicatedSwitchSectionBodies` | Duplicated 'switch' branches | — |
| `EmptyConstructor` | Empty constructor is redundant. The compiler generates the same by default. | ✅ |
| `EmptyDestructor` | Empty destructor is redundant | — |
| `EmptyExtensionBlock` | Extension block is empty | — |
| `EmptyForStatement` | Empty '{0}' loop is redundant | — |
| `EmptyGeneralCatchClause` | Empty general catch clause suppresses any errors | — |
| `EmptyNamespace` | Empty namespace declaration is redundant | — |
| `EmptyRegion` | This region is empty | — |
| `EmptyStatement` | Empty statement is redundant | — |
| `EnforceDoWhileStatementBraces` | Inconsistent braces style: missing braces | — |
| `EnforceFixedStatementBraces` | Inconsistent braces style: missing braces | — |
| `EnforceForStatementBraces` | Inconsistent braces style: missing braces | — |
| `EnforceForeachStatementBraces` | Inconsistent braces style: missing braces | — |
| `EnforceIfStatementBraces` | Inconsistent braces style: missing braces | — |
| `EnforceLockStatementBraces` | Inconsistent braces style: missing braces | — |
| `EnforceUsingStatementBraces` | Inconsistent braces style: missing braces | — |
| `EnforceWhileStatementBraces` | Inconsistent braces style: missing braces | — |
| `EntityFramework.ClientSideDbFunctionCall` | Database function must not be called in non-database context | — |
| `EntityFramework.ModelValidation.CircularDependency` | — | — |
| `EntityFramework.ModelValidation.UnlimitedStringLength` | — | — |
| `EntityFramework.NPlusOne.IncompleteDataQuery` | Query can return incomplete data for related entities | — |
| `EntityFramework.NPlusOne.IncompleteDataUsage` | Usage of navigational property can return incomplete data | — |
| `EntityFramework.NPlusOne.Query` | Possible multiple queries to the database for related entities (N+1 problem) | — |
| `EntityFramework.NPlusOne.Usage` | Possible multiple queries to the database (N+1 problem) | — |
| `EntityFramework.UnsupportedServerSideFunctionCall` | Function is not convertible to SQL and must not be called in the database context | — |
| `EnumUnderlyingTypeIsInt` | '{0}' is default {1} governing type | — |
| `EnumerableSumInExplicitUncheckedContext` | 'Enumerable.Sum' invocation in explicit unchecked context | — |
| `EqualExpressionComparison` | Similar expressions comparison | — |
| `EventUnsubscriptionViaAnonymousDelegate` | Event unsubscription via anonymous delegate | — |
| `ExplicitCallerInfoArgument` | Explicit argument passed to parameter with caller info attribute | — |
| `ExpressionIsAlwaysNull` | Expression is always null | — |
| `ExtractCommonBranchingCode` | — | — |
| `ExtractCommonPropertyPattern` | Extract common property pattern '{0}' | — |
| `FieldHidesInterfacePropertyWithDefaultImplementation` | Field '{0}' hides property with default implementation in interface {1} | — |
| `ForCanBeConvertedToForeach` | '{0}' loop can be converted into '{1}' loop | — |
| `ForStatementConditionIsTrue` | '{0}' is redundant as '{1}'-statement condition | — |
| `ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator` | Loop can be converted into LINQ-expression but another '{0}' method will be used | — |
| `ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator` | Part of loop's body can be converted into LINQ-expression but another '{0}' method will be used | — |
| `FunctionComplexityOverflow` | {0} body is too complex to analyze, consider decomposing it | — |
| `FunctionRecursiveOnAllPaths` | — | — |
| `GCSuppressFinalizeForTypeWithoutDestructor` | '{0}.{1}' is invoked for type without destructor | — |
| `GenericEnumeratorNotDisposed` | — | — |
| `HeuristicUnreachableCode` | Code is heuristically unreachable | — |
| `ImmutableArrayWithCollectionInitializer` | Using a collection initializer with 'ImmutableArray<T>' leads to a runtime exception | — |
| `InParameterWithMustDisposeResourceAttribute` | Meaningless [MustDisposeResource] annotation for an input parameter | — |
| `InconsistentOrderOfLocks` | — | — |
| `InconsistentlySynchronizedField` | The field is sometimes used inside synchronized block and sometimes used without synchronization | — |
| `IncorrectConstantExpectedAnnotation` | The {0} and {1} values are inverted | — |
| `IndexingByInvalidRange` | Possible '{0}.{1}'. Start index must be less than or equal to end index. | — |
| `InheritdocConsiderUsage` | Use <inheritdoc /> on root level to inherit documentation from base candidate | — |
| `InheritdocInvalidUsage` | Usage of <inheritdoc /> is invalid: {0} | — |
| `InlineOutVariableDeclaration` | Inline '{0}' variable declaration | — |
| `InlineTemporaryVariable` | Inline temporary variable | — |
| `IntDivisionByZero` | Division by zero in at least one execution path | — |
| `IntVariableOverflow` | Possible overflow of '{0}' variable | — |
| `IntVariableOverflowInCheckedContext` | Possible overflow in '{0}' context | — |
| `IntVariableOverflowInUncheckedContext` | Possible overflow in '{0}' context | — |
| `InternalAttributeOnPublicApi` | — | — |
| `InternalOrPrivateMemberNotDocumented` | Missing XML comment for type or member '{0}' | — |
| `InterpolatedStringExpressionIsNotIFormattable` | Formatting is specified, but interpolated string expression is not '{0}' | — |
| `IntroduceOptionalParameters.Global` | Introduce optional parameter(s) for {0} '{1}' | — |
| `IntroduceOptionalParameters.Local` | Introduce optional parameter(s) for {0} '{1}' | — |
| `InvalidXmlDocComment` | — | — |
| `InvertIf` | Invert '{0}' statement to reduce nesting | ✅ |
| `InvocationIsSkipped` | {0} '{1}' invocation is skipped. Compiler will not generate invocation because the {2} is marked with [{3}] attribute and none of the following conditional symbols are defined: {4} | — |
| `InvokeAsExtensionMember` | — | — |
| `InvokeAsExtensionMemberFromSameClass` | Invoke as extension {0} | — |
| `IsExpressionAlwaysFalse` | The given expression is never of the provided type | — |
| `IsExpressionAlwaysTrue` | The expression is always of the provided type | — |
| `IteratorMethodResultIsIgnored` | Return value of iterator is not used | — |
| `IteratorNeverReturns` | Iterator never returns | — |
| `JoinDeclarationAndInitializer` | Join declaration and assignment | — |
| `JoinNullCheckWithUsage` | Join '{0}' check with assignment | — |
| `LambdaExpressionCanBeMadeStatic` | {0} can be made '{1}' | — |
| `LambdaExpressionMustBeStatic` | {0} must be '{1}' to avoid allocations | — |
| `LambdaShouldNotCaptureContext` | {0} must not have captures of the containing context | — |
| `LocalFunctionCanBeMadeStatic` | {0} '{1}' can be made {2} | — |
| `LocalFunctionHidesMethod` | Local function '{0}' hides {1} '{2}' | — |
| `LocalVariableHidesPrimaryConstructorParameter` | Local variable '{0}' hides primary constructor parameter '{1}' | — |
| `LoopCanBeConvertedToQuery` | Loop can be converted into LINQ-expression | ✅ |
| `LoopCanBePartlyConvertedToQuery` | Part of loop's body can be converted into LINQ-expression | — |
| `LoopVariableIsNeverChangedInsideLoop` | Loop control variable is never changed inside loop | ✅ |
| `MathAbsMethodIsRedundant` | {0}.{1}() argument is always non-negative | — |
| `MathClampMinGreaterThanMax` | Argument 'min' is always greater than argument 'max' | — |
| `MeaninglessDefaultParameterValue` | 'DefaultParameterValueAttribute' must be used in conjunction with 'OptionalAttribute' | — |
| `MemberCanBeFileLocal` | {0} '{1}' can be made file-local | — |
| `MemberHidesInterfaceMemberWithDefaultImplementation` | Non-public {0} '{1}' hides {0} with default implementation in interface {2} | — |
| `MemberHidesStaticFromOuterClass` | {0} '{1}' hides {2} from outer class | — |
| `MemberInitializerValueIgnored` | {0} initializer value ignored during initialization | — |
| `MergeAndPattern` | Merge '{0}' pattern operands | — |
| `MergeCastWithTypeCheck` | Merge cast with type check | — |
| `MergeConditionalExpression` | Merge conditional expression | — |
| `MergeIntoLogicalPattern` | Merge into logical pattern | — |
| `MergeIntoNegatedPattern` | Merge into pattern | — |
| `MergeIntoPattern` | Merge into pattern | ✅ |
| `MergeNestedPropertyPatterns` | Merge nested property patterns | — |
| `MergeSequentialChecks` | Merge sequential checks | — |
| `MethodHasAsyncOverload` | Method has '{0}' overload | — |
| `MethodHasAsyncOverloadWithCancellation` | Method has '{0}' overload with cancellation support | — |
| `MethodOverloadWithOptionalParameter` | Method with {0} parameter is hidden by overload | ✅ |
| `MethodSupportsCancellation` | Method has overload with cancellation support | — |
| `MisleadingBodyLikeStatement` | Statement can be confused with previous statement's body | — |
| `MoreSpecificForeachVariableTypeAvailable` | Variable can be declared with more specific type '{0}' | — |
| `MoveLocalFunctionAfterJumpStatement` | Put local function '{0}' after '{1}' | — |
| `MoveToExistingPositionalDeconstructionPattern` | Use positional deconstruction pattern | — |
| `MoveToExtensionBlock` | Move to extension block | — |
| `MoveVariableDeclarationInsideLoopCondition` | Variable '{0}' can be declared inside loop condition | — |
| `MultipleCqrsEntity` | Unable to identify CQRS entity. There are conflicts between its name, implemented interfaces, or attributes. | — |
| `MultipleNullableAttributesUsage` | Multiple nullness attribute usage | — |
| `MultipleOrderBy` | Multiple sequential '{0}' invocations are meaningless. Possibly you mean '{1}' instead. | — |
| `MultipleResolveCandidatesInText` | Ambiguous {0}.{1}Candidates are:{2} | — |
| `MustUseReturnValue` | — | — |
| `NUnit.AsyncMethodMustBeTask` | NUnit supports only async test methods that return Task or Task<T> | — |
| `NUnit.AttributeProducesTooManyTests` | The '{0}' attribute produces {1} tests, which exceeds the specified threshold ({2}).  With this many tests, NUnit test runner may fail with 'Out of Memory'. | — |
| `NUnit.AutoFixture.IncorrectArgumentType` | Argument value of type '{0}' is incorrect or is not assignable to parameter '{1}' of type '{2}' | — |
| `NUnit.AutoFixture.MissedTestOrTestFixtureAttribute` | Missing [Test] or [TestFixture] attribute, test method will not be run | — |
| `NUnit.AutoFixture.RedundantArgumentInInlineAutoDataAttribute` | Redundant argument in AutoFixture InlineAutoData attribute | — |
| `NUnit.DuplicateValues` | The value is duplicated. It produces unnecessary duplicate tests. | — |
| `NUnit.IgnoredParameterAttribute` | The attribute is ignored because another parameter attribute has already been specified | — |
| `NUnit.ImplicitUnspecifiedNullValues` | 'Sequential' attribute produces {0} tests but {1} values are provided by the attribute. The rest of the tests will be implicitly initialized with 'null' or default values. | — |
| `NUnit.IncorrectArgumentType` | Argument value of type '{0}' is incorrect or is not assignable to parameter '{1}' of type '{2}' | — |
| `NUnit.IncorrectExpectedResultType` | Value of type '{0}' is incorrect or is not assignable to the test method's expected result of type '{1}' | — |
| `NUnit.IncorrectRangeBounds` | Incorrect range bounds. 'to' must be greater than or equal to 'from', or there must be a negative step. | — |
| `NUnit.MethodWithParametersAndTestAttribute` | No values provided for all parameters of NUnit test method | — |
| `NUnit.MissingArgumentsInTestCaseAttribute` | Missing required {0} ({1}) in NUnit TestCase attribute | — |
| `NUnit.MissingCancelAfterAttribute` | Missing 'CancelAfter' attribute on test method declaration | — |
| `NUnit.NoValuesProvided` | No values provided for the parameter of type '{0}' in the {1} attribute. {2} | — |
| `NUnit.NonPublicMethodWithTestAttribute` | NUnit test methods should have public visibility | — |
| `NUnit.ParameterTypeIsNotCompatibleWithAttribute` | The type '{0}' provided in [{1}] is not compatible with the test parameter type '{2}' | — |
| `NUnit.RangeAttributeBoundsAreOutOfRange` | The value is out of range for the test parameter type '{0}' | — |
| `NUnit.RangeStepSignMismatch` | The sign of the 'step' parameter must be {0} to match the range values | — |
| `NUnit.RangeStepValueMustNotBeZero` | 'step' parameter value is incorrect, it must be non-zero | — |
| `NUnit.RangeToValueIsNotReachable` | With the current step, the test will reach {0} but not {1}. Check range and step values. | — |
| `NUnit.RedundantArgumentInTestCaseAttribute` | Redundant argument in NUnit TestCase attribute | — |
| `NUnit.RedundantArgumentInsteadOfExpectedResult` | Does last argument of NUnit TestCase attribute mean expected result? | — |
| `NUnit.RedundantExpectedResultInTestCaseAttribute` | NUnit TestCase attribute should not provide expected result for test method without return value | — |
| `NUnit.TestCaseAttributeRequiresExpectedResult` | NUnit {0} attribute should provide expected result for test method with return value | — |
| `NUnit.TestCaseResultPropertyDuplicatesExpectedResult` | Obsolete test case 'Result' property duplicates specified 'ExpectedResult' | — |
| `NUnit.TestCaseResultPropertyIsObsolete` | Test case Result property is obsolete since NUnit 2.6. Use ExpectedResult instead of it. | — |
| `NUnit.TestCaseSourceMustBeFieldPropertyMethod` | NUnit test case source '{0}' must be field, property, or method | — |
| `NUnit.TestCaseSourceMustBeStatic` | NUnit test case source field/property/method '{0}' must be static | — |
| `NUnit.TestCaseSourceShouldImplementIEnumerable` | — | — |
| `NegationOfRelationalPattern` | Simplify negated relational pattern | — |
| `NegativeEqualityExpression` | Simplify negative equality expression | — |
| `NegativeIndex` | Possible '{0}'. Index must be a non-negative integer. | — |
| `NestedStringInterpolation` | Merge string interpolation with containing one | — |
| `NonAtomicCompoundOperator` | Suspicious 'volatile' field usage: compound operation is not atomic. 'Interlocked' class can be used instead. | — |
| `NonConstantEqualityExpressionHasConstantResult` | — | — |
| `NonParsableElement` | Part of the code cannot be parsed because of deep nesting. Most of the features will not work inside it. | — |
| `NonReadonlyMemberInGetHashCode` | Non-readonly {0} referenced in '{1}()' | — |
| `NotDisposedResource` | — | — |
| `NotDisposedResourceIsReturned` | Return value should be disposed but {0} is not annotated with [{1}] | — |
| `NotDisposedResourceIsReturnedByProperty` | Return value of {0} must be disposed by the callee | — |
| `NotNullOrRequiredMemberIsNotInitialized` | — | — |
| `NotObservableAnnotationRedundancy` | Applying nullness annotation to declaration that cannot be directly referenced from other code is meaningless | — |
| `NotResolvedInText` | Cannot resolve symbol '{0}' | — |
| `NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract` | '??' left operand is never '{0}' according to nullable reference types' annotations | — |
| `NullableWarningSuppressionIsUsed` | A suppressed nullable warning or assuming a value is non-nullable might hide an underlying problem | — |
| `NullnessAnnotationConflictWithJetBrainsAnnotations` | Nullability of a member conflicts with annotations in hierarchy | — |
| `ObjectCreationAsStatement` | Possible unassigned object created by '{0}' expression | — |
| `OneWayOperationContractWithReturnType` | Method '{0}' is marked as [{1}] operation and therefore could not return values | — |
| `OperationContractWithoutServiceContract` | Method '{0}' is marked as [{1}] but containing type is not marked as [{2}] | — |
| `OperatorIsCanBeUsed` | Operator '{0}' can be used | — |
| `OperatorWithoutMatchedCheckedOperator` | The operator does not have a matching '{0}' operator while other operators do | — |
| `OptionalParameterHierarchyMismatch` | Parameter {0} in base {1} '{2}' has default value | — |
| `OptionalParameterRefOut` | A 'ref' or 'out' parameter cannot have a default value | — |
| `OutParameterWithHandlesResourceDisposalAttribute` | Meaningless [HandlesResourceDisposal] annotation for an output parameter | — |
| `ParameterHidesPrimaryConstructorParameter` | Parameter '{0}' hides primary constructor parameter '{1}' | — |
| `PartialMethodParameterNameMismatch` | Parameter name differs between 'partial' member declarations | — |
| `PartialMethodWithSinglePart` | Partial method without implementing declaration | — |
| `PartialTypeWithSinglePart` | Partial {0} with single part | — |
| `PatternAlwaysMatches` | The source expression always matches the provided pattern | — |
| `PatternIsAlwaysTrueOrFalse` | Pattern is always {0} | — |
| `PatternIsRedundant` | The pattern is redundant, it does not produce any runtime checks | — |
| `PatternNeverMatches` | The source expression never matches the provided pattern | — |
| `PlaceAssignmentExpressionIntoBlock` | Put assignment expression into block | — |
| `PolymorphicFieldLikeEventInvocation` | Invocation of polymorphic field-like event | — |
| `PossibleIntendedRethrow` | Exception rethrow possibly intended | — |
| `PossibleInterfaceMemberAmbiguity` | Possible ambiguity while accessing by this interface:{0} | — |
| `PossibleInvalidCastException` | Possible '{0}.{1}' | — |
| `PossibleInvalidCastExceptionInForeachLoop` | Possible '{0}.{1}' when casting from '{2}' type to '{3}' | — |
| `PossibleInvalidOperationException` | Possible '{0}.{1}' | — |
| `PossibleInvalidOperationExceptionCollectionWasModified` | Possible '{0}.{1}: Collection was modified' | — |
| `PossibleLossOfFraction` | Possible loss of fraction | — |
| `PossibleMistakenCallToGetType` | Possible mistaken '{0}()' invocation | — |
| `PossibleMistakenSystemTypeArgument` | Possible mistaken argument of type '{0}.{1}' | — |
| `PossibleMultipleWriteAccessInDoubleCheckLocking` | Possible incorrect implementation of Double-Check Locking. Possible multiple write access to checked field. | — |
| `PossibleNullReferenceException` | Possible '{0}.{1}' | — |
| `PossibleStructMemberModificationOfNonVariableStruct` | Assigned value can be lost if generic type parameter will be substituted with struct type. {0}Struct member modifications require accessed struct to be classified as a variable. | — |
| `PossibleUnintendedLinearSearchInSet` | Possibly unintended linear search in set | — |
| `PossibleUnintendedQueryableAsEnumerable` | '{0}' is possibly unintentionally used as '{1}' | — |
| `PossibleUnintendedReferenceComparison` | Possible unintended reference comparison. To get a value comparison, use '{0}' method. | — |
| `PossiblyImpureMethodCallOnReadonlyVariable` | Possibly impure struct method called on readonly variable: struct value always copied before invocation | — |
| `PossiblyMissingIndexerInitializerComma` | Assignment expression inside member initializer looks suspicious. Comma is probably missing. | — |
| `PossiblyMistakenUseOfCancellationToken` | Possibly mistaken use of {0} parameter '{1}' of {2}.{3} | — |
| `PossiblyMistakenUseOfInterpolatedStringInsert` | Interpolated string is used. Did you mean to use a format string placeholder instead? | — |
| `PossiblyUnintendedSideEffectsInsideConditionalInvocation` | Externally visible modification will be skipped when conditional invocation is not performed | — |
| `PossiblyUnintendedUsageParameterlessGetExpressionType` | Possible unintended usage of parameterless GetExpressionType() when an instance of IResolveContext is available in the context | — |
| `PreferConcreteValueOverDefault` | Use '{0}' instead of '{1}' | — |
| `PreferExplicitlyProvidedTupleComponentName` | Prefer explicit name '{0}' for this tuple component | — |
| `PrimaryConstructorParameterCaptureDisallowed` | Primary constructor parameter capturing is disallowed, use explicit field instead | — |
| `PrivateFieldCanBeConvertedToLocalVariable` | The field is always assigned before being used and can be converted into a local variable | — |
| `PropertyFieldKeywordIsNeverAssigned` | Backing field is used but never assigned | — |
| `PropertyFieldKeywordIsNeverUsed` | Backing field is assigned but never used | — |
| `PublicConstructorInAbstractClass` | Convert constructor to protected | — |
| `PureAttributeOnVoidMethod` | Annotating a 'void' method with [Pure] is meaningless | — |
| `QueryInvasionDeclaration.Global` | CQRS context intersection. {0} is called from the Command context but belongs to the Query context. | — |
| `QueryInvasionUsage.Global` | CQRS context intersection. {0} is called from the Command context but belongs to the Query context. | — |
| `RawStringCanBeSimplified` | Raw string can be simplified | — |
| `ReadAccessInDoubleCheckLocking` | Possible incorrect implementation of Double-Check Locking. Read access to checked field. | — |
| `RedundantAbstractModifier` | The keyword '{0}' is redundant | — |
| `RedundantAccessorBody` | Redundant accessor body | — |
| `RedundantAlwaysMatchSubpattern` | Subpattern that always matches is redundant | — |
| `RedundantAnonymousTypePropertyName` | Redundant explicit property name | — |
| `RedundantArgumentDefaultValue` | The parameter '{0}' has the same default value | ✅ |
| `RedundantArrayCreationExpression` | Redundant array creation expression | — |
| `RedundantAttributeParentheses` | Parentheses are redundant if attribute has no arguments | — |
| `RedundantAttributeSuffix` | Redundant '{0}' suffix | — |
| `RedundantAttributeUsageProperty` | Redundant [{0}] property assignment: {1} | — |
| `RedundantBaseConstructorCall` | Redundant base constructor call | ✅ |
| `RedundantBaseQualifier` | Qualifier '{0}.' is redundant | — |
| `RedundantBoolCompare` | — | — |
| `RedundantCallerArgumentExpressionDefaultValue` | The parameter '{0}' has 'CallerArgumentExpression' attribute applied, so argument could be omitted | — |
| `RedundantCapacityArgument` | Capacity argument is redundant because more elements are already provided | — |
| `RedundantCaseLabel` | Redundant '{0}' label | — |
| `RedundantCast` | Type cast is redundant | ✅ |
| `RedundantCatchClause` | Catch clause is redundant | — |
| `RedundantCheckBeforeAssignment` | Redundant check before assignment | ✅ |
| `RedundantCollectionCopyCall` | Redundant '{0}' call | — |
| `RedundantCollectionInitializerElementBraces` | Redundant braces | — |
| `RedundantConfigureAwait` | Redundant '{0}' can be removed | — |
| `RedundantCqrsAttribute` | Redundant CQRS attribute | — |
| `RedundantDeclarationSemicolon` | Semicolon after {0} declaration is redundant | — |
| `RedundantDefaultMemberInitializer` | Initializing {0} by default value is redundant | — |
| `RedundantDelegateCreation` | Redundant explicit delegate creation | — |
| `RedundantDictionaryContainsKeyBeforeAdding` | '{0}' call is redundant before adding the item to the dictionary | — |
| `RedundantDisableWarningComment` | There is no code inspection issue disabled by this comment or attribute | — |
| `RedundantDiscardDesignation` | Redundant discard designation | — |
| `RedundantEmptyFinallyBlock` | Redundant empty '{0}' block | — |
| `RedundantEmptyObjectCreationArgumentList` | Empty argument list is redundant | — |
| `RedundantEmptyObjectOrCollectionInitializer` | Empty object or collection initializer list is redundant | — |
| `RedundantEmptySwitchSection` | Redundant empty '{0}' section | — |
| `RedundantEmptyWithElement` | 'with()' is redundant | — |
| `RedundantEnumCaseLabelForDefaultSection` | Redundant '{0}' label | — |
| `RedundantEnumerableCastCall` | Redundant '{0}.{1}<T>' call | — |
| `RedundantExplicitArrayCreation` | Redundant explicit array type specification | — |
| `RedundantExplicitArraySize` | Redundant explicit array size specification | — |
| `RedundantExplicitNullableCreation` | Redundant explicit nullable type creation | — |
| `RedundantExplicitParamsArrayCreation` | Redundant explicit collection creation in argument of '{0}' parameter | — |
| `RedundantExplicitPositionalPropertyDeclaration` | Redundant explicit positional property declaration | — |
| `RedundantExplicitTupleComponentName` | Redundant explicit tuple component name | — |
| `RedundantExtendsListEntry` | — | — |
| `RedundantFixedPointerDeclaration` | Fixed pointer declaration is redundant because accessing elements of fixed size buffer does not require pinning | — |
| `RedundantIfElseBlock` | Redundant '{0}' block | ✅ |
| `RedundantImmediateDelegateInvocation` | Redundant immediate delegate invocation | — |
| `RedundantIsBeforeRelationalPattern` | Redundant '{0}' | — |
| `RedundantJumpStatement` | Redundant control flow jump statement | — |
| `RedundantLambdaParameterType` | Redundant lambda parameter explicit type specification | ✅ |
| `RedundantLambdaSignatureParentheses` | Redundant lambda signature parentheses | ✅ |
| `RedundantLogicalConditionalExpressionOperand` | Redundant operand in logical conditional expression | — |
| `RedundantNameQualifier` | Qualifier is redundant | ✅ |
| `RedundantNotNullConstraint` | The '{0}' constraint is redundant because type parameter '{1}' is constrained by non-nullable type '{2}' | — |
| `RedundantNullableAnnotationOnReferenceTypeConstraint` | The nullable annotation on '{0}?' constraint is redundant because type parameter '{1}' is constrained by non-nullable type '{2}' | — |
| `RedundantNullableAnnotationOnTypeConstraintHasNonNullableBaseType` | The nullable annotation on type constraint '{0}' is redundant because type parameter '{1}' is constrained by non-nullable type '{2}' | — |
| `RedundantNullableAnnotationOnTypeConstraintHasNonNullableTypeKind` | The nullable annotation on type constraint '{0}' is redundant because type parameter '{1}' has '{2}' constraint | — |
| `RedundantNullableDirective` | Redundant nullable directive | ✅ |
| `RedundantNullableFlowAttribute` | The nullability attribute has no effect and can be safely removed | — |
| `RedundantNullableTypeMark` | Type check for nullable type is equal to underlying type check | — |
| `RedundantNullnessAttributeWithNullableReferenceTypes` | — | — |
| `RedundantOverflowCheckingContext` | — | — |
| `RedundantOverload.Global` | Redundant method overload | ✅ |
| `RedundantOverload.Local` | Redundant method overload | — |
| `RedundantOverriddenMember` | Redundant {0} override | — |
| `RedundantParams` | '{0}' modifier is always ignored on overrides | — |
| `RedundantPartialMethodEmptyImplementation` | Empty implementation of 'partial void' method is redundant | — |
| `RedundantPatternParentheses` | Redundant parentheses | ✅ |
| `RedundantPropertyPatternClause` | Redundant property pattern clause | — |
| `RedundantQueryOrderByAscendingKeyword` | Redundant '{0}' clause '{1}' keyword | — |
| `RedundantRangeBound` | Redundant range bound | — |
| `RedundantReadonlyModifier` | Redundant '{0}' modifier: {1} | — |
| `RedundantRecordClassKeyword` | Redundant '{0}' keyword in record declaration | — |
| `RedundantScopedParameterModifier` | Redundant '{0}' modifier | — |
| `RedundantSetsRequiredMembersAttribute` | {0} attribute is redundant | — |
| `RedundantSpreadElement` | Redundant spread element | — |
| `RedundantStringFormatCall` | Redundant '{0}.{1}()' call | — |
| `RedundantStringInterpolation` | Redundant string interpolation | ✅ |
| `RedundantStringToCharArrayCall` | Redundant '{0}.{1}()' call | — |
| `RedundantSuppressNullableWarningExpression` | The nullable warning suppression expression is redundant | — |
| `RedundantSwitchExpressionArms` | — | — |
| `RedundantTernaryExpression` | Redundant conditional ternary expression usage | — |
| `RedundantToStringCall` | Redundant '{0}.{1}()' call | — |
| `RedundantToStringCallForValueType` | Redundant '{0}.{1}()' call for value type | — |
| `RedundantTypeArgumentsInsideNameof` | Type arguments inside 'nameof' are redundant | — |
| `RedundantTypeArgumentsOfMethod` | Type argument specification is redundant | — |
| `RedundantTypeCheckInPattern` | Pattern type specification is redundant | — |
| `RedundantTypeDeclarationBody` | Redundant empty {0} declaration body | — |
| `RedundantUnsafeContext` | 'unsafe' context is redundant | — |
| `RedundantUsingDirective` | Using directive is not required by the code and can be safely removed | ✅ |
| `RedundantUsingDirective.Global` | '{0}' directive is not required by the code and can be safely removed | — |
| `RedundantVerbatimPrefix` | Redundant verbatim prefix | — |
| `RedundantVerbatimStringPrefix` | Redundant verbatim string prefix | — |
| `RedundantVirtualModifier` | The keyword 'virtual' is redundant | — |
| `RedundantWithCancellation` | Redundant '{0}()' invocation | — |
| `RedundantWithExpression` | Empty 'with' expression is redundant | — |
| `ReferenceEqualsWithValueType` | '{0}.{1}' is always false because it is called with value type | — |
| `RemoveRedundantBraces` | Inconsistent braces style: redundant braces | — |
| `ReplaceAsyncWithTaskReturn` | Replace 'async' {0} with '{1}' value return | — |
| `ReplaceAutoPropertyWithComputedProperty` | Replace auto-property with computed property | — |
| `ReplaceConditionalExpressionWithNullCoalescing` | Conditional expression can be rewritten as null-coalescing | — |
| `ReplaceObjectPatternWithVarPattern` | Object pattern not performing any checks can be replaced with '{0}' pattern | — |
| `ReplaceSequenceEqualWithConstantPattern` | Replace 'SequenceEqual' with constant pattern | ✅ |
| `ReplaceSliceWithRangeIndexer` | Use range indexer | — |
| `ReplaceSubstringWithRangeIndexer` | Use range indexer | — |
| `ReplaceWithFieldKeyword` | Replace with '{0}' keyword | — |
| `ReplaceWithOfType` | Replace with '.{0}<{1}>()' | — |
| `ReplaceWithPrimaryConstructorParameter` | Replace with primary constructor parameter | — |
| `ReplaceWithStringIsNullOrEmpty` | Use '{0}.{1}' | ✅ |
| `RequiredAttributeMissing` | — | — |
| `RequiredBaseTypesConflict` | — | — |
| `RequiredBaseTypesDirectConflict` | — | — |
| `RequiredBaseTypesIsNotInherited` | — | — |
| `ReturnOfTaskProducedByUsingVariable` | Task being returned may need '{0}' object to complete, but it is captured by 'using' statement and will be automatically disposed before returning | — |
| `ReturnOfUsingVariable` | Variable '{0}' is captured by 'using' statement and will be automatically disposed before returning | — |
| `ReturnTypeCanBeNotNullable` | Return type of '{0}' can be made non-nullable | — |
| `ReturnValueOfPureMethodIsNotUsed` | Return value of pure method is not used | — |
| `RichTextDebugStringFormatting` | — | — |
| `SafeCastIsUsedAsTypeCheck` | Try cast and check for null can be replaced with a type check | — |
| `SealedMemberInSealedClass` | 'sealed' modifier is redundant because {0} '{1}' is sealed | — |
| `SensitiveDataApiUsageTag` | — | — |
| `SeparateControlTransferStatement` | Separate control transfer statement with empty line | — |
| `SeparateLocalFunctionsWithJumpStatement` | — | — |
| `ServiceContractWithoutOperations` | Interface '{0}' is marked as ServiceContract but does not contain any operation contracts | — |
| `ShebangDirectiveBadPlacement` | Shebang directive must be the first token in the file | — |
| `ShiftExpressionRealShiftCountIsZero` | Actual shift count equals zero. Left operand will remain unchanged. | — |
| `ShiftExpressionResultEqualsZero` | Constant shift expression with non-zero operands results in a zero value | — |
| `ShiftExpressionRightOperandNotEqualRealCount` | Suspicious shift count {0} for left operand of type {1}: actual shift count will be {2} | — |
| `ShiftExpressionZeroLeftOperand` | Shift expression with zero left operand equals zero | — |
| `ShortLivedHttpClient` | Short-lived 'HttpClient' is not recommended. Frequently creating 'HttpClient' instances can lead to socket exhaustion. Consider using 'IHttpClientFactory' or a long-lived (e.g., static) 'HttpClient' instance instead. | — |
| `SimilarAnonymousTypeNearby` | Similar anonymous type detected nearby. Are they the same? | — |
| `SimplifyConditionalTernaryExpression` | Simplify conditional ternary expression | — |
| `SimplifyLinqExpressionUseAll` | Simplify LINQ expression (use 'All') | — |
| `SimplifyLinqExpressionUseAny` | Simplify LINQ expression (use 'Any') | — |
| `SimplifyStringInterpolation` | Simplify string interpolation | — |
| `SpecifyACultureInStringConversionExplicitly` | Specify string culture explicitly | — |
| `SpecifyStringComparison` | Specify string comparison explicitly | — |
| `SpinLockInReadonlyField` | SpinLock should not be stored in readonly field | — |
| `StackAllocInsideLoop` | Memory stack allocation inside a loop can cause a temporary memory leak because the allocated memory is freed only when the method returns | — |
| `StaticMemberInGenericType` | Static {0} in generic type | — |
| `StaticMemberInitializerReferesToMemberBelow` | Static member initializer refers to static member below or in other type part | — |
| `StaticProblemInText` | Cannot access static {0} '{1}' in non-static context | — |
| `StreamReadReturnValueIgnored` | Return value of '{0}()' is ignored. Use '{1}()' to ensure the number of bytes read from the stream is equal to the expected value. | — |
| `StringLiteralAsInterpolationArgument` | Merge string literal with containing interpolation | ✅ |
| `StringSpanComparison` | Possibly incorrect string comparison: spans are only equal when pointing to the same memory location. Do you want to compare the span contents instead? | — |
| `SuggestDiscardDeclarationVarStyle` | — | — |
| `SuggestVarOrType_BuiltInTypes` | Use {0} (built-in types) | ✅ |
| `SuggestVarOrType_DeconstructionDeclarations` | Use {0} for each variable | — |
| `SuggestVarOrType_Elsewhere` | Use {0} (elsewhere) | ✅ |
| `SuggestVarOrType_SimpleTypes` | Use {0} (simple types) | ✅ |
| `SuppressNullableWarningExpressionAsInvertedIsExpression` | Null suppression operator might be confused with an inverted '{0}' expression | — |
| `SuspiciousLockOverSynchronizationPrimitive` | Locking over instance of a synchronization primitive; did you mean to use '{0}' members instead? | — |
| `SuspiciousMathSignMethod` | {0}.{1}() method always gives the same result ({2}) | — |
| `SuspiciousParameterNameInArgumentNullException` | The argument '{0}' does not match the parameter '{1}' that was checked | — |
| `SwapViaDeconstruction` | Swap via deconstruction | — |
| `SwitchExpressionHandlesSomeKnownEnumValuesWithExceptionInDefault` | — | — |
| `SwitchStatementHandlesSomeKnownEnumValuesWithDefault` | — | — |
| `SwitchStatementMissingSomeEnumCasesNoDefault` | — | — |
| `TabsAreDisallowed` | Tabulation characters are not allowed | — |
| `TailRecursiveCall` | Tail recursive call can be replaced with loop | — |
| `ThreadStaticAtInstanceField` | [ThreadStatic] does not work with {0} | — |
| `ThreadStaticFieldHasInitializer` | Thread static {0} has initializer: it will be evaluated only once by the first thread (while invoking static constructor of the containing type), other threads will observe default value of the {0} | — |
| `TooWideLocalVariableScope` | {0} '{1}' can be declared in inner scope | — |
| `TreeNodeEnumerableCanBeUsed` | — | — |
| `TreeNodeEnumerableMemberCanBeUsed` | — | — |
| `TryCastAlwaysSucceeds` | Safe cast expression always succeeds, consider direct cast instead | — |
| `TryStatementsCanBeMerged` | try-catch and try-finally statements can be merged | — |
| `TypeParameterCanBeVariant` | The type parameter '{0}' could be declared as {1} | — |
| `TypeParameterEqualityUsage` | — | — |
| `UnnecessaryWhitespace` | Unnecessary whitespace | — |
| `UnreachableSwitchArmDueToIntegerAnalysis` | Case is heuristically unreachable | — |
| `UnreachableSwitchCaseDueToIntegerAnalysis` | Case is heuristically unreachable | — |
| `UnsafeConstructorCanBeMarkedWithNotUsedByComponentContainer` | — | — |
| `UnsupportedRequiredBaseType` | BaseTypeRequired attribute supports only classes and interfaces | — |
| `UnusedAnonymousMethodSignature` | Specifying a signature is not required because none of its parameters are used in the body | — |
| `UnusedLocalFunction` | {0} '{1}' is never used | — |
| `UnusedLocalFunction.Compiler` | {0} '{1}' is never used | — |
| `UnusedLocalFunctionReturnValue` | Local function '{0}' return value is never used | — |
| `UnusedNullableDirective` | Unused nullable directive | — |
| `UnusedTupleComponentInReturnValue` | Component of the return tuple is never used | — |
| `UsageOfDefaultStructEquality` | Struct '{0}' is checked for equality using an inefficient runtime-provided implementation | — |
| `UseArgumentExceptionThrowIfMethod` | Use '{0}.{1}' | — |
| `UseArrayEmptyMethod` | Use '{0}.{1}<{2}>()' to avoid array allocation | — |
| `UseAwaitUsing` | Use '{0}' | — |
| `UseCancellationTokenForIAsyncEnumerable` | Use cancellation token | — |
| `UseCollectionCountProperty` | Replace 'Enumerable.Count()' invocation with collection count property access | — |
| `UseCollectionExpression` | Use collection expression | ✅ |
| `UseConfigureAwaitFalse` | Missing '.{0}({1})' in library code | — |
| `UseConfigureAwaitFalseForAsyncDisposable` | Missing '.{0}({1})' for async disposable in library code | — |
| `UseDeconstruction` | Use deconstruction | ✅ |
| `UseDiscardAssignment` | Use discard assignment | — |
| `UseEmptyTypesField` | Use 'Type.EmptyTypes' | — |
| `UseEventArgsEmptyField` | Use 'EventArgs.Empty' | — |
| `UseIndexFromEndExpression` | Use index from end expression | — |
| `UseIndexedProperty` | Use indexed property | — |
| `UseNameOfInsteadOfToString` | Use '{0}' expression to reference '{1}.{2}' name | — |
| `UseNameOfInsteadOfTypeOf` | Use '{0}' expression to reference {1} '{2}' name | — |
| `UseNameofExpression` | Use '{0}' expression to reference {1} '{2}' name | ✅ |
| `UseNameofExpressionForPartOfTheString` | Use '{0}' expression to reference {1} '{2}' name | — |
| `UseNameofForDependencyProperty` | Use 'nameof' expression when registering a DependencyProperty | — |
| `UseNegatedPatternInIsExpression` | Use negated pattern | — |
| `UseNegatedPatternMatching` | Use pattern matching | — |
| `UseNullPropagation` | Use null propagation | ✅ |
| `UseNullableAnnotationInsteadOfAttribute` | Use nullable annotation instead of an attribute | — |
| `UseNullableAttributesSupportedByCompiler` | Use compiler-supported nullable attributes | — |
| `UseNullableReferenceTypesAnnotationSyntax` | Use type annotation syntax | — |
| `UseObjectOrCollectionInitializer` | Use object initializer | — |
| `UsePatternMatching` | Use pattern matching | — |
| `UsePositionalDeconstructionPattern` | Use positional deconstruction pattern | — |
| `UseRawString` | Use raw string | — |
| `UseSwitchCasePatternVariable` | Merge cast with type pattern | — |
| `UseSymbolAlias` | Use {0} alias '{1}' | — |
| `UseThrowIfNullMethod` | Use 'ArgumentNullException.ThrowIfNull' | — |
| `UseUnsignedRightShiftOperator` | Use unsigned right shift operator '>>>' | — |
| `UseUtf8StringLiteral` | Byte array can be converted to a UTF-8 string literal | — |
| `UseVerbatimString` | Use verbatim string | — |
| `UseWithExpressionToCopyAnonymousObject` | Use '{0}' expression to copy anonymous object | — |
| `UseWithExpressionToCopyRecord` | Use '{0}' expression to copy record | — |
| `UseWithExpressionToCopyStruct` | Use '{0}' expression to copy struct | — |
| `UseWithExpressionToCopyTuple` | Use '{0}' expression to copy tuple | — |
| `UselessBinaryOperation` | {0} in every execution path, which is useless | — |
| `UselessComparisonToIntegralConstant` | Comparison to integral constant is useless; the constant is outside the range of type '{0}' | — |
| `UsingStatementResourceInitialization` | Initialize object properties inside the 'using' statement to ensure that the object is disposed if an exception is thrown during initialization | — |
| `UsingStatementResourceInitializationExpression` | Calculate object properties before the 'using' statement to ensure that no exception is thrown during initialization, which leaves the object not disposed | — |
| `ValueParameterNotUsed` | '{0}' parameter is not used | — |
| `ValueRangeAttributeViolation` | Possible violation of [{0}]/[{1}] attribute | — |
| `ValueTupleHashCodeIgnoresElements` | This value tuple contains {0} elements, but its hash code is calculated using only the last 8; the first {1} element(s) are ignored | — |
| `VariableCanBeNotNullable` | '{0}' can be declared as non-nullable | — |
| `VariableHidesOuterVariable` | {0} '{1}' hides outer {2} with the same name | — |
| `VariableLengthStringHexEscapeSequence` | Avoid mixing of variable-length escape sequences and text | — |
| `VirtualMemberCallInConstructor` | Virtual member call in constructor | — |
| `VoidMethodWithMustDisposeResourceAttribute` | Annotating a 'void' method with [MustDisposeResource] is meaningless | — |
| `VoidMethodWithMustUseReturnValueAttribute` | Annotating a 'void' method with [MustUseReturnValue] is meaningless | — |
| `VulnerableApi` | — | — |
| `WithExpressionInsteadOfInitializer` | 'with' expression is used instead of object initializer | — |
| `WithExpressionModifiesAllMembers` | 'with' expression modifies all accessible instance members | — |
| `Xunit.XunitTestWithConsoleOutput` | Xunit tests should use ITestOutputHelper instead of System.Console | — |
| `ZeroIndexFromEnd` | Index from end must be greater than zero; use '^{0}' to refer to the last element | — |

## CPP — 274 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `CDeclarationWithImplicitIntType` | Type-specifier missing, defaults to 'int' | — |
| `ConstevalIfIsAlwaysConstant` | consteval if is always true in an immediate context | — |
| `CppAbstractClassWithoutSpecifier` | — | — |
| `CppAbstractFinalClass` | Abstract {0} is marked '{1}' | — |
| `CppAbstractVirtualFunctionCallInCtor` | Call to a virtual function inside a {0} will result in a pure virtual function call | — |
| `CppAccessSpecifierWithNoDeclarations` | Access specifier does not affect any declaration | — |
| `CppAssignedValueIsNeverUsed` | Assigned value is never used | — |
| `CppAwaiterTypeIsNotClass` | — | — |
| `CppBooleanIncrementExpression` | Incrementing expression of type bool is deprecated in C++11 and removed in C++17 | — |
| `CppBoostFormatBadCode` | Incorrect format directive: '{0}' | — |
| `CppBoostFormatLegacyCode` | Format directive '{0}' is outdated in boost::format | — |
| `CppBoostFormatMixedArgs` | Positional and non-positional arguments in the same boost::format call | — |
| `CppBoostFormatTooFewArgs` | Format directive '{0}' does not have a corresponding argument | — |
| `CppBoostFormatTooManyArgs` | No format directives refer to the argument #{0} | — |
| `CppBoundToDelegateMethodIsNotMarkedAsUFunction` | Method '{0}::{1}' is not marked with UFUNCTION macro | — |
| `CppCStyleCast` | C-style cast is used instead of a C++ cast | — |
| `CppCVQualifierCanNotBeAppliedToReference` | '{0}' qualifier cannot be applied to reference | — |
| `CppClangTidyHighlighting` | — | — |
| `CppClassCanBeFinal` | {0} can be made {1} | — |
| `CppClassIsIncomplete` | — | — |
| `CppClassNeedsConstructorBecauseOfUninitializedMember` | — | — |
| `CppClassNeverUsed` | {0} is never used | — |
| `CppCompileTimeConstantCanBeReplacedWithBooleanConstant` | Expression is converted to bool and can be replaced with '{0}' | — |
| `CppConceptNeverUsed` | {0} is never used | — |
| `CppConditionalExpressionCanBeSimplified` | Conditional expression can be simplified | — |
| `CppConstParameterInDeclaration` | Const-qualified parameter in a function declaration | — |
| `CppConstValueFunctionReturnType` | Function returns by const value. Consider returning by non-const value instead. | — |
| `CppConstexprIfDiscardedBranch` | Discarded 'if constexpr' branch | — |
| `CppCoroutineCallResolveError` | — | — |
| `CppDFAArrayIndexOutOfBounds` | — | — |
| `CppDFAConstantConditions` | — | — |
| `CppDFAConstantFunctionResult` | — | — |
| `CppDFAConstantParameter` | — | — |
| `CppDFADeletedPointer` | — | — |
| `CppDFAEndlessLoop` | Endless loop | — |
| `CppDFAInfiniteRecursion` | Infinite recursion | — |
| `CppDFAInvalidatedMemory` | — | — |
| `CppDFALocalValueEscapesFunction` | — | — |
| `CppDFALocalValueEscapesScope` | — | — |
| `CppDFALoopConditionNotUpdated` | — | — |
| `CppDFAMemoryLeak` | — | — |
| `CppDFANotInitializedField` | — | — |
| `CppDFANullDereference` | — | — |
| `CppDFATimeOver` | The file is too complex to perform the data-flow analysis | — |
| `CppDFAUnreachableCode` | Unreachable code | — |
| `CppDFAUnreachableFunctionCall` | — | — |
| `CppDFAUnreadVariable` | — | — |
| `CppDFAUnusedValue` | The value is never used | — |
| `CppDeclarationHidesLocal` | Declaration of '{0}' hides previous local declaration | — |
| `CppDeclarationHidesUncapturedLocal` | Declaration of '{0}' inside a lambda hides a previous uncaptured local declaration | — |
| `CppDeclarationSpecifierWithoutDeclarators` | '{0}' specifier is ignored when there are no declarators | — |
| `CppDeclaratorDisambiguatedAsFunction` | Declarator is disambiguated as a function declaration. Make sure a variable definition is not intended instead. | — |
| `CppDeclaratorNeverUsed` | {0} is never used{1} | — |
| `CppDeclaratorUsedBeforeInitialization` | {0} '{1}' used without being initialized | — |
| `CppDefaultCaseNotHandledInSwitchStatement` | Default case is not handled in the switch statement | — |
| `CppDefaultInitializationWithNoUserConstructor` | — | — |
| `CppDefaultIsUsedAsIdentifier` | Keyword 'default' is used as identifier | — |
| `CppDefaultedSpecialMemberFunctionIsImplicitlyDeleted` | — | — |
| `CppDefinitionsOrder` | Order of definitions does not match the order of declaration | — |
| `CppDeletingVoidPointer` | Deleting a void pointer is undefined behavior | — |
| `CppDependentTemplateWithoutTemplateKeyword` | Dependent template requires using 'template' keyword | — |
| `CppDependentTypeWithoutTypenameKeyword` | Dependent type requires using 'typename' keyword | — |
| `CppDeprecatedEntity` | {0} '{1}' is deprecated | — |
| `CppDeprecatedOverridenMethod` | — | — |
| `CppDeprecatedRegisterStorageClassSpecifier` | The 'register' storage class specifier is deprecated in C++11 and removed in C++17 | — |
| `CppDereferenceOperatorLimitExceeded` | Use of 'operator ->' produced too large sequence of calls, limit on the number of iterations exceeded | — |
| `CppDiscardedPostfixOperatorResult` | Result of a postfix operator is discarded, consider replacing it with a prefix operator | — |
| `CppDoxygenSyntaxError` | {0} | — |
| `CppDoxygenUndocumentedParameter` | Missing function parameter description for the parameter '{0}' | — |
| `CppDoxygenUnresolvedReference` | Cannot resolve symbol '{0}' | — |
| `CppEmptyDeclaration` | Declaration does not declare anything | — |
| `CppEnforceCVQualifiersOrder` | Inconsistent order of cv-qualifiers | — |
| `CppEnforceCVQualifiersPlacement` | Inconsistent placement of cv-qualifiers | — |
| `CppEnforceDoStatementBraces` | Inconsistent braces style: missing braces | — |
| `CppEnforceForStatementBraces` | Inconsistent braces style: missing braces | — |
| `CppEnforceFunctionDeclarationStyle` | Function declaration uses regular return type syntax, but the syntax style requires trailing return type | — |
| `CppEnforceIfStatementBraces` | Inconsistent braces style: missing braces | — |
| `CppEnforceNestedNamespacesStyle` | Use C++17 nested namespace definition | — |
| `CppEnforceOverridingDestructorStyle` | Overriding {0} does not have an '{1}' specifier | — |
| `CppEnforceOverridingFunctionStyle` | Overriding {0} does not have {1} specifier | — |
| `CppEnforceTypeAliasCodeStyle` | Use a type alias instead of a typedef | — |
| `CppEnforceWhileStatementBraces` | Inconsistent braces style: missing braces | — |
| `CppEntityAssignedButNoRead` | {0} is only assigned but never accessed | — |
| `CppEntityUsedOnlyInUnevaluatedContext` | {0} is used only in unevaluated context | — |
| `CppEnumeratorNeverUsed` | Enumerator '{0}' is never used | — |
| `CppEqualOperandsInBinaryExpression` | Binary operator '{0}' acts on equal operands | — |
| `CppExplicitSpecializationInNonNamespaceScope` | Explicit specialization of {0} in non-namespace scope | — |
| `CppExpressionWithoutSideEffects` | Expression result is not used | — |
| `CppFinalFunctionInFinalClass` | Redundant '{0}' specifier on a function in a {1} class | — |
| `CppFinalNonOverridingVirtualFunction` | Non-overriding virtual function is marked '{0}' | — |
| `CppForLoopCanBeReplacedWithWhile` | for-loop can be replaced with while-loop | — |
| `CppForwardEnumDeclarationWithoutUnderlyingType` | ISO C++ forbids forward enum declarations without underlying type | — |
| `CppFunctionDoesntReturnValue` | No return statement in a function returning non-void | — |
| `CppFunctionIsNotImplemented` | {0} is not implemented | — |
| `CppFunctionResultShouldBeUsed` | Function result of type '{0}' should be used | — |
| `CppFunctionalStyleCast` | Functional-style cast is used instead of a C++ cast | — |
| `CppHeaderHasBeenAlreadyIncluded` | Guarded header has already been included | — |
| `CppHiddenFunction` | Non-virtual {0} is hidden in derived {1} | — |
| `CppHidingFunction` | {0} hides a non-virtual function from {1} | — |
| `CppIdenticalOperandsInBinaryExpression` | Binary operator '{0}' acts on identical operands | — |
| `CppIfCanBeReplacedByConstexprIf` | If statement with constant condition can be replaced with 'if constexpr' | — |
| `CppImplicitDefaultConstructorNotAvailable` | — | — |
| `CppIncompatiblePointerConversion` | Expression of type '{0}' is implicitly converted to incompatible pointer type '{1}' | — |
| `CppIncompleteSwitchStatement` | The switch statement does not cover the whole range of the '{0}' enumeration used | — |
| `CppInconsistentNaming` | — | — |
| `CppInitializedValueIsAlwaysRewritten` | {0} is reassigned in all paths before being read | — |
| `CppIntegralToPointerConversion` | Expression of type '{0}' is implicitly converted to pointer type '{1}' | — |
| `CppInvalidLineContinuation` | Backslash and newline are separated by space | — |
| `CppJoinDeclarationAndAssignment` | Declaration and assignment can be joined | — |
| `CppLambdaCaptureNeverUsed` | Lambda capture '{0}' is never used | — |
| `CppLocalVariableMayBeConst` | Local variable '{0}' can be made {1} | — |
| `CppLocalVariableMightNotBeInitialized` | {0} '{1}' might not be initialized | — |
| `CppLocalVariableWithNonTrivialDtorIsNeverUsed` | Local variable '{0}' is never used, but may have side-effects in its destructor | — |
| `CppLongFloat` | 'long' floats are not allowed by the C++ standard | — |
| `CppMemberFunctionMayBeConst` | Member function can be made const | — |
| `CppMemberFunctionMayBeStatic` | Member function can be made static | — |
| `CppMemberInitializersOrder` | Order of member initializers does not match the initialization order | — |
| `CppMismatchedClassTags` | — | — |
| `CppMissingIncludeGuard` | Include guard is not found at the beginning of a header file | — |
| `CppMissingKeywordThrow` | May be missing keyword 'throw' | — |
| `CppModulePartitionWithSeveralPartitionUnits` | Module partition with several partition units is a non-conforming MSVC extension | — |
| `CppMsExtAddressOfClassRValue` | Taking an address of class r-value is non-standard Microsoft C++ extension | — |
| `CppMsExtBindingRValueToLvalueReference` | Binding r-value to l-value reference is non-standard Microsoft C++ extension | — |
| `CppMsExtCopyElisionInCopyInitDeclarator` | Call of deleted copy {0} elided during copy-initialization. This is non-standard Microsoft C++ extension. | — |
| `CppMsExtDoubleUserConversionInCopyInit` | More than one implicit conversion applied during copy-initialization. This is non-standard Microsoft C++ extension. | — |
| `CppMsExtNotInitializedStaticConstLocalVar` | Static local variable of type '{0}' must be initialized. This is non-standard Microsoft C++ extension. | — |
| `CppMsExtReinterpretCastFromNullptr` | Casting from nullptr to pointer type with reinterpret_cast is non-standard Microsoft C++ extension | — |
| `CppMultiCharacterLiteral` | Multi-character character literal | — |
| `CppMultiCharacterWideLiteral` | Characters beyond first in a wide-character literal are ignored | — |
| `CppMustBePublicVirtualToImplementInterface` | Method must be 'public' and 'virtual' in order to implement a C++/CLI interface member | — |
| `CppMutableSpecifierOnReferenceMember` | 'mutable' specifier cannot be applied to reference members | — |
| `CppNoDiscardExpression` | Unused result of a function with a nodiscard {0}{1} | — |
| `CppNodiscardFunctionWithoutReturnValue` | [[nodiscard]] cannot be applied to functions without a return value | — |
| `CppNonExceptionSafeResourceAcquisition` | Some control paths may not reach this | — |
| `CppNonExplicitConversionOperator` | Non-explicit conversion operator | — |
| `CppNonExplicitConvertingConstructor` | Non-explicit converting constructor | — |
| `CppNonInlineFunctionDefinitionInHeaderFile` | Definition of {0} in a header file should have an 'inline' specifier | — |
| `CppNonInlineVariableDefinitionInHeaderFile` | Definition of {0} in a header file should have an 'inline' specifier | — |
| `CppNotAllPathsReturnValue` | Not all control paths return a value | — |
| `CppObjectMemberMightNotBeInitialized` | Object member '{0}' might not be initialized | — |
| `CppOutParameterMustBeWritten` | The out parameter '{0}' must be assigned to (before control leaves the function) | — |
| `CppOverrideWithDifferentVisibility` | — | — |
| `CppParameterMayBeConst` | Parameter '{0}' can be made {1} | — |
| `CppParameterMayBeConstPtrOrRef` | Parameter '{0}' can be made {1} | — |
| `CppParameterNamesMismatch` | Parameter has different names: '{0}' and '{1}' | — |
| `CppParameterNeverUsed` | Parameter '{0}' is never used | — |
| `CppParameterValueIsReassigned` | Parameter '{0}' is always rewritten in function body before being read | — |
| `CppPassValueParameterByConstReference` | Parameter '{0}' is copied for each invocation, consider passing by const reference | — |
| `CppPointerConversionDropsQualifiers` | Expression of type '{0}' is implicitly converted to pointer type '{1}' with loss of qualifiers | — |
| `CppPointerToIntegralConversion` | Expression of pointer type '{0}' is implicitly converted to {1} | — |
| `CppPolymorphicClassWithNonVirtualPublicDestructor` | Polymorphic {0} has a non-virtual public destructor | — |
| `CppPossiblyErroneousEmptyStatements` | Possibly erroneous empty statement | — |
| `CppPossiblyUninitializedMember` | — | — |
| `CppPossiblyUnintendedObjectSlicing` | Possibly unintended object slicing: {0} is value-initialized from derived {1} | — |
| `CppPrecompiledHeaderIsNotIncluded` | Precompiled header must be included at the top of source file | — |
| `CppPrecompiledHeaderNotFound` | No corresponding file with the 'Precompiled Header' property set to 'Create' (/Yc) is found | — |
| `CppPrintfBadFormat` | {0} | — |
| `CppPrintfExtraArg` | Argument does not match any printf format specifier | — |
| `CppPrintfMissedArg` | Missing '{0}' argument of printf to match '{1}' | — |
| `CppPrintfRiskyFormat` | {0} | — |
| `CppPrivateSpecialMemberFunctionIsNotImplemented` | Private {0} must be defined or deleted | — |
| `CppRangeBasedForIncompatibleReference` | Incompatible reference type in range declaration can cause unintended copying | — |
| `CppRedefinitionOfDefaultArgumentInOverrideFunction` | Override function redefines default argument for parameter '{0}' (previously defined by {1}) | — |
| `CppRedundantAccessSpecifier` | Access specifier does not change accessibility level | — |
| `CppRedundantBaseClassAccessSpecifier` | Redundant '{0}' base class access specifier | — |
| `CppRedundantBaseClassInitializer` | Redundant base class initializer | — |
| `CppRedundantBooleanExpressionArgument` | Redundant argument in conditional expression | — |
| `CppRedundantCastExpression` | Redundant {0} | — |
| `CppRedundantComplexityInComparison` | Expression can be simplified | — |
| `CppRedundantConditionalExpression` | Conditional expression can be removed | — |
| `CppRedundantConstSpecifier` | Redundant 'const' specifier on a constexpr variable | — |
| `CppRedundantControlFlowJump` | Redundant control flow jump statement | — |
| `CppRedundantDereferencingAndTakingAddress` | Dereferencing of address taking can be removed | — |
| `CppRedundantElaboratedTypeSpecifier` | Redundant elaborated type specifier | — |
| `CppRedundantElseKeyword` | Redundant 'else' keyword | — |
| `CppRedundantElseKeywordInsideCompoundStatement` | Redundant 'else' keyword | — |
| `CppRedundantEmptyDeclaration` | Redundant empty declaration | — |
| `CppRedundantEmptyStatement` | Redundant empty statement | — |
| `CppRedundantExportKeyword` | Keyword 'export' is redundant, because there is enclosing export declaration | — |
| `CppRedundantFwdClassOrEnumSpecifier` | Redundant forward declaration | — |
| `CppRedundantInlineSpecifier` | Redundant 'inline' specifier on a constexpr function | — |
| `CppRedundantLambdaParameterList` | Redundant parameter list | — |
| `CppRedundantMemberInitializer` | Redundant member initializer in constructor initialization list | — |
| `CppRedundantNamespaceDefinition` | Namespace can be removed by qualifying class name | — |
| `CppRedundantParentheses` | Redundant parentheses | — |
| `CppRedundantQualifier` | Redundant qualifier | — |
| `CppRedundantQualifierADL` | Qualifier is not needed because of ADL | — |
| `CppRedundantStaticSpecifierOnMemberAllocationFunction` | Redundant 'static' specifier on member operator {0} | — |
| `CppRedundantStaticSpecifierOnThreadLocalLocalVariable` | Redundant 'static' specifier on a 'thread_local' local variable | — |
| `CppRedundantTemplateArguments` | Redundant template arguments | — |
| `CppRedundantTemplateKeyword` | Redundant 'template' keyword | — |
| `CppRedundantTypenameKeyword` | Redundant 'typename' keyword | — |
| `CppRedundantVoidArgumentList` | Redundant void argument list | — |
| `CppRedundantZeroInitializerInAggregateInitialization` | Redundant zero initializer in aggregate initialization | — |
| `CppReferenceToOverriddenVirtualFunction` | — | — |
| `CppReinterpretCastFromVoidPtr` | reinterpret_cast is used instead of static_cast | — |
| `CppRemoveRedundantBraces` | Inconsistent braces style: redundant braces | — |
| `CppReplaceMemsetWithZeroInitialization` | memset can be replaced with zero initialization of '{0}' | — |
| `CppReplaceTieWithStructuredBinding` | Structured bindings can be used instead of tie(..) | — |
| `CppReturnNoValueInNonVoidFunction` | A function with non-void return type should return a value | — |
| `CppSmartPointerVsMakeFunction` | Construction of {0} can be replaced with {1} | — |
| `CppSomeObjectMembersMightNotBeInitialized` | Following members of {0} might not be initialized: {1} | — |
| `CppSpecialFunctionWithoutNoexceptSpecification` | Move constructor does not have any noexcept specification | — |
| `CppStaticAssertFailure` | — | — |
| `CppStaticDataMemberInUnnamedStruct` | Static data member '{0}' in anonymous {1} is not allowed | — |
| `CppStaticSpecifierOnAnonymousNamespaceMember` | Redundant 'static' specifier on an anonymous namespace member | — |
| `CppStringLiteralToCharPointerConversion` | {0} | — |
| `CppSyntaxWarning` | — | — |
| `CppTabsAreDisallowed` | Tabulation characters are not allowed | — |
| `CppTemplateArgumentsCanBeDeduced` | Template arguments can be deduced | — |
| `CppTemplateParameterNeverUsed` | Template parameter '{0}' is never used | — |
| `CppTemplateParameterShadowing` | — | — |
| `CppThisArgMemberFuncDelegateCtorIsUnsuportedByDotNetCore` | Delegate constructors with a member pointer parameter are unsupported by .NET Core | — |
| `CppThrowExpressionCanBeReplacedWithRethrow` | Throw expression can be replaced with a rethrow expression | — |
| `CppTooWideScope` | Variable '{0}' can be moved to {1} | — |
| `CppTooWideScopeInitStatement` | Variable '{0}' can be moved to {1} | — |
| `CppTypeAliasNeverUsed` | {0} is never used | — |
| `CppUE4BlueprintCallableFunctionMayBeConst` | BlueprintCallable function can be made const | — |
| `CppUE4BlueprintCallableFunctionMayBeStatic` | BlueprintCallable function can be made static | — |
| `CppUE4CodingStandardNamingViolationWarning` | Name '{0}' violates Unreal Engine's coding standard rule '{1}'. Suggested name is '{2}'. | — |
| `CppUE4CodingStandardUClassNamingViolationError` | Class is declared with UCLASS or USTRUCT macro but it's name '{0}' violates Unreal Engine's coding standard rule '{1}' and will lead to a build error. Suggested name is '{2}'. | — |
| `CppUE4ProbableMemoryIssuesWithUObject` | Object member '{0}' can be destroyed during garbage collection, resulting in a stale pointer | — |
| `CppUE4ProbableMemoryIssuesWithUObjectsInContainer` | Objects stored in member '{0}' can be destroyed during garbage collection, resulting in stale pointers | — |
| `CppUEBlueprintCallableFunctionUnused` | {0} is never used in Blueprint or C++ code | — |
| `CppUEBlueprintImplementableEventNotImplemented` | BlueprintImplementableEvent function is not implemented in any blueprint | — |
| `CppUEIncorrectEngineDirectory` | The engine version referenced in the solution does not correspond to the engine version from '{0}'.     Referenced path: {2}     Target version path: {1}     Regenerate project files to fix that. | — |
| `CppUEMissingStructMember` | Missing {0} required by {1} | — |
| `CppUEMissingSuperCall` | Missing 'Super::{0}' in derived {0} implementation | — |
| `CppUENonExistentInputAction` | {0} is not a name of any input action | — |
| `CppUENonExistentInputAxis` | {0} is not a name of any input axis | — |
| `CppUESourceFileWithoutPredefinedMacros` | Unreal Engine predefined macros are not found for '{0}' in the project model, you might need to regenerate the project files | — |
| `CppUESourceFileWithoutStandardLibrary` | C++ standard library headers are not found, you might need to regenerate the project files | — |
| `CppUEVersionFileDoesntExist` | Unreal Engine version file (located at '{0}') not found. Regenerate project files. | — |
| `CppUFunctionMacroCallHasNoEffect` | UFUNCTION macro call before declaration of '{0}' has no effect outside of UCLASS, USTRUCT or IInterface | — |
| `CppUPropertyMacroCallHasNoEffect` | UPROPERTY macro call before declaration of '{0}' has no effect outside of UCLASS or USTRUCT | — |
| `CppUninitializedDependentBaseClass` | — | — |
| `CppUninitializedNonStaticDataMember` | Non-static data member '{0}' is uninitialized | — |
| `CppUnionMemberOfReferenceType` | {0} cannot have reference type | — |
| `CppUnmatchedPragmaEndRegionDirective` | The '#pragma endregion' directive is missing a matching '#pragma region' directive | — |
| `CppUnmatchedPragmaRegionDirective` | The '#pragma region' directive is missing a matching '#pragma endregion' directive | — |
| `CppUnnamedNamespaceInHeaderFile` | Unnamed namespaces should not be used in header files | — |
| `CppUnnecessaryWhitespace` | Unnecessary whitespace | — |
| `CppUnreachableCode` | Unreachable code | — |
| `CppUnsignedZeroComparison` | — | — |
| `CppUnusedIncludeDirective` | Possibly unused #include directive | — |
| `CppUseAlgorithmWithCount` | Call to '{0}' can be replaced with a call to '{0}_n' | — |
| `CppUseAssociativeContains` | 'contains' member function can be used | — |
| `CppUseAuto` | Type can be replaced with auto | — |
| `CppUseAutoForNumeric` | Type can be replaced with auto | — |
| `CppUseDesignatedInitializers` | — | — |
| `CppUseElementsView` | views::{0} can be used | — |
| `CppUseEraseAlgorithm` | '{0}' can be used | — |
| `CppUseFamiliarTemplateSyntaxForGenericLambdas` | Lambda can be rewritten to use explicit template parameters | — |
| `CppUseInternalLinkage` | — | — |
| `CppUseOfUndeclaredClass` | Class '{0}' has not been declared previously | — |
| `CppUseRangeAlgorithm` | Call to '{0}' can be replaced with a call to 'ranges::{0}' | — |
| `CppUseStdSize` | Can be replaced with std::size | — |
| `CppUseStructuredBinding` | Variable '{0}' can be replaced with structured bindings | — |
| `CppUseTypeTraitAlias` | {0} can be simplified using '{1}' | — |
| `CppUserDefinedLiteralSuffixDoesNotStartWithUnderscore` | User-defined literal suffixes must start with an underscore | — |
| `CppUsingResultOfAssignmentAsCondition` | Result of assignment is used as condition | — |
| `CppVariableCanBeMadeConstexpr` | Variable {0} can be made constexpr | — |
| `CppVirtualFunctionCallInsideCtor` | Call to a virtual function inside a {0} is resolved at compile time | — |
| `CppVirtualFunctionInFinalClass` | Virtual {0} cannot be overridden because containing {1} is declared as '{2}' | — |
| `CppVolatileParameterInDeclaration` | Volatile-qualified parameter in a function declaration | — |
| `CppWarningDirective` | #warning directive: {0} | — |
| `CppWrongIncludesOrder` | Order of #include directives does not match code style settings | — |
| `CppWrongSlashesInIncludeDirective` | Slash symbol '{0}' in #include directive does not match code style settings | — |
| `CppZeroConstantCanBeReplacedWithNullptr` | Zero constant can be replaced with nullptr | — |
| `CppZeroValuedExpressionUsedAsNullPointer` | Expression that evaluates to zero is used as a null pointer constant | — |
| `IfStdIsConstantEvaluatedCanBeReplaced` | 'if' can be replaced by 'if consteval' | — |
| `StdIsConstantEvaluatedWillAlwaysEvaluateToConstant` | '{0}' will always evaluate to 'true' in a manifestly constant-evaluated expression | — |
| `UnrealHeaderToolError` | — | — |
| `UnrealHeaderToolWarning` | — | — |

## C# and Visual Basic — 105 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `AutoPropertyCanBeMadeGetOnly.Global` | Auto-property can be made {0} | — |
| `AutoPropertyCanBeMadeGetOnly.Local` | Auto-property can be made {0} | — |
| `CheckNamespace` | Namespace does not correspond to file location, must be: '{0}' | — |
| `ClassCanBeSealed.Global` | {0} '{1}' has no inheritors and can be marked {2} | — |
| `ClassCanBeSealed.Local` | {0} '{1}' has no inheritors and can be marked {2} | — |
| `ClassNeverInstantiated.Global` | {0} '{1}' is never instantiated | — |
| `ClassNeverInstantiated.Local` | {0} '{1}' is never instantiated | — |
| `ClassWithVirtualMembersNeverInherited.Global` | {0} '{1}' has some {2} members but no inheritors | — |
| `ClassWithVirtualMembersNeverInherited.Local` | {0} '{1}' has some {2} members but no inheritors | — |
| `CollectionNeverQueried.Global` | Content of collection '{0}' is only updated but never used | — |
| `CollectionNeverQueried.Local` | Content of collection '{0}' is only updated but never used | — |
| `CollectionNeverUpdated.Global` | Content of collection '{0}' is never updated | — |
| `CollectionNeverUpdated.Local` | Content of collection '{0}' is never updated | — |
| `ConvertToConstant.Global` | Convert into constant | — |
| `ConvertToConstant.Local` | Convert into constant | ✅ |
| `DefaultStructEqualityIsUsed.Global` | Struct '{0}' is checked for equality using an inefficient runtime-provided implementation | — |
| `DefaultStructEqualityIsUsed.Local` | Struct '{0}' is checked for equality using an inefficient runtime-provided implementation | — |
| `DuplicateItemInLoggerTemplate` | Items in a structured log message should not be duplicated or differ only in case | — |
| `EntityNameCapturedOnly.Global` | {0} '{1}' is only used to capture its name | — |
| `EntityNameCapturedOnly.Local` | {0} '{1}' is only used to capture its name | — |
| `EventNeverInvoked` | Event '{0}' is never invoked | — |
| `EventNeverInvoked.Global` | Event '{0}' is invoked neither in this class nor in its inheritors | — |
| `EventNeverSubscribedTo.Global` | Event '{0}' is never subscribed to | — |
| `EventNeverSubscribedTo.Local` | Event '{0}' is never subscribed to | — |
| `FieldCanBeMadeReadOnly.Global` | {0} can be made {1} | — |
| `FieldCanBeMadeReadOnly.Local` | {0} can be made {1} | — |
| `FormatSpecifierCapturesRightBraces` | The right brace will be captured by a format specifier when run on .NET Framework | — |
| `FormatStringProblem` | Non-existing argument in format string | — |
| `HighlightingSourceContractViolation` | '{0}' is not allowed by contract interface '{1}' | — |
| `HighlightingSourceContractViolationOnBaseClass` | Base class '{0}' does not implement contract interface '{1}' | — |
| `MemberCanBeInternal` | {0} '{1}' can be made {2} | ✅ |
| `MemberCanBeMadeStatic.Global` | {0} '{1}' can be made {2} | ✅ |
| `MemberCanBeMadeStatic.Local` | {0} '{1}' can be made {2} | — |
| `MemberCanBePrivate.Global` | {0} '{1}' can be made private | ✅ |
| `MemberCanBePrivate.Local` | {0} '{1}' can be made private | — |
| `MemberCanBeProtected.Global` | {0} '{1}' can be made protected | — |
| `MemberCanBeProtected.Local` | {0} '{1}' can be made protected | — |
| `MissingHighlightingSourceAttribute` | Missing highlighting source attribute | — |
| `MultipleHighlightingSourceAttributes` | Conflict with '[{0}]' | — |
| `MultipleHighlightingSourceContracts` | Multiple highlighting source contracts in hierarchy: {0} | — |
| `NotAccessedField.Compiler` | {0} '{1}' is assigned but its value is never used | — |
| `NotAccessedField.Global` | {0} '{1}' is assigned but its value is never used | ✅ |
| `NotAccessedField.Local` | {0} '{1}' is assigned but its value is never used | — |
| `NotAccessedOutParameterVariable` | {0} '{1}' is only used to discard 'out' parameter value | — |
| `NotAccessedPositionalProperty.Global` | Positional property '{0}' is never accessed (except in implicit {1}/{2} implementations) | — |
| `NotAccessedPositionalProperty.Local` | Positional property '{0}' is never accessed (except in implicit {1}/{2} implementations) | — |
| `NotAccessedPrimaryConstructorParameterCompiler` | Primary constructor parameter '{0}' is never accessed | — |
| `NotAccessedVariable` | {0} '{1}' is only assigned but its value is never used | — |
| `NotAccessedVariable.Compiler` | {0} '{1}' is only assigned but its value is never used | — |
| `OutParameterValueIsAlwaysDiscarded.Global` | Parameter '{0}' output value is always discarded | — |
| `OutParameterValueIsAlwaysDiscarded.Local` | Parameter '{0}' output value is always discarded | — |
| `ParameterOnlyUsedForPreconditionCheck.Global` | Parameter '{0}' is only used for precondition check(s) | — |
| `ParameterOnlyUsedForPreconditionCheck.Local` | Parameter '{0}' is only used for precondition check(s) | — |
| `ParameterTypeCanBeEnumerable.Global` | Parameter can be of type '{0}' | — |
| `ParameterTypeCanBeEnumerable.Local` | Parameter can be of type '{0}' | — |
| `PassStringInterpolation` | Pass string interpolation | — |
| `PossibleInfiniteInheritance` | Possible infinite inheritance | — |
| `PropertyCanBeMadeInitOnly.Global` | Property can be made {0} | — |
| `PropertyCanBeMadeInitOnly.Local` | Property can be made {0} | — |
| `RedundantHighlightingType` | Redundant highlighting type: already specified in '{0}' | — |
| `ReturnTypeCanBeEnumerable.Global` | Return type can be '{0}' | — |
| `ReturnTypeCanBeEnumerable.Local` | Return type can be '{0}' | — |
| `StructCanBeMadeReadOnly` | {0} can be made {1} | — |
| `StructLacksIEquatable.Global` | Implement '{0}' interface to avoid boxing when using struct equality in a generic context | — |
| `StructLacksIEquatable.Local` | Implement '{0}' interface to avoid boxing when using struct equality in a generic context | — |
| `StructMemberCanBeMadeReadOnly` | {0} can be made {1} | — |
| `StructuredMessageTemplateProblem` | Non-existing argument in message template | — |
| `SuggestBaseTypeForParameter` | Parameter can be of type '{0}' | — |
| `SuggestBaseTypeForParameterInConstructor` | Parameter can be of type '{0}' | — |
| `SuspiciousTypeConversion.Global` | Suspicious cast: there is no type in the solution which is inherited from both '{0}' and '{1}' | ✅ |
| `SymbolFromNotCopiedLocallyReferenceUsedWarning` | — | — |
| `TypeWithSuspiciousEqualityIsUsedInRecord.Global` | Type with suspicious equality is used as a member of a record type | — |
| `TypeWithSuspiciousEqualityIsUsedInRecord.Local` | Type with suspicious equality is used as a member of a record type | — |
| `UnassignedField.Compiler` | {0} '{1}' is never assigned | — |
| `UnassignedField.Global` | {0} '{1}' is never assigned | — |
| `UnassignedField.Local` | {0} '{1}' is never assigned | — |
| `UnassignedGetOnlyAutoProperty` | Get-only auto-property '{0}' is never assigned | — |
| `UnassignedReadonlyField` | Readonly {0} '{1}' is never assigned | — |
| `UnassignedReadonlyField.Compiler` | Readonly {0} '{1}' is never assigned | — |
| `UnregisteredHighlightingType` | Highlighting '{0}' is not specified in the '{1}' on the containing type | — |
| `UnusedAutoPropertyAccessor.Global` | Auto-property accessor '{0}' is never used | — |
| `UnusedAutoPropertyAccessor.Local` | Auto-property accessor '{0}' is never used | — |
| `UnusedField.Compiler` | {0} '{1}' is never used | — |
| `UnusedLabel` | Label '{0}' is never referenced | — |
| `UnusedMember.Global` | {0} '{1}' is never used{2} | ✅ |
| `UnusedMember.Local` | {0} '{1}' is never used{2} | — |
| `UnusedMemberHierarchy.Global` | {0} '{1}' with its overriders is never used from outside | — |
| `UnusedMemberHierarchy.Local` | {0} '{1}' with its overriders is never used from outside | — |
| `UnusedMemberInSuper.Global` | Only implementations of {0} '{1}' are used | — |
| `UnusedMemberInSuper.Local` | Only implementations of {0} '{1}' are used | — |
| `UnusedMethodReturnValue.Global` | Method '{0}' return value is never used | — |
| `UnusedMethodReturnValue.Local` | Method '{0}' return value is never used | — |
| `UnusedParameter.Global` | Parameter '{0}' is never used | — |
| `UnusedParameter.Local` | Parameter '{0}' is never used | ✅ |
| `UnusedParameterInPartialMethod` | Parameter '{0}' is never used | — |
| `UnusedPositionalParameterCompiler` | Positional parameter '{0}' is never used. Did you forget to use it to initialize the property with that name? | — |
| `UnusedType.Global` | {0} '{1}' is never used{2} | — |
| `UnusedType.Local` | {0} '{1}' is never used{2} | — |
| `UnusedTypeParameter` | Type parameter '{0}' is never used | — |
| `UnusedVariable` | {0} '{1}' is never used | ✅ |
| `UnusedVariable.Compiler` | {0} '{1}' is never used | — |
| `UseFormatSpecifierInFormatString` | Use format specifier | — |
| `UseStringInterpolation` | Use string interpolation expression | ✅ |
| `VirtualMemberNeverOverridden.Global` | {0} {1} '{2}' is never overridden | — |
| `VirtualMemberNeverOverridden.Local` | {0} {1} '{2}' is never overridden | — |

## JavaScript — 105 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `AssignToConstant` | Assignment to constant '{0}' | — |
| `AssignToImplicitGlobalInFunctionScope` | Assignment to an implicitly declared global variable '{0}' | — |
| `AssignedValueIsNeverUsed` | Value assigned is not used in any execution path | — |
| `AssignmentInConditionExpression` | Assignment to a variable inside a conditional statement | — |
| `BlockScopeRedeclaration` | Redeclaration of block-scope variable '{0}' | — |
| `CallerCalleeUsing` | Avoid using 'arguments.caller' or 'arguments.callee' by either giving function expressions a name or use a function declaration | — |
| `CallerCalleeUsingError` | 'arguments.caller' and 'arguments.callee' are forbidden in 'strict mode' | — |
| `ClosureOnModifiedVariable` | Closure on a variable modified in loop of outer scope | — |
| `CoercedEqualsUsing` | '{0}' tries to coerce values before comparing them, which can lead to unexpected results | — |
| `CoercedEqualsUsingWithNullUndefined` | '{0}' comparison with '{1}' performs comparison with '{2}' as well | — |
| `CommaNotValidHere` | Comma is not valid here | — |
| `ConditionIsAlwaysConst` | Condition is always '{0}' | — |
| `ConstructorCallNotUsed` | Constructor call is not used or possibly used for side-effects | — |
| `DeclarationHides` | Declaration hides {0} from outer scope | — |
| `DeletingNonQualifiedReference` | Calling 'delete' on non-qualified identifier is not allowed in 'strict mode' | — |
| `DuplicatingLocalDeclaration` | Duplicate declaration | — |
| `DuplicatingParameterDeclarationError` | Duplicate parameter declaration is not allowed in 'strict mode' | — |
| `DuplicatingPropertyDeclaration` | Duplicate property declaration | — |
| `DuplicatingPropertyDeclarationError` | Duplicate property declaration is not allowed in 'strict mode' | — |
| `DuplicatingSwitchLabel` | Duplicate switch label | — |
| `ElidedTrailingElement` | When element is elided at the end of an array, that element does not contribute to the length of the Array | — |
| `EmptyObjectPropertyDeclaration` | Object property declaration is expected | — |
| `ErrorInXmlDocReference` | {0} | — |
| `Es6Feature` | ECMAScript 2015 feature. Your current language level is: {0} | — |
| `Es7Feature` | ECMAScript 2016 feature. Your current language level is: {0} | — |
| `EvalArgumentsNameError` | 'eval' or 'arguments' cannot be used as declaration name in 'strict mode' | — |
| `ExperimentalFeature` | Experimental level feature. Your current language level is: {0} | — |
| `ExpressionIsAlwaysConst` | Expressions is always '{0}' | — |
| `FunctionParameterNamedArguments` | Parameter name 'arguments' clashes with pre-defined JS function-scope variable | — |
| `FunctionUsedOutOfScope` | Function '{0}' is used outside of block which it is declared | — |
| `FunctionsUsedBeforeDeclared` | Function '{0}' is used before it is declared | — |
| `HeuristicallyUnreachableCode` | Code is heuristically unreachable | — |
| `ImportKeywordNotWithInvocation` | 'import' keyword is not allowed here | — |
| `InconsistentFunctionReturns` | Return statement should have value | — |
| `IncorrectOperandInTypeOfComparison` | Incorrect 'typeof' value '{0}' | — |
| `IncorrectTripleSlashLocation` | Triple-slash directives are only valid at the top of the file | — |
| `InvalidJsonSyntax` | '{0}' is not a valid JSON syntax | — |
| `InvocationOfNonFunction` | Invoked expressions is not a function | — |
| `InvokedExpressionMaybeNonFunction` | Invoked expressions can be non-function | — |
| `JoinDeclarationAndInitializerJs` | Join declaration and assignment | — |
| `JsPathNotFound` | {0} | — |
| `JsUnreachableCode` | Code is unreachable | — |
| `JsonValidationFailed` | JSON validation failed: {0} | — |
| `JumpMustBeInLoop` | 'continue' must be in loop | — |
| `LValueIsExpected` | L-value is expected | — |
| `LabelOrSemicolonExpected` | Label name or ';' expected | — |
| `LexicalDeclarationNeedsBlock` | Lexical declaration can only be declared inside a block | — |
| `LocalFunctionRedefinedLater` | Function '{0}' is redefined later | — |
| `MissingHasOwnPropertyInForeach` | There is no .hasOwnProperty() check in a body of 'for in' loop. This will cause an enumeration of prototype properties. | — |
| `MisuseOfOwnerFunctionThis` | Suspicious use of 'this' qualifier from inner function | — |
| `MultipleDeclarationsInForeach` | Only one variable can be declared in for header | — |
| `NativeTypePrototypeExtending` | Extending prototype of native object '{0}' may cause unexpected effects | — |
| `NativeTypePrototypeOverwriting` | Overwriting prototype of native object '{0}' has no effect | — |
| `NonAssignedConstant` | Constant must be initialized | — |
| `NotAllPathsReturnValue` | Not all code paths return a value | — |
| `NotResolved` | Cannot resolve symbol '{0}' | — |
| `ObjectDestructuringWithoutParentheses` | Non-parenthesized object destructuring assignment cannot be used in expression statement | — |
| `ObjectLiteralsAreNotCommaFree` | Comma expected | — |
| `OctalLiteralsNotAllowedError` | Octal literals and escape sequences are not allowed in the 'strict mode' | — |
| `ParameterValueIsNotUsed` | Parameter value is not used because it is overridden in method body | — |
| `PossiblyIncorrectlyBrokenStatement` | Statement is possibly incorrectly broken by a newline | — |
| `PossiblyUnassignedProperty` | Property or global variable '{0}' is possibly never assigned | — |
| `PropertyGetterCannotHaveParameters` | Property getter cannot have parameters | — |
| `PropertySetterMustHaveSingleParameter` | Property setter must have a single parameter | — |
| `QualifiedExpressionIsNull` | Qualifier is {0} | — |
| `QualifiedExpressionMaybeNull` | Qualifier can be {0} | — |
| `RedundantBlock` | Block is redundant | — |
| `RedundantElseBlock` | Redundant 'else' block | — |
| `RedundantIntermediateVariable` | Intermediate variable can be safely inlined | — |
| `RedundantLocalClassName` | Local class name '{0}' is never used within its own block so it is redundant | — |
| `RedundantLocalFunctionName` | Local function '{0}' is never called recursively so its name is redundant | — |
| `ReplaceIndicingWithArrayDestructuring` | Sequential indexing can be replaced with a {0} | — |
| `ReplaceIndicingWithShortHandPropertiesAfterDestructuring` | Sequential indexing in properties can be replaced with short-hand properties after a destructuring assignment | — |
| `ReplaceUndefinedCheckingSeriesWithObjectDestructuring` | Sequential undefined-checking can be replaced with a {0} | — |
| `ReplaceWithDestructuringSwap` | Intermediate variable can be safely replaced with a destructuring swap | — |
| `ReturnFromGlobalScopetWithValue` | Return from global scope must not have value | — |
| `SameVariableAssignment` | Assignment of a local variable to itself is redundant | — |
| `SimilarExpressionsComparison` | Similar expressions comparison | — |
| `StatementTermination` | Statement termination does not match code style settings | — |
| `StringConcatenationToTemplateString` | Concatenation can be converted to a template | — |
| `StringLiteralWrongQuotes` | Your code style settings require literal expression to be surrounded with {0}-quotes | — |
| `SuperCallProhibitsThis` | 'super' must be called before accessing 'this' in the constructor of a derived class | — |
| `SuspiciousLambdaBlock` | Suspicious syntax: this is a block, did you mean to have an object literal here? | — |
| `ThisInGlobalContext` | 'this' should not be used in global context | — |
| `ThrowMustBeFollowedByExpression` | Throw must be followed by an expression | — |
| `UndeclaredGlobalVariableUsing` | Using a variable (property or object) without declaring it is not allowed in the 'strict mode' | — |
| `UnsafeCommaInObjectPropertiesList` | Trailing comma in object properties list may lead to an error in some JS engines | — |
| `UnusedInheritedParameter` | Parameter '{0}' is never used | — |
| `UnusedLocals` | Local variable '{0}' is never used | — |
| `UnusedParameter` | Parameter '{0}' is never used | — |
| `UnusedProperty` | {0} '{1}' is never used | — |
| `UsageOfDefinitelyUnassignedValue` | Usage of unassigned value | — |
| `UsageOfPossiblyUnassignedValue` | Usage of possibly unassigned value | — |
| `UseOfImplicitGlobalInFunctionScope` | Use of an implicitly declared global variable '{0}' | — |
| `UsingOfReservedWord` | Use of future reserved word | — |
| `UsingOfReservedWordError` | Using of future reserved keywords is not allowed in the 'strict mode' | — |
| `VariableCanBeMadeConst` | Variable '{0}' can be made constant | — |
| `VariableCanBeMadeLet` | Variable '{0}' can be made block-local | — |
| `VariableCanBeMovedToInnerBlock` | Variable '{0}' declaration can be moved to inner block | — |
| `VariableUsedBeforeDeclared` | Variable '{0}' is used before it is declared | — |
| `VariableUsedInInnerScopeBeforeDeclared` | Variable '{0}' is used in inner scope before it is declared | — |
| `VariableUsedOutOfScope` | Variable '{0}' is used outside of block which it is declared | — |
| `WithStatementUsingError` | 'with' statement is not allowed in the 'strict mode' | — |
| `WrongExpressionStatement` | Expression statement is not assignment or call | — |
| `XmlDocCommentSyntaxProblem` | {0} | — |

## XAML — 58 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `ObsoleteElement` | {0} | — |
| `ObsoleteElementError` | {0} | — |
| `Xaml.AssignNullToNotNullAttribute` | — | — |
| `Xaml.AvaloniaWrongBindingModeForStreamBindingOperator` | — | — |
| `Xaml.BindingWithContextNotResolved` | — | — |
| `Xaml.BindingWithoutContextNotResolved` | — | — |
| `Xaml.CompiledBindingMissingDataTypeErrorHighlighting` | — | — |
| `Xaml.ConstructorWarning` | — | — |
| `Xaml.DecimalParsingIsCultureDependent` | — | — |
| `Xaml.DependencyPropertyResolveError` | — | — |
| `Xaml.DuplicateStyleSetter` | Duplicate {0} '{1}' setter | — |
| `Xaml.DynamicResourceError` | — | — |
| `Xaml.ElementNameReferenceNotResolved` | — | — |
| `Xaml.EmptyGridLengthDefinition` | — | — |
| `Xaml.FieldModifierRequiresNameAttribute` | x:FieldModifier attribute requires x:Name attribute | — |
| `Xaml.GridDefinitionsCanBeConvertedToAttribute` | — | — |
| `Xaml.IgnoredPathHighlighting` | — | — |
| `Xaml.IndexOutOfGridDefinition` | — | — |
| `Xaml.InvalidDynamicResourceType` | — | — |
| `Xaml.InvalidMemberType` | — | — |
| `Xaml.InvalidResourceTargetType` | — | — |
| `Xaml.InvalidResourceType` | — | — |
| `Xaml.InvalidType` | — | — |
| `Xaml.LanguageLevel` | — | — |
| `Xaml.MappedPathHighlighting` | — | — |
| `Xaml.MethodArgumentsWillBeIgnored` | — | — |
| `Xaml.MissingGridIndex` | — | — |
| `Xaml.OverloadsCollision` | — | — |
| `Xaml.ParentIsOutOfCurrentComponentTree` | Specified parent is outside the current component tree and possibly missing, which may cause runtime errors | — |
| `Xaml.PathError` | — | — |
| `Xaml.PossibleNullReferenceException` | — | — |
| `Xaml.RedundantAttachedProperty` | Attached property setter '{0}.{1}' has no effect in the current context and can be removed | — |
| `Xaml.RedundantBindingModeAttribute` | '{0}' is default binding mode and can be safely removed | — |
| `Xaml.RedundantCollectionProperty` | Empty collection property setter is redundant and can be safely removed | — |
| `Xaml.RedundantFreezeAttribute` | {0} | — |
| `Xaml.RedundantGridDefinitions` | — | — |
| `Xaml.RedundantGridSpan` | — | — |
| `Xaml.RedundantModifiersAttribute` | {0} are '{1}' by default | — |
| `Xaml.RedundantNameAttribute` | {0} attribute in resources is negligible | — |
| `Xaml.RedundantNamespaceAlias` | Namespace alias '{0}' is not required by code and can be safely removed | — |
| `Xaml.RedundantPropertyTypeQualifier` | Redundant property type qualifier | — |
| `Xaml.RedundantResource` | Resource{0} is never used | — |
| `Xaml.RedundantStyledValue` | Equal value provided by style '{0}' | — |
| `Xaml.RedundantUpdateSourceTriggerAttribute` | 'Default' is the default value for UpdateSourceTrigger attribute and can be safely removed | — |
| `Xaml.RedundantXamarinFormsClassDeclaration` | Resource cannot be accessed by class {0} because of x:Key attribute | — |
| `Xaml.ResourceFilePathCaseMismatch` | — | — |
| `Xaml.RoutedEventResolveError` | — | — |
| `Xaml.StaticResourceNotResolved` | — | — |
| `Xaml.StyleClassNotFound` | — | — |
| `Xaml.StyleInvalidTargetType` | Target type '{0}' is not convertible to base type '{1}' | — |
| `Xaml.UnexpectedElement` | — | — |
| `Xaml.UnexpectedTextToken` | — | — |
| `Xaml.XKeyAttributeDisallowed` | {0} is allowed for resources and dictionary elements only | — |
| `Xaml.XamlDuplicateDeviceFamilyTypeViewHighlighting` | — | — |
| `Xaml.XamlMismatchedDeviceFamilyViewClrNameHighlighting` | — | — |
| `Xaml.XamlRelativeSourceDefaultModeWarningHighlighting` | — | — |
| `Xaml.XamlUnknownDeviceFamilyTypeHighlighting` | — | — |
| `Xaml.XamlXamarinFormsDataTypeAndBindingContextTypeMismatchedHighlighting` | — | — |

## Visual Basic — 53 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `AccessToDisposedClosure` | — | — |
| `AccessToForEachVariableInClosure` | — | — |
| `AccessToModifiedClosure` | — | — |
| `ConvertToVbAutoProperty` | — | — |
| `ConvertToVbAutoPropertyWhenPossible` | — | — |
| `ConvertToVbAutoPropertyWithPrivateSetter` | — | — |
| `FunctionNeverReturns` | — | — |
| `ImplicitAnnotationRedundancy` | — | — |
| `InactivePreprocessorBranch` | — | — |
| `LocalVariableHidesMember` | — | — |
| `LongLiteralEndingLowerL` | — | — |
| `NotAssignedOutParameter` | — | — |
| `ParameterHidesMember` | — | — |
| `PossibleMultipleEnumeration` | — | — |
| `PossibleWriteToMe` | — | — |
| `RedundantArrayLowerBoundSpecification` | — | — |
| `RedundantAssignment` | — | — |
| `RedundantEmptyCaseElse` | — | — |
| `RedundantIfStatementThenKeyword` | — | — |
| `RedundantIteratorKeyword` | — | — |
| `RedundantMeQualifier` | — | — |
| `RedundantMyBaseQualifier` | — | — |
| `RedundantMyClassQualifier` | — | — |
| `RedundantParentheses` | — | — |
| `RedundantPropertyParentheses` | — | — |
| `RedundantQualifier` | — | — |
| `RedundantSetterValueParameterDeclaration` | — | — |
| `SimplifyConditionalOperator` | — | — |
| `SimplifyIIf` | — | — |
| `UnusedImportClause` | — | — |
| `UseImplicitByValModifier` | — | — |
| `UseImplicitlyTypedVariable` | — | — |
| `UseImplicitlyTypedVariableEvident` | — | — |
| `VBWarnings::BC40000` | — | — |
| `VBWarnings::BC400005` | — | — |
| `VBWarnings::BC40008` | — | — |
| `VBWarnings::BC40056` | — | — |
| `VBWarnings::BC42016` | — | — |
| `VBWarnings::BC42025` | — | — |
| `VBWarnings::BC42104` | — | — |
| `VBWarnings::BC42105,BC42106,BC42107` | — | — |
| `VBWarnings::BC42304` | — | — |
| `VBWarnings::BC42309` | — | — |
| `VBWarnings::BC42322` | — | — |
| `VBWarnings::BC42349` | — | — |
| `VBWarnings::BC42353,BC42354,BC42355` | — | — |
| `VBWarnings::BC42356` | — | — |
| `VBWarnings::BC42358` | — | — |
| `VBWarnings::BC42380` | — | — |
| `VBWarnings::BC42504` | — | — |
| `VBWarnings::BC42505` | — | — |
| `VBWarnings::WME006` | — | — |
| `VbUnreachableCode` | — | — |

## TypeScript — 39 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `AbstractClassConstructorCanBeMadeProtected` | Constructor of an abstract class can be made protected | — |
| `AmdDependencyPathProblem` | Import declaration in an ambient external module declaration cannot reference external module through relative external module name | — |
| `AssignedValueWontBeAssignedToCorrespondingField` | Parameter-property '{0}' value assigned inside the constructor will not be assigned to a class property '{0}'. Did you mean to use 'this.{0}'? | — |
| `CreateSpecializedOverload` | String literal usage allows creating a specialized overload | — |
| `CyclicReferenceComment` | Reference to file '{0}' creates cyclic dependency via '{1}' | — |
| `DeclarationVisibilityError` | Exported class '{0}' extends private class '{1}' | — |
| `DoubleNegationOfBoolean` | Double negation of a boolean expression is redundant | — |
| `DuplicateIdentifierError` | Duplicate identifier '{0}' | — |
| `DuplicateReferenceComment` | File '{0}' is already referenced | — |
| `EmptyReturnValueForTypeAnnotatedFunction` | Function is provided with type annotation, but return statement value is empty | — |
| `ImplicitAnyError` | '{0}' without 'get' accessor and parameter type annotation on 'set' accessor implicitly has 'any' type | — |
| `ImplicitAnyTypeWarning` | '{0}' without 'get' accessor and parameter type annotation on 'set' accessor implicitly has 'any' type | — |
| `IntroduceVariableToApplyGuard` | Introduce variable to apply type guard | — |
| `LessSpecificOverloadThanMainSignature` | Overload is less specific than the main signature: potential error | — |
| `MoreSpecificSignatureAfterLessSpecific` | More specific signature follows less specific: potential error | — |
| `ParameterDoesntMakeAnySense` | Parameter is not a part of the main signature | — |
| `PrivateVariableCanBeMadeReadonly` | Private class field can be made readonly | — |
| `RedundantComparisonWithBoolean` | Comparison of boolean value with '{0}' is equivalent to {1} | — |
| `RedundantEmptyConstructor` | Redundant empty constructor without parameters | — |
| `RedundantParentTypeDeclaration` | Specifying this parent type is redundant, because {0} already exists in the list | — |
| `RedundantTypeCast` | Type cast is redundant: expression is always of specified type | — |
| `RedundantTypeCastStructural` | Type cast is redundant: expression is of a type structurally identical to specified type | — |
| `RedundantVariableTypeSpecification` | Type specification for {0} '{1}' is redundant: specified type is the same as the contextual type | — |
| `SameImportsWithDifferentName` | Two different import declarations '{0}' and '{1}' for the same imported item | — |
| `SpecifyVariableTypeExplicitly` | Type '{0}' of {1} '{2}' can be specified explicitly | — |
| `SuspiciousInstanceofCheck` | Suspicious 'instanceof' check: {0} | — |
| `SuspiciousThisUsage` | Suspicious 'this' usage: in current context 'this' refers to a local function, not to a containing class | — |
| `SuspiciousTypeofCheck` | Suspicious 'typeof' check: {0} | — |
| `TernaryCanBeReplacedByItsCondition` | Ternary expression can be replaced with its {0}condition | — |
| `TsNotResolved` | Cannot resolve symbol '{0}' in '{1}' | — |
| `TsResolvedFromInaccessibleModule` | Symbol '{0}' cannot be properly resolved, probably it is located in inaccessible module | — |
| `TypeGuardDoesntAffectAnything` | Type guard does not affect the type: {0} | — |
| `TypeGuardProducesNeverType` | Type guard always produces 'never' type: {0} | — |
| `TypeParameterHidesTypeParamFromOuterScope` | Type parameter '{0}' hides type parameter declared in outer scope | — |
| `UnusedLocalImport` | Import '{0}' is never used | — |
| `UseAsInsteadOfTypeCast` | Type assertion can be replaced with 'as' operator | — |
| `UseOfPossiblyUnassignedProperty` | Use of possibly unassigned property in a static initializer | — |
| `WrongPublicModifierSpecification` | Your code style settings require {0} of 'public' modifier | — |
| `WrongRequireRelativePath` | Your code style settings require relative module paths to {0}start with './' | — |

## ASPX — 23 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `Asp.ContentPlaceholderNotResolved` | — | — |
| `Asp.CustomPageParserFilterType` | — | — |
| `Asp.DeadCode` | — | — |
| `Asp.Image` | — | — |
| `Asp.InvalidControlType` | — | — |
| `Asp.NotResolved` | — | — |
| `Asp.ResolveWarning` | — | — |
| `Asp.SkinNotResolved` | — | — |
| `Asp.TagAttributeWithOptionalValue` | — | — |
| `Asp.ThemeNotResolved` | — | — |
| `Asp.Warning` | — | — |
| `AspOdsMethodReferenceResolveError` | — | — |
| `AspUnusedRegisterDirectiveHighlighting` | — | — |
| `Mvc.ActionNotResolved` | — | — |
| `Mvc.AreaNotResolved` | — | — |
| `Mvc.ControllerNotResolved` | — | — |
| `Mvc.InvalidModelType` | — | — |
| `Mvc.MasterpageNotResolved` | — | — |
| `Mvc.PartialViewNotResolved` | — | — |
| `Mvc.TemplateNotResolved` | — | — |
| `Mvc.ViewComponentNotResolved` | — | — |
| `Mvc.ViewComponentViewNotResolved` | — | — |
| `Mvc.ViewNotResolved` | — | — |

## CSS — 22 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `CssBrowserCompatibility` | The newer '::' syntax is not compatible with {0} | — |
| `CssCaniuseFeatureRequiresPrefix` | {0} feature requires vendor-specific prefix for {1} | — |
| `CssCaniuseUnsupportedFeature` | {0} feature is not supported by {1} | — |
| `CssNotResolved` | Symbol '{0}' doesn't exist in CSS | — |
| `CssObsolete` | {0} | — |
| `CssPropertyDoesNotOverrideVendorProperty` | Property '{0}' should come after vendor-specific properties | — |
| `DeclarationIsEmpty` | Declaration is empty | — |
| `DoubleColonsExpected` | The pseudo-element must start with '::' | — |
| `DoubleColonsPreferred` | All specified browsers support the newer '::' syntax here | — |
| `HexColorValueWithAlpha` | Value of color with alpha component is not supported | — |
| `InvalidValue` | {0} | — |
| `PathNotResolved` | {0} | — |
| `Redundant` | {0} is redundant | — |
| `RedundantCssHack` | {0} is redundant according to the current browser compatibility configuration | — |
| `RedundantUnits` | Unit of measure is redundant | — |
| `RequiresFallbackColor` | Requires fallback color | — |
| `SyntaxIsNotAllowed` | Syntax is not allowed in CSS | — |
| `UnexpectedValue` | {0} | — |
| `UnknownCssClass` | Unknown CSS class '{0}' | — |
| `UnknownCssVariable` | Undeclared variable '{0}' | — |
| `UnknownCssVendorExtension` | Unknown vendor-specific extension '{0}' | — |
| `ValueShouldHaveUnits` | {0} | — |

## RouteTemplate — 17 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `RouteTemplates.ActionRoutePrefixCanBeExtractedToControllerRoute` | — | — |
| `RouteTemplates.AmbiguousMatchingConstraintConstructor` | — | — |
| `RouteTemplates.ConstraintArgumentCannotBeConverted` | — | — |
| `RouteTemplates.ControllerRouteParameterIsNotPassedToMethods` | — | — |
| `RouteTemplates.DuplicatedParameter` | — | — |
| `RouteTemplates.MatchingConstraintConstructorNotResolved` | — | — |
| `RouteTemplates.MethodMissingRouteParameters` | — | — |
| `RouteTemplates.OptionalParameterCanBePrecededOnlyBySinglePeriod` | — | — |
| `RouteTemplates.OptionalParameterMustBeAtTheEndOfSegment` | — | — |
| `RouteTemplates.ParameterConstraintCanBeSpecified` | — | — |
| `RouteTemplates.ParameterTypeAndConstraintsMismatch` | — | — |
| `RouteTemplates.ParameterTypeCanBeMadeStricter` | — | — |
| `RouteTemplates.RouteParameterConstraintNotResolved` | — | — |
| `RouteTemplates.RouteParameterIsNotPassedToMethod` | — | — |
| `RouteTemplates.RouteTokenNotResolved` | — | — |
| `RouteTemplates.SymbolNotResolved` | — | — |
| `RouteTemplates.SyntaxError` | — | — |

## HTML — 15 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `Asp.Entity` | — | — |
| `Html.AttributeNotResolved` | — | — |
| `Html.AttributeValueNotResolved` | — | — |
| `Html.AttributesQuotes` | — | — |
| `Html.DeadCode` | — | — |
| `Html.EventNotResolved` | — | — |
| `Html.IdDuplication` | — | — |
| `Html.IdNotResolved` | — | — |
| `Html.Obsolete` | — | — |
| `Html.PathError` | — | — |
| `Html.TagNotClosed` | — | — |
| `Html.TagNotResolved` | — | — |
| `Html.TagShouldBeSelfClosed` | — | — |
| `Html.TagShouldNotBeSelfClosed` | — | — |
| `Html.Warning` | — | — |

## Web.Config — 13 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `Web.IgnoredPath` | — | — |
| `Web.MappedPath` | — | — |
| `WebConfig.ModuleNotResolved` | — | — |
| `WebConfig.ModuleQualificationResolve` | — | — |
| `WebConfig.RedundantAddNamespaceTag` | — | — |
| `WebConfig.RedundantLocationTag` | — | — |
| `WebConfig.TagPrefixRedundand` | — | — |
| `WebConfig.TypeNotResolved` | — | — |
| `WebConfig.UnusedAddTag` | — | — |
| `WebConfig.UnusedElementDueToConfigSourceAttribute` | — | — |
| `WebConfig.UnusedRemoveOrClearTag` | — | — |
| `WebConfig.WebConfigPathWarning` | — | — |
| `WebConfig.WrongModule` | — | — |

## MSBUILD_BUILD_SCRIPT — 12 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `MissingAttribute` | — | — |
| `MultipleOutputTags` | — | — |
| `OutputTagRequired` | — | — |
| `UnknownItemGroup` | — | — |
| `UnknownMetadata` | — | — |
| `UnknownOutputParameter` | — | — |
| `UnknownProperty` | — | — |
| `UnknownTarget` | — | — |
| `UnknownTask` | — | — |
| `UnknownTaskAttribute` | — | — |
| `VulnerablePackage` | — | — |
| `WrongMetadataUse` | — | — |

## RESX — 10 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `DuplicateResource` | Duplicate resource '{0}' | — |
| `FormatStringPlaceholdersMismatch` | Format string placeholders mismatch | — |
| `InvalidValueType` | Resource type '{0}' is not equal to the base type '{1}' | — |
| `LocalizableElement` | Element is localizable | — |
| `NotDeclaredInParentCulture` | Resource is not declared in {0} | — |
| `NotOverriddenInSpecificCulture` | Resource is not overridden in {0} | — |
| `OverriddenWithEmptyValue` | Resource overrides base resource with empty value | — |
| `OverriddenWithSameValue` | Resource is overridden with identical value | — |
| `RedundantStringType` | Resource type is redundant | — |
| `ResxNotResolved` | {0} | — |

## CSHARP,VBASIC,ASPX — 3 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `PropertyNotResolved` | Only names of properties are allowed here | — |
| `ResourceItemNotResolved` | Cannot resolve resource item '{0}' | — |
| `ResourceNotResolved` | Cannot resolve resource '{0}' | — |

## Razor — 3 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `Razor.AssemblyNotResolved` | — | — |
| `Razor.UnresolvedComponent` | — | — |
| `RazorNullConditionalOperatorHighlighting` | — | — |

## Razor,CSHARP — 2 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `Razor.LayoutNotResolved` | — | — |
| `Razor.SectionNotResolved` | — | — |

## XML,RESX,XAML,HTML,Razor,ASPX,ASXX — 2 ids

| Id | What the rule reports | Coverage |
|---|---|---|
| `MarkupAttributeTypo` | Typo in markup attribute value: '{0}' | — |
| `MarkupTextTypo` | Typo in markup text: '{0}' | — |

## ASXX — 1 id

| Id | What the rule reports | Coverage |
|---|---|---|
| `Asxx.PathError` | — | — |

## Angular2HTML — 1 id

| Id | What the rule reports | Coverage |
|---|---|---|
| `AngularHtml.Banana` | — | — |

## Blazor — 1 id

| Id | What the rule reports | Coverage |
|---|---|---|
| `Blazor.EditorRequired` | — | — |

## CSHARP,VBASIC,JAVA_SCRIPT,Razor,ASPX,ASXX,TYPE_SCRIPT,CPP — 1 id

| Id | What the rule reports | Coverage |
|---|---|---|
| `StringLiteralTypo` | Typo in string: '{0}' | ✅ |

## CSHARP,VBASIC,Razor,ASPX,ASXX,XAML,JAVA_SCRIPT,TYPE_SCRIPT,CPP — 1 id

| Id | What the rule reports | Coverage |
|---|---|---|
| `IdentifierTypo` | Typo in identifier: '{0}' | — |

## CSHARP,VBASIC,Razor,ASPX,ASXX,XML,XAML,JAVA_SCRIPT,TYPE_SCRIPT,RESX,CPP — 1 id

| Id | What the rule reports | Coverage |
|---|---|---|
| `CommentTypo` | Typo in comment: '{0}' | — |

## CSHARP,VBASIC,XAML,JAVA_SCRIPT — 1 id

| Id | What the rule reports | Coverage |
|---|---|---|
| `InconsistentNaming` | Name '{0}' does not match rule '{1}'. Suggested name is '{2}'. | ✅ |

## REGULAR_EXPRESSION — 1 id

| Id | What the rule reports | Coverage |
|---|---|---|
| `RegExpInspections` | Quantifier {0} following nothing | — |

## The curated ids this installation no longer declares

The coverage map names these, and this Rider version has dropped them (a rename or a merged rule):
`RedundantBooleanLiteral`, `UsePreferredNamespaceBody`, `UseImplicitlyTypedLambda`, `AssignmentIsNotUsed`.
