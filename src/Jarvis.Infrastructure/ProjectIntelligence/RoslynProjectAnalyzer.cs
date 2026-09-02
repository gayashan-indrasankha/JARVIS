using System.Security.Cryptography;
using System.Text;
using Jarvis.Core.ProjectIntelligence;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Jarvis.Infrastructure.ProjectIntelligence;

internal sealed class RoslynProjectAnalyzer
{
    private readonly CSharpParseOptions _parseOptions = new(
        LanguageVersion.Preview,
        DocumentationMode.Parse);

    private static readonly string[] DiMethods =
    [
        "AddSingleton",
        "AddScoped",
        "AddTransient",
        "TryAddSingleton",
        "TryAddScoped",
        "TryAddTransient",
    ];

    private static readonly string[] AuthenticationMethods =
    [
        "AddAuthentication",
        "AddAuthorization",
        "AddJwtBearer",
        "AddCookie",
        "AddIdentity",
        "UseAuthentication",
        "UseAuthorization",
    ];

    private static readonly string[] DatabaseMethods =
    [
        "UseSqlServer",
        "UseSqlite",
        "UseNpgsql",
        "UseMySql",
        "UseOracle",
        "AddDbContext",
        "AddDbContextPool",
    ];

    public async ValueTask<ProjectAnalysisSnapshot> AnalyzeAsync(
        IReadOnlyList<StaticProject> staticProjects,
        IReadOnlyList<IndexedFile> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staticProjects);
        ArgumentNullException.ThrowIfNull(files);
        using AdhocWorkspace workspace = new();
        Dictionary<string, ProjectId> projectIds = CreateProjects(workspace, staticProjects);
        Solution solution = workspace.CurrentSolution;
        AddProjectReferences(ref solution, staticProjects, projectIds);
        Dictionary<DocumentId, IndexedFile> documentFiles = AddDocuments(
            ref solution,
            staticProjects,
            files,
            projectIds);
        if (!workspace.TryApplyChanges(solution))
        {
            throw new ProjectIndexException("roslyn_workspace_load_failed");
        }

        List<IndexedSymbol> symbols = [];
        List<IndexedFact> facts = CreateProjectFacts(staticProjects, files);
        Dictionary<ISymbol, List<IndexedSymbol>> symbolMap = new(SymbolEqualityComparer.Default);
        List<DocumentContext> documents = [];

        foreach ((DocumentId documentId, IndexedFile file) in documentFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Document? document = workspace.CurrentSolution.GetDocument(documentId);
            if (document is null)
            {
                continue;
            }

            SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken)
                .ConfigureAwait(false);
            if (root is null || semanticModel is null)
            {
                continue;
            }

