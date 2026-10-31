using Microsoft.Extensions.DependencyInjection;
using Snitch.Platform;
using Snitch.Workspace;

namespace Snitch;

/// <summary>
/// Registers the Snitch code-intelligence library into a dependency-injection
/// container. A host application configures its identity via <see cref="SnitchOptions"/>
/// and then resolves the <c>I*Service</c> interfaces it needs.
/// </summary>
public static class SnitchServiceCollectionExtensions
{
    /// <summary>
    /// Adds Snitch's services and configuration to <paramref name="services"/>.
    /// All services are singletons; the workspace owns the single loaded solution
    /// and the rest are stateless wrappers over it.
    /// </summary>
    public static IServiceCollection AddSnitch(
        this IServiceCollection services, Action<SnitchOptions>? configure = null)
    {
        var options = new SnitchOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // SnitchContext is the environment (owns the opened solutions) *and* the
        // IRoslynWorkspace the feature services depend on — one object, one active solution.
        services.AddSingleton<SnitchContext>();
        services.AddSingleton<IRoslynWorkspace>(sp => sp.GetRequiredService<SnitchContext>());

        // On-disk context store: <solution>/.visualMCP/visualmcp.db, used for facts that are
        // expensive to recompute and useful across restarts.
        services.AddSingleton<Storage.IVisualMcpStore, Storage.VisualMcpStore>();

        // Feature services.
        services.AddSingleton<Git.IGitService, Git.GitService>();
        services.AddSingleton<Docker.IDockerService, Docker.DockerService>();
        services.AddSingleton<Search.ISearchTextService, Search.SearchTextService>();
        // Full-text index: FTS5 tables in the same solution store, one row per line of every source,
        // document and decompiled file, so a content question is a query and not a walk.
        services.AddSingleton<Search.IFullTextIndexService, Search.FullTextIndexService>();
        // Structural search: an AST pattern with $holes$, evaluated against the loaded solution and,
        // with a replacement, rewritten with the same compiler gate every refactoring uses.
        services.AddSingleton<Languages.CSharp.Search.IStructuralSearchService, Languages.CSharp.Search.StructuralSearchService>();
        // Code search: a text pattern whose hits are resolved to the declaration that contains them, so
        // "find it" and "show it" are one call instead of search_text followed by read_file.
        services.AddSingleton<Languages.CSharp.Search.ICodeSearchService, Languages.CSharp.Search.CodeSearchService>();
        services.AddSingleton<Execution.IEnvironmentInfoService, Execution.EnvironmentInfoService>();
        services.AddSingleton<Execution.IExtractBuildLogErrorsService, Execution.ExtractBuildLogErrorsService>();
        services.AddSingleton<Scaffolding.IScaffoldService, Scaffolding.ScaffoldService>();
        // Decompilation is registered by AddSnitchMetacode(): the implementation lives in
        // Snitch.Metacode, which registers it next to the model it produces.
        services.AddSingleton<Languages.CSharp.Reflection.IInspectAssemblyService, Languages.CSharp.Reflection.InspectAssemblyService>();
        services.AddSingleton<Languages.CSharp.Packages.INuGetPackagesService, Languages.CSharp.Packages.NuGetPackagesService>();
        // PackageInspectionService is registered by AddSnitchMetacode(): its DecompilePackageType chains
        // into the decompiler, which lives in Snitch.Metacode.
        services.AddSingleton<Languages.CSharp.Navigation.INavigationService, Languages.CSharp.Navigation.NavigationService>();
        services.AddSingleton<Languages.CSharp.Analysis.IRunCodeAnalysisService, Languages.CSharp.Analysis.RunCodeAnalysisService>();
        // The boundaries of a static call graph, made explicit: reflective lookups, generated trees, the
        // used surface of referenced assemblies, and the members frameworks call by convention.
        services.AddSingleton<Languages.CSharp.Analysis.IFindReflectionUsageService, Languages.CSharp.Analysis.FindReflectionUsageService>();
        services.AddSingleton<Languages.CSharp.Analysis.IGeneratedSourcesService, Languages.CSharp.Analysis.GeneratedSourcesService>();
        services.AddSingleton<Languages.CSharp.Analysis.IExternalUsagesService, Languages.CSharp.Analysis.ExternalUsagesService>();
        services.AddSingleton<Languages.CSharp.Analysis.IFrameworkEntryPointsService, Languages.CSharp.Analysis.FrameworkEntryPointsService>();
        services.AddSingleton<Execution.IBuildProjectService, Execution.BuildProjectService>();
        services.AddSingleton<Execution.IRunProjectService, Execution.RunProjectService>();
        services.AddSingleton<Execution.IRunCommandService, Execution.RunCommandService>();
        services.AddSingleton<GitHub.IGitHubService, GitHub.GitHubService>();
        services.AddSingleton<IO.IIoService, IO.IoService>();
        services.AddSingleton<Languages.CSharp.Documentation.IDocumentationService, Languages.CSharp.Documentation.DocumentationService>();
        services.AddSingleton<Languages.CSharp.SolutionManagement.ISolutionProjectsService, Languages.CSharp.SolutionManagement.SolutionProjectsService>();
        services.AddSingleton<Languages.CSharp.Packages.IAddNuGetPackageService, Languages.CSharp.Packages.AddNuGetPackageService>();
        services.AddSingleton<Languages.CSharp.Packages.INuGetManageService, Languages.CSharp.Packages.NuGetManageService>();
        services.AddSingleton<Languages.CSharp.Packages.ISearchNuGetPackagesService, Languages.CSharp.Packages.SearchNuGetPackagesService>();
        services.AddSingleton<Languages.CSharp.Benchmarking.IQuickBenchmarkService, Languages.CSharp.Benchmarking.QuickBenchmarkService>();
        services.AddSingleton<Languages.CSharp.Benchmarking.IRunBenchmarksService, Languages.CSharp.Benchmarking.RunBenchmarksService>();
        services.AddSingleton<Planning.ITaskChecklistService, Planning.TaskChecklistService>();
        services.AddSingleton<Planning.IBoardService, Planning.BoardService>();
        services.AddSingleton<Execution.IStopServerService, Execution.StopServerService>();
        services.AddSingleton<Languages.CSharp.Testing.IMutationTestingService, Languages.CSharp.Testing.MutationTestingService>();
        // The runtime half of analysis: the suite on both sides of a change, diffed test by test.
        services.AddSingleton<Languages.CSharp.Testing.IVerifyChangeService, Languages.CSharp.Testing.VerifyChangeService>();
        services.AddSingleton<Runtime.IHttpFileService, Runtime.HttpFileService>();
        services.AddSingleton<IFixLineEndingsService, FixLineEndingsService>();
        services.AddSingleton<Web.IDownloadFileService, Web.DownloadFileService>();

        // Razor: .cshtml/.razor files are content, not Roslyn documents, so these services read them
        // from disk and run the Razor scanner/rules over the markup.
        services.AddSingleton<Languages.Razor.IRazorPagesService, Languages.Razor.RazorPagesService>();
        services.AddSingleton<Languages.Razor.IAnalyzeRazorPagesService, Languages.Razor.AnalyzeRazorPagesService>();
        services.AddSingleton<Languages.Razor.IFindRazorVulnerabilitiesService, Languages.Razor.FindRazorVulnerabilitiesService>();
        // The write side: verified splices that are re-scanned before they are written, and recorded
        // in the edit history so revert_edit can undo them.
        services.AddSingleton<Languages.Razor.IRazorEditService, Languages.Razor.RazorEditService>();

        // C# analysis services (logic moved from the MCP tool layer).
        services.AddSingleton<Languages.CSharp.Analysis.IGetDiagnosticsService, Languages.CSharp.Analysis.GetDiagnosticsService>();
        // The compiler's own diagnostics, rewritten instead of reported: apply_code_fix runs a pack's
        // CodeFixProvider, and compiler diagnostics (CS####) ship none — this is that family.
        services.AddSingleton<Languages.CSharp.Analysis.IFixDiagnosticsService, Languages.CSharp.Analysis.FixDiagnosticsService>();
        // "Where does a refactoring pay first?" — git churn joined with the complexity ranking.
        services.AddSingleton<Languages.CSharp.Analysis.IGitChurnHotspotsService, Languages.CSharp.Analysis.GitChurnHotspotsService>();
        services.AddSingleton<Languages.CSharp.Analysis.IFindUnusedSymbolsService, Languages.CSharp.Analysis.FindUnusedSymbolsService>();
        services.AddSingleton<Languages.CSharp.Analysis.IAnalyzeNamespaceCouplingService, Languages.CSharp.Analysis.AnalyzeNamespaceCouplingService>();
        services.AddSingleton<Languages.CSharp.Analysis.IAnalyzeDependenciesService, Languages.CSharp.Analysis.AnalyzeDependenciesService>();
        services.AddSingleton<Languages.CSharp.Analysis.IAnalyzeCircularNamespaceDependenciesService, Languages.CSharp.Analysis.AnalyzeCircularNamespaceDependenciesService>();
        services.AddSingleton<Languages.CSharp.Analysis.IFindDeadCodeService, Languages.CSharp.Analysis.FindDeadCodeService>();
        services.AddSingleton<Languages.CSharp.Analysis.IGetComplexityHotspotsService, Languages.CSharp.Analysis.GetComplexityHotspotsService>();
        services.AddSingleton<Languages.CSharp.Analysis.IGetMetricsService, Languages.CSharp.Analysis.GetMetricsService>();
        services.AddSingleton<Languages.CSharp.Analysis.IAnalyzeComplexityService, Languages.CSharp.Analysis.AnalyzeComplexityService>();
        services.AddSingleton<Languages.CSharp.Analysis.IComplexityReportService, Languages.CSharp.Analysis.ComplexityReportService>();
        services.AddSingleton<Languages.CSharp.Analysis.IAnalyzeDependencyInjectionService, Languages.CSharp.Analysis.AnalyzeDependencyInjectionService>();
        services.AddSingleton<Languages.CSharp.Analysis.IFindAsyncAntiPatternsService, Languages.CSharp.Analysis.FindAsyncAntiPatternsService>();
        services.AddSingleton<Languages.CSharp.Analysis.IFindCodeSmellsService, Languages.CSharp.Analysis.FindCodeSmellsService>();
        services.AddSingleton<Languages.CSharp.Analysis.IFindDuplicateCodeService, Languages.CSharp.Analysis.FindDuplicateCodeService>();
        services.AddSingleton<Languages.CSharp.Analysis.IFindSecurityIssuesService, Languages.CSharp.Analysis.FindSecurityIssuesService>();
        services.AddSingleton<Languages.CSharp.Analysis.IPlanFeatureChangeService, Languages.CSharp.Analysis.PlanFeatureChangeService>();
        // Architecture, cohesion, test quality, argument validation, nullability and API compatibility:
        // the questions the complexity report and the repair planner do not answer.
        services.AddSingleton<Languages.CSharp.Analysis.ICheckLayerViolationsService, Languages.CSharp.Analysis.CheckLayerViolationsService>();
        services.AddSingleton<Languages.CSharp.Analysis.IExportDependencyGraphService, Languages.CSharp.Analysis.ExportDependencyGraphService>();
        services.AddSingleton<Languages.CSharp.Analysis.IGetCohesionMetricsService, Languages.CSharp.Analysis.GetCohesionMetricsService>();
        services.AddSingleton<Languages.CSharp.Analysis.IAnalyzeTestQualityService, Languages.CSharp.Analysis.AnalyzeTestQualityService>();
        services.AddSingleton<Languages.CSharp.Analysis.IFindMissingValidationService, Languages.CSharp.Analysis.FindMissingValidationService>();
        services.AddSingleton<Languages.CSharp.Analysis.IAnalyzeNullabilityService, Languages.CSharp.Analysis.AnalyzeNullabilityService>();
        services.AddSingleton<Languages.CSharp.Analysis.IFindBreakingChangesService, Languages.CSharp.Analysis.FindBreakingChangesService>();
        services.AddSingleton<Languages.CSharp.Analysis.Performance.IPerformanceAnalyzer, Languages.CSharp.Analysis.Performance.PerformancePatternAnalyzer>();

        // C# refactoring services (logic moved from the MCP tool layer).
        services.AddSingleton<Languages.CSharp.Refactoring.IApplyRenameService, Languages.CSharp.Refactoring.ApplyRenameService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IEncapsulateFieldService, Languages.CSharp.Refactoring.EncapsulateFieldService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IExtractInterfaceService, Languages.CSharp.Refactoring.ExtractInterfaceService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IExtractMethodCandidatesService, Languages.CSharp.Refactoring.ExtractMethodCandidatesService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IGenerateMembersService, Languages.CSharp.Refactoring.GenerateMembersService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IChangeSignatureService, Languages.CSharp.Refactoring.ChangeSignatureService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IImplementInpcService, Languages.CSharp.Refactoring.ImplementInpcService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IInlineSymbolService, Languages.CSharp.Refactoring.InlineSymbolService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IIntroduceVariableService, Languages.CSharp.Refactoring.IntroduceVariableService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IMoveTypeService, Languages.CSharp.Refactoring.MoveTypeService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IOptimizeUsingsService, Languages.CSharp.Refactoring.OptimizeUsingsService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IPreviewRenameService, Languages.CSharp.Refactoring.PreviewRenameService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IPullMembersUpService, Languages.CSharp.Refactoring.PullMembersUpService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IRemoveRegionsService, Languages.CSharp.Refactoring.RemoveRegionsService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IReorderMembersService, Languages.CSharp.Refactoring.ReorderMembersService>();
        services.AddSingleton<Languages.CSharp.Refactoring.ISafeDeleteService, Languages.CSharp.Refactoring.SafeDeleteService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IPropertyToAutoService, Languages.CSharp.Refactoring.PropertyToAutoService>();
        services.AddSingleton<Languages.CSharp.Refactoring.ITypeToPartialService, Languages.CSharp.Refactoring.TypeToPartialService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IMakeStaticService, Languages.CSharp.Refactoring.MakeStaticService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IFunctionToPropertyService, Languages.CSharp.Refactoring.FunctionToPropertyService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IStaticToExtensionService, Languages.CSharp.Refactoring.StaticToExtensionService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IIntroduceFieldService, Languages.CSharp.Refactoring.IntroduceFieldService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IExtractMethodService, Languages.CSharp.Refactoring.ExtractMethodService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IIntroduceParameterService, Languages.CSharp.Refactoring.IntroduceParameterService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IInvertIfService, Languages.CSharp.Refactoring.InvertIfService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IAddParamsModifierService, Languages.CSharp.Refactoring.AddParamsModifierService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IChangeNullabilityService, Languages.CSharp.Refactoring.ChangeNullabilityService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IRenameTupleComponentService, Languages.CSharp.Refactoring.RenameTupleComponentService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IGlobalUsingsService, Languages.CSharp.Refactoring.GlobalUsingsService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IChangeTypeService, Languages.CSharp.Refactoring.ChangeTypeService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IMoveTypeToOuterScopeService, Languages.CSharp.Refactoring.MoveTypeToOuterScopeService>();
        services.AddSingleton<Languages.CSharp.Refactoring.ICopyTypeService, Languages.CSharp.Refactoring.CopyTypeService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IConstructorToFactoryService, Languages.CSharp.Refactoring.ConstructorToFactoryService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IUseBaseTypeService, Languages.CSharp.Refactoring.UseBaseTypeService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IConvertToPrimaryConstructorService, Languages.CSharp.Refactoring.ConvertToPrimaryConstructorService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IPushMembersDownService, Languages.CSharp.Refactoring.PushMembersDownService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IExtractClassService, Languages.CSharp.Refactoring.ExtractClassService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IPropertyToFunctionService, Languages.CSharp.Refactoring.PropertyToFunctionService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IInlineParameterService, Languages.CSharp.Refactoring.InlineParameterService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IExtractSuperclassService, Languages.CSharp.Refactoring.ExtractSuperclassService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IConvertAnonymousToTypeService, Languages.CSharp.Refactoring.ConvertAnonymousToTypeService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IRenameNamespaceService, Languages.CSharp.Refactoring.RenameNamespaceService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IMoveToNamespaceService, Languages.CSharp.Refactoring.MoveToNamespaceService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IReduceComplexityService, Languages.CSharp.Refactoring.ReduceComplexityService>();
        // The one-call entry point over the complexity family: rank the solution, lower the worst methods,
        // aggregate the runs. It composes the report and the reducer instead of duplicating their logic.
        services.AddSingleton<Languages.CSharp.Refactoring.IFixComplexityService, Languages.CSharp.Refactoring.FixComplexityService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IMoveToFolderService, Languages.CSharp.Refactoring.MoveToFolderService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IConvertToRecordService, Languages.CSharp.Refactoring.ConvertToRecordService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IIntroducePartOfStringService, Languages.CSharp.Refactoring.IntroducePartOfStringService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IMoveStaticMembersService, Languages.CSharp.Refactoring.MoveStaticMembersService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IMoveInstanceMethodService, Languages.CSharp.Refactoring.MoveInstanceMethodService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IInlineClassService, Languages.CSharp.Refactoring.InlineClassService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IIndexerConversionsService, Languages.CSharp.Refactoring.IndexerConversionsService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IImplicitUsingsService, Languages.CSharp.Refactoring.ImplicitUsingsService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IConvertToNonGlobalUsingService, Languages.CSharp.Refactoring.ConvertToNonGlobalUsingService>();
        services.AddSingleton<Languages.CSharp.Style.ISimplifyCodeService, Languages.CSharp.Style.SimplifyCodeService>();
        // Formatting as its own step, and the cleanup that composes the rewrite pipeline.
        services.AddSingleton<Languages.CSharp.Refactoring.IFormatCodeService, Languages.CSharp.Refactoring.FormatCodeService>();
        services.AddSingleton<Languages.CSharp.Refactoring.ICodeCleanupService, Languages.CSharp.Refactoring.CodeCleanupService>();
        // The "create" family ReSharper's context actions cover: missing members, doc comments, and a
        // stub derived from a call site that does not exist yet.
        services.AddSingleton<Languages.CSharp.Refactoring.IImplementMembersService, Languages.CSharp.Refactoring.ImplementMembersService>();
        services.AddSingleton<Languages.CSharp.Refactoring.IGenerateDocsService, Languages.CSharp.Refactoring.GenerateDocsService>();
        services.AddSingleton<Languages.CSharp.Refactoring.ICreateFromUsageService, Languages.CSharp.Refactoring.CreateFromUsageService>();
        // The context actions that are neither IDE-style rewrites nor refactorings: import type, argument
        // guards, surround-with, and the string-literal conversions. One service, one action id per family.
        services.AddSingleton<Languages.CSharp.ContextActions.IContextActionService, Languages.CSharp.ContextActions.ContextActionService>();

        // Solution store: package facts and the edit history. The symbol index is registered by
        // AddSnitchMetacode(): its implementation and its tables live in Snitch.Metacode.
        services.AddSingleton<Languages.CSharp.Analysis.IPackageFactsService, Languages.CSharp.Analysis.PackageFactsService>();
        services.AddSingleton<Storage.IEditHistoryService, Storage.EditHistoryService>();

        // Modernization: Aspire installed and scaffolded on demand, the target framework of the
        // projects and the NuGet versions that belong to it.
        services.AddSingleton<Languages.CSharp.Modernization.IAspireService, Languages.CSharp.Modernization.AspireService>();
        services.AddSingleton<Languages.CSharp.Modernization.ISolutionUpgradeService, Languages.CSharp.Modernization.SolutionUpgradeService>();

        // C# testing services (logic moved from the MCP tool layer).
        services.AddSingleton<Languages.CSharp.Testing.IGetTestCoverageMapService, Languages.CSharp.Testing.GetTestCoverageMapService>();
        services.AddSingleton<Languages.CSharp.Testing.IRunTestsService, Languages.CSharp.Testing.RunTestsService>();

        // Runtime services.
        services.AddSingleton<Runtime.IHttpInvokeService, Runtime.HttpInvokeService>();
        services.AddSingleton<Runtime.ISignalRSessionService, Runtime.SignalRSessionService>();
        services.AddSingleton<Runtime.ISignalRService, Runtime.SignalRService>();
        return services;
    }
}
