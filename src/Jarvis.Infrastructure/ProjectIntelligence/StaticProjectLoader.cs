using System.Xml;
using System.Xml.Linq;

namespace Jarvis.Infrastructure.ProjectIntelligence;

internal static class StaticProjectLoader
{
    public static IReadOnlyList<StaticProject> Load(IReadOnlyList<IndexedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files
            .Where(static file => file.Kind == IndexedFileKind.Project)
            .Select(ParseProject)
            .OrderBy(static project => project.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static StaticProject ParseProject(IndexedFile file)
    {
        XDocument document;
        try
        {
            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 2 * 1024 * 1024,
                MaxCharactersFromEntities = 0,
            };
            using StringReader text = new(file.Content);
            using XmlReader reader = XmlReader.Create(text, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            throw new ProjectIndexException("project_xml_invalid");
        }

        string name = Path.GetFileNameWithoutExtension(file.RelativePath);
        string assemblyName = Value("AssemblyName") ?? name;
        string rootNamespace = Value("RootNamespace") ?? assemblyName;
        string outputType = Value("OutputType") ?? "Library";
        string[] targetFrameworks = (Value("TargetFrameworks") ?? Value("TargetFramework") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => !value.Contains("$(", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] projectReferences = document.Descendants()
            .Where(static element => element.Name.LocalName == "ProjectReference")
            .Select(static element => element.Attribute("Include")?.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value) &&
                !value.Contains("$(", StringComparison.Ordinal))
            .Select(value => NormalizeReference(file.RelativePath, value!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        PackageReferenceInfo[] packageReferences = document.Descendants()
            .Where(static element => element.Name.LocalName == "PackageReference")
            .Select(static element => new PackageReferenceInfo(
                element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value ?? string.Empty,
                element.Attribute("Version")?.Value ??
                    element.Elements().FirstOrDefault(static child => child.Name.LocalName == "Version")?.Value))
            .Where(static package => !string.IsNullOrWhiteSpace(package.Name) &&
                !package.Name.Contains("$(", StringComparison.Ordinal))
            .DistinctBy(static package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool isTestProject = string.Equals(Value("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
            packageReferences.Any(static package =>
                package.Name.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));

        return new StaticProject(
            file.RelativePath,
            name,
            assemblyName,
            rootNamespace,
            targetFrameworks,
            projectReferences,
            packageReferences,
            isTestProject,
            outputType);

        string? Value(string localName) => document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim();
    }

    private static string NormalizeReference(string projectPath, string reference)
    {
        string projectDirectory = Path.GetDirectoryName(projectPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        string combined = Path.GetFullPath(Path.Combine("C:\\__jarvis_repo__", projectDirectory, reference));
        return Path.GetRelativePath("C:\\__jarvis_repo__", combined)
            .Replace(Path.DirectorySeparatorChar, '/');
    }
}