            DocumentContext context = new(document, root, semanticModel, file);
            documents.Add(context);
            ExtractSymbols(context, symbols, symbolMap, facts, cancellationToken);
        }

        List<IndexedRelationship> relationships = [];
        foreach (DocumentContext context in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractRelationships(context, symbolMap, relationships, facts, cancellationToken);
        }

        return new ProjectAnalysisSnapshot(
            staticProjects,
            symbols,
            relationships
                .DistinctBy(static relation =>
                    $"{relation.SourceSymbolId}|{relation.TargetSymbolId}|{relation.Kind}|{relation.RelativePath}|{relation.StartLine}",
                    StringComparer.Ordinal)
                .ToArray(),
            facts
                .DistinctBy(static fact =>
                    $"{fact.Kind}|{fact.Name}|{fact.RelativePath}|{fact.StartLine}",
                    StringComparer.Ordinal)
                .ToArray());
    }

    private Dictionary<string, ProjectId> CreateProjects(
        AdhocWorkspace workspace,
        IReadOnlyList<StaticProject> projects)
    {
        Dictionary<string, ProjectId> ids = new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<StaticProject> effectiveProjects = projects.Count == 0
            ? [new StaticProject("__repository__.csproj", "Repository", "Repository", "Repository", [], [], [], false, "Library")]
            : projects;
        MetadataReference[] references = GetPlatformReferences();
        foreach (StaticProject project in effectiveProjects)
        {
            ProjectId id = ProjectId.CreateNewId(project.RelativePath);
            ids[project.RelativePath] = id;
            workspace.AddProject(ProjectInfo.Create(
                id,
                VersionStamp.Create(),
                project.Name,
                project.AssemblyName,
                LanguageNames.CSharp,
                filePath: project.RelativePath,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: false,
                    deterministic: true),
                parseOptions: _parseOptions,
                metadataReferences: references));
        }

        return ids;
    }

    private static void AddProjectReferences(
        ref Solution solution,
        IReadOnlyList<StaticProject> projects,
        Dictionary<string, ProjectId> ids)
    {
        foreach (StaticProject project in projects)
        {
            if (!ids.TryGetValue(project.RelativePath, out ProjectId? sourceId))
            {
                continue;
            }

            foreach (string reference in project.ProjectReferences)
            {
                if (ids.TryGetValue(reference, out ProjectId? targetId) && sourceId != targetId)
                {
                    solution = solution.AddProjectReference(sourceId, new ProjectReference(targetId));
                }
            }
        }
    }

    private static Dictionary<DocumentId, IndexedFile> AddDocuments(
        ref Solution solution,
        IReadOnlyList<StaticProject> projects,
        IReadOnlyList<IndexedFile> files,
        IReadOnlyDictionary<string, ProjectId> ids)
    {
        Dictionary<DocumentId, IndexedFile> result = [];
        foreach (IndexedFile file in files.Where(static file => file.Kind == IndexedFileKind.Source))
        {
            ProjectId projectId = ResolveOwningProject(file.RelativePath, projects, ids);
            DocumentId documentId = DocumentId.CreateNewId(projectId, file.RelativePath);
            solution = solution.AddDocument(
                documentId,
                Path.GetFileName(file.RelativePath),
                SourceText.From(file.Content, Encoding.UTF8),
                filePath: file.RelativePath);
            result[documentId] = file;
        }

        return result;
    }

    private static ProjectId ResolveOwningProject(
        string sourcePath,
        IReadOnlyList<StaticProject> projects,
        IReadOnlyDictionary<string, ProjectId> ids)
    {
        StaticProject? project = projects
            .Where(candidate => IsBelowDirectory(sourcePath, GetDirectory(candidate.RelativePath)))
            .OrderByDescending(candidate => GetDirectory(candidate.RelativePath).Length)
            .FirstOrDefault();
        return project is not null
            ? ids[project.RelativePath]
            : ids.Values.First();
    }

    private static void ExtractSymbols(
        DocumentContext context,
        List<IndexedSymbol> symbols,
        Dictionary<ISymbol, List<IndexedSymbol>> symbolMap,
        List<IndexedFact> facts,
        CancellationToken cancellationToken)
    {
        foreach (BaseNamespaceDeclarationSyntax declaration in
            context.Root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            INamespaceSymbol? namespaceSymbol = context.SemanticModel.GetDeclaredSymbol(
                declaration,
                cancellationToken) as INamespaceSymbol;
            string qualifiedName = namespaceSymbol?.ToDisplayString() ?? declaration.Name.ToString();
            FileLinePositionSpan span = declaration.Name.GetLocation().GetLineSpan();
            IndexedSymbol indexed = new(
                CreateStableId(context.File.RelativePath, span.StartLinePosition.Line + 1, qualifiedName),
                context.File.RelativePath,
                context.Document.Project.FilePath,
                ProjectSymbolKind.Namespace,
                namespaceSymbol?.Name ?? declaration.Name.ToString().Split('.').Last(),
                qualifiedName,
                qualifiedName,
                span.StartLinePosition.Line + 1,
                span.EndLinePosition.Line + 1,
                declaration.Name.ToString(),
                context.File.ContentHash);
            symbols.Add(indexed);
            if (namespaceSymbol is not null)
            {
                AddSymbolDeclaration(namespaceSymbol, indexed);
            }

            AddFact("namespace", qualifiedName, qualifiedName, declaration.Name, qualifiedName);
        }

        foreach (MemberDeclarationSyntax declaration in
            context.Root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<ISymbol?> declaredSymbols = declaration switch
            {
                FieldDeclarationSyntax field => field.Declaration.Variables.Select(variable =>
                    context.SemanticModel.GetDeclaredSymbol(variable, cancellationToken)),
                EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.Select(variable =>
                    context.SemanticModel.GetDeclaredSymbol(variable, cancellationToken)),
                _ => [GetDeclaredSymbol(declaration)],
            };
            foreach (ISymbol? symbol in declaredSymbols)
            {
                if (symbol is null || !TryMapKind(symbol, out ProjectSymbolKind kind))
                {
                    continue;
                }

                FileLinePositionSpan span = declaration.GetLocation().GetLineSpan();
                string qualifiedName = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                string id = CreateStableId(
                    context.File.RelativePath,
                    span.StartLinePosition.Line + 1,
                    qualifiedName);
                IndexedSymbol indexed = new(
                    id,
                    context.File.RelativePath,
                    context.Document.Project.FilePath,
                    kind,
                    symbol.Name,
                    qualifiedName,
                    symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                    span.StartLinePosition.Line + 1,
                    span.EndLinePosition.Line + 1,
                    CreateDeclarationExcerpt(declaration),
                    context.File.ContentHash);
                symbols.Add(indexed);
                AddSymbolDeclaration(symbol, indexed);

                if (symbol is INamedTypeSymbol typeSymbol)
                {
                    ExtractTypeFacts(context, declaration, typeSymbol, indexed, facts);
                }
            }
        }

        return;

        void AddFact(
            string kind,
            string name,
            string detail,
            SyntaxNode node,
            string? symbol)
        {
            FileLinePositionSpan span = node.GetLocation().GetLineSpan();
            facts.Add(new IndexedFact(
                kind,
                name,
                detail,
                context.File.RelativePath,
                span.StartLinePosition.Line + 1,
                span.EndLinePosition.Line + 1,
                symbol,
                CreateDeclarationExcerpt(node),
                context.File.ContentHash));
        }

        void AddSymbolDeclaration(ISymbol symbol, IndexedSymbol indexed)
        {
            if (!symbolMap.TryGetValue(symbol, out List<IndexedSymbol>? declarations))
            {
                declarations = [];
                symbolMap.Add(symbol, declarations);
            }

            declarations.Add(indexed);
        }

        ISymbol? GetDeclaredSymbol(MemberDeclarationSyntax declaration) => declaration switch
        {
            BaseTypeDeclarationSyntax type => context.SemanticModel.GetDeclaredSymbol(type, cancellationToken),
            DelegateDeclarationSyntax @delegate => context.SemanticModel.GetDeclaredSymbol(@delegate, cancellationToken),
            MethodDeclarationSyntax method => context.SemanticModel.GetDeclaredSymbol(method, cancellationToken),
            ConstructorDeclarationSyntax constructor => context.SemanticModel.GetDeclaredSymbol(constructor, cancellationToken),
            PropertyDeclarationSyntax property => context.SemanticModel.GetDeclaredSymbol(property, cancellationToken),
            EventDeclarationSyntax @event => context.SemanticModel.GetDeclaredSymbol(@event, cancellationToken),
            _ => null,
        };
    }

    private static void ExtractTypeFacts(
        DocumentContext context,
        MemberDeclarationSyntax declaration,
        INamedTypeSymbol type,
        IndexedSymbol indexed,
        List<IndexedFact> facts)
    {
        bool controller = type.Name.EndsWith("Controller", StringComparison.Ordinal) ||
            InheritanceContains(type, "ControllerBase") ||
            InheritanceContains(type, "Controller");
        if (controller)
        {
            facts.Add(CreateFact("controller", type.Name, indexed.QualifiedName, declaration, indexed));
        }

        if (InheritanceContains(type, "DbContext"))
        {
            facts.Add(CreateFact("db_context", type.Name, indexed.QualifiedName, declaration, indexed));
        }

        if (type.TypeKind == TypeKind.Interface)
        {
            facts.Add(CreateFact("interface", type.Name, indexed.QualifiedName, declaration, indexed));
        }

        foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.Type is INamedTypeSymbol propertyType && propertyType.Name == "DbSet" &&
                propertyType.TypeArguments.Length == 1)
            {
                SyntaxReference? syntax = property.DeclaringSyntaxReferences.FirstOrDefault();
                SyntaxNode evidenceNode = syntax?.GetSyntax() ?? declaration;
                facts.Add(CreateFact(
                    "entity",
                    propertyType.TypeArguments[0].Name,
                    $"{type.Name}.{property.Name} exposes DbSet<{propertyType.TypeArguments[0].ToDisplayString()}>",
                    evidenceNode,
                    indexed));
            }
        }

        foreach (AttributeData attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.Name is "AuthorizeAttribute" or "AllowAnonymousAttribute")
            {
                facts.Add(CreateFact(
                    "authentication",
                    attribute.AttributeClass.Name,
                    $"{indexed.QualifiedName} has {attribute.AttributeClass.Name}",
                    declaration,
                    indexed));
            }
        }

        return;

        IndexedFact CreateFact(
            string kind,
            string name,
            string detail,
            SyntaxNode node,
            IndexedSymbol symbol)
        {
            FileLinePositionSpan span = node.GetLocation().GetLineSpan();
            return new IndexedFact(
                kind,
                name,
                detail,
                context.File.RelativePath,
                span.StartLinePosition.Line + 1,
                span.EndLinePosition.Line + 1,
                symbol.QualifiedName,
                CreateDeclarationExcerpt(node),
                context.File.ContentHash);
        }
    }

    private static void ExtractRelationships(
        DocumentContext context,
        IReadOnlyDictionary<ISymbol, List<IndexedSymbol>> symbolMap,
        List<IndexedRelationship> relationships,
        List<IndexedFact> facts,
        CancellationToken cancellationToken)
    {
        foreach ((ISymbol sourceSymbol, List<IndexedSymbol> declarations) in symbolMap)
        {
            foreach (IndexedSymbol source in declarations.Where(declaration =>
                declaration.RelativePath.Equals(context.File.RelativePath, StringComparison.OrdinalIgnoreCase)))
            {
                if (sourceSymbol is not INamedTypeSymbol type)
                {
                    continue;
                }

                if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
                {
                    AddRelationship(source, baseType, ProjectRelationshipKind.Inherits, source.StartLine, source.EndLine);
                }

                foreach (INamedTypeSymbol @interface in type.Interfaces)
                {
                    AddRelationship(source, @interface, ProjectRelationshipKind.Implements, source.StartLine, source.EndLine);
                }
            }
        }

        foreach (InvocationExpressionSyntax invocation in
            context.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string methodName = GetInvocationName(invocation);
            ISymbol? called = context.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol;
            ISymbol? enclosing = context.SemanticModel.GetEnclosingSymbol(invocation.SpanStart, cancellationToken);
            IndexedSymbol? source = FindIndexedContainingSymbol(
                enclosing,
                symbolMap,
                context.File.RelativePath);
            FileLinePositionSpan span = invocation.GetLocation().GetLineSpan();
            int invocationLine = span.StartLinePosition.Line + 1;
            source ??= symbolMap.Values.SelectMany(static declarations => declarations)
                .Where(symbol => symbol.RelativePath.Equals(
                        context.File.RelativePath,
                        StringComparison.OrdinalIgnoreCase) &&
                    symbol.StartLine <= invocationLine && symbol.EndLine >= invocationLine &&
                    symbol.Kind is ProjectSymbolKind.Method or ProjectSymbolKind.Constructor or
                        ProjectSymbolKind.Property)
                .OrderBy(static symbol => symbol.EndLine - symbol.StartLine)
                .FirstOrDefault();
            if (source is not null && called is not null)
            {
                AddRelationship(
                    source,
                    called.OriginalDefinition,
                    ProjectRelationshipKind.Calls,
                    span.StartLinePosition.Line + 1,
                    span.EndLinePosition.Line + 1);
            }
            else if (source is not null)
            {
                if (!string.IsNullOrWhiteSpace(methodName))
                {
                    relationships.Add(new IndexedRelationship(
                        source.Id,
                        null,
                        source.QualifiedName,
                        methodName,
                        ProjectRelationshipKind.Calls,
                        context.File.RelativePath,
                        span.StartLinePosition.Line + 1,
                        span.EndLinePosition.Line + 1,
                        context.File.ContentHash));
                }
            }

            if (DiMethods.Contains(methodName, StringComparer.Ordinal))
            {
                facts.Add(CreateInvocationFact("di_registration", methodName, invocation, source));
            }

            if (AuthenticationMethods.Contains(methodName, StringComparer.Ordinal))
            {
                facts.Add(CreateInvocationFact("authentication", methodName, invocation, source));
            }

            if (DatabaseMethods.Contains(methodName, StringComparer.Ordinal))
            {
                facts.Add(CreateInvocationFact("database", methodName, invocation, source));
            }

            if (methodName is "MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch" &&
                invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax route)
            {
                string detail = $"{methodName[3..].ToUpperInvariant()} {route.Token.ValueText}";
                facts.Add(CreateInvocationFact("endpoint", detail, invocation, source));
            }
        }

        foreach (MethodDeclarationSyntax method in
            context.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            string? httpMethod = null;
            string? route = null;
            foreach (AttributeSyntax attribute in method.AttributeLists.SelectMany(static list => list.Attributes))
            {
                string name = attribute.Name.ToString().Split('.').Last();
                if (name.StartsWith("Http", StringComparison.Ordinal) && name.Length > 4)
                {
                    httpMethod = name[4..].Replace("Attribute", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
                    route = GetFirstStringArgument(attribute) ?? route;
                }

                if (name is "Route" or "RouteAttribute")
                {
                    route = GetFirstStringArgument(attribute) ?? route;
                }
            }

            if (httpMethod is not null)
            {
                TypeDeclarationSyntax? containingType = method.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                string? prefix = containingType?.AttributeLists
                    .SelectMany(static list => list.Attributes)
                    .Where(static attribute => attribute.Name.ToString().Split('.').Last() is "Route" or "RouteAttribute")
                    .Select(GetFirstStringArgument)
                    .FirstOrDefault(static value => value is not null);
                string combined = CombineRoute(prefix, route);
                ISymbol? methodSymbol = context.SemanticModel.GetDeclaredSymbol(method, cancellationToken);
                IndexedSymbol? indexed = FindIndexedContainingSymbol(
                    methodSymbol,
                    symbolMap,
                    context.File.RelativePath);
                facts.Add(CreateInvocationFact(
                    "endpoint",
                    $"{httpMethod} {combined} -> {indexed?.QualifiedName ?? method.Identifier.ValueText}",
                    method,
                    indexed));
            }
        }

        void AddRelationship(
            IndexedSymbol source,
            ISymbol targetSymbol,
            ProjectRelationshipKind kind,
            int startLine,
            int endLine)
        {
            IndexedSymbol? target = FindIndexedSymbol(targetSymbol, symbolMap);
            relationships.Add(new IndexedRelationship(
                source.Id,
                target?.Id,
                source.QualifiedName,
                target?.QualifiedName ?? targetSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                kind,
                context.File.RelativePath,
                startLine,
                endLine,
                context.File.ContentHash));
        }

        IndexedFact CreateInvocationFact(
            string kind,
            string name,
            SyntaxNode node,
            IndexedSymbol? source)
        {
            FileLinePositionSpan span = node.GetLocation().GetLineSpan();
            return new IndexedFact(
                kind,
                name,
                $"{name}: {CreateDeclarationExcerpt(node)}",
                context.File.RelativePath,
                span.StartLinePosition.Line + 1,
                span.EndLinePosition.Line + 1,
                source?.QualifiedName,
                CreateDeclarationExcerpt(node),
                context.File.ContentHash);
        }
    }

    private static List<IndexedFact> CreateProjectFacts(
        IReadOnlyList<StaticProject> projects,
        IReadOnlyList<IndexedFile> files)
    {
        Dictionary<string, IndexedFile> byPath = files.ToDictionary(
            static file => file.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        List<IndexedFact> facts = [];
        foreach (StaticProject project in projects)
        {
            IndexedFile file = byPath[project.RelativePath];
            facts.Add(CreateLineFact(
                "project",
                project.Name,
                $"Project file {project.RelativePath} is present.",
                file,
                "<Project"));
            if (project.IsTestProject)
            {
                facts.Add(CreateLineFact("test_project", project.Name, project.RelativePath, file, project.Name));
            }

            foreach (PackageReferenceInfo package in project.PackageReferences)
            {
                facts.Add(CreateLineFact(
                    "package_reference",
                    package.Name,
                    package.Version is null ? package.Name : $"{package.Name} {package.Version}",
                    file,
                    package.Name));
            }

            foreach (string reference in project.ProjectReferences)
            {
                facts.Add(CreateLineFact(
                    "project_reference",
                    reference,
                    $"{project.RelativePath} references {reference}",
                    file,
                    Path.GetFileName(reference)));
            }
        }

        foreach (IndexedFile solution in files.Where(static file => file.Kind == IndexedFileKind.Solution))
        {
            facts.Add(CreateLineFact(
                "solution",
                Path.GetFileName(solution.RelativePath),
                $"Solution file {solution.RelativePath} is present.",
                solution,
                Path.GetFileName(solution.RelativePath)));
        }
        foreach (IndexedFile documentation in files.Where(static file =>
            file.Kind == IndexedFileKind.Documentation &&
            Path.GetFileName(file.RelativePath).StartsWith("README", StringComparison.OrdinalIgnoreCase)))
        {
            string[] lines = documentation.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            string excerpt = string.Join('\n', lines.Take(12)).Trim();
            facts.Add(new IndexedFact(
                "repository_documentation",
                Path.GetFileName(documentation.RelativePath),
                excerpt,
                documentation.RelativePath,
                1,
                Math.Max(1, Math.Min(lines.Length, 12)),
                null,
                TrimExcerpt(excerpt),
                documentation.ContentHash));
        }

        return facts;
    }

    private static IndexedFact CreateLineFact(
        string kind,
        string name,
        string detail,
        IndexedFile file,
        string needle)
    {
        string[] lines = file.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int index = Array.FindIndex(lines, line => line.Contains(needle, StringComparison.OrdinalIgnoreCase));
        index = Math.Max(index, 0);
        return new IndexedFact(
            kind,
            name,
            detail,
            file.RelativePath,
            index + 1,
            index + 1,
            null,
            TrimExcerpt(lines[index]),
            file.ContentHash);
    }

    private static MetadataReference[] GetPlatformReferences()
    {
        string? trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trusted))
        {
            throw new ProjectIndexException("runtime_reference_set_unavailable");
        }

        return trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static bool TryMapKind(ISymbol symbol, out ProjectSymbolKind kind)
    {
        kind = symbol switch
        {
            INamedTypeSymbol { IsRecord: true } => ProjectSymbolKind.Record,
            INamedTypeSymbol { TypeKind: TypeKind.Class } => ProjectSymbolKind.Class,
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => ProjectSymbolKind.Interface,
            INamedTypeSymbol { TypeKind: TypeKind.Struct } => ProjectSymbolKind.Struct,
            INamedTypeSymbol { TypeKind: TypeKind.Enum } => ProjectSymbolKind.Enum,
            INamedTypeSymbol { TypeKind: TypeKind.Delegate } => ProjectSymbolKind.Delegate,
            IMethodSymbol { MethodKind: MethodKind.Constructor } => ProjectSymbolKind.Constructor,
            IMethodSymbol => ProjectSymbolKind.Method,
            IPropertySymbol => ProjectSymbolKind.Property,
            IFieldSymbol => ProjectSymbolKind.Field,
            IEventSymbol => ProjectSymbolKind.Event,
            _ => default,
        };
        return symbol is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol;
    }

    private static IndexedSymbol? FindIndexedContainingSymbol(
        ISymbol? symbol,
        IReadOnlyDictionary<ISymbol, List<IndexedSymbol>> symbolMap,
        string relativePath)
    {
        for (ISymbol? current = symbol; current is not null; current = current.ContainingSymbol)
        {
            IndexedSymbol? indexed = FindIndexedSymbol(current, symbolMap, relativePath);
            if (indexed is not null)
            {
                return indexed;
            }
        }

        return null;
    }

    private static IndexedSymbol? FindIndexedSymbol(
        ISymbol symbol,
        IReadOnlyDictionary<ISymbol, List<IndexedSymbol>> symbolMap,
        string? relativePath = null)
    {
        if (symbolMap.TryGetValue(symbol, out List<IndexedSymbol>? direct))
        {
            return direct.FirstOrDefault(candidate => relativePath is null ||
                candidate.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        }

        ISymbol original = symbol.OriginalDefinition;
        return symbolMap.TryGetValue(original, out List<IndexedSymbol>? definition)
            ? definition.FirstOrDefault(candidate => relativePath is null ||
                candidate.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static bool InheritanceContains(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name.Equals(name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetInvocationName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => string.Empty,
        };

    private static string? GetFirstStringArgument(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
            ? literal.Token.ValueText
            : null;

    private static string CombineRoute(string? prefix, string? route)
    {
        string combined = string.Join(
            '/',
            new[] { prefix, route }
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!.Trim('/')));
        return string.IsNullOrWhiteSpace(combined) ? "/" : "/" + combined;
    }

    private static bool IsBelowDirectory(string path, string directory) =>
        directory.Length == 0 ||
        path.StartsWith(directory.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    private static string GetDirectory(string path) =>
        (Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static string CreateStableId(string path, int line, string qualifiedName) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{path}|{line}|{qualifiedName}")));

    private static string CreateDeclarationExcerpt(SyntaxNode node)
    {
        string text = node switch
        {
            TypeDeclarationSyntax type => type.WithMembers(default).NormalizeWhitespace().ToFullString(),
            MethodDeclarationSyntax method => method.WithBody(null).WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace().ToFullString(),
            ConstructorDeclarationSyntax constructor => constructor.WithBody(null).WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace().ToFullString(),
            _ => node.NormalizeWhitespace().ToFullString(),
        };
        return TrimExcerpt(text);
    }

    private static string TrimExcerpt(string value)
    {
        const int maximum = 1_500;
        string normalized = value.Replace('\0', '\uFFFD').Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }

    private sealed record DocumentContext(
        Document Document,
        SyntaxNode Root,
        SemanticModel SemanticModel,
        IndexedFile File);
}
