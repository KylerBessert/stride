// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using NuGet.Commands;
using NuGet.DependencyResolver;
using NuGet.ProjectModel;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Packages;

namespace Stride.Core.Assets;

partial class PackageSession
{
    private async Task PreLoadPackageDependencies(ILogger log, SolutionProject project, PackageLoadParameters loadParameters)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(loadParameters);

        bool packageDependencyErrors = false;

        var package = project.Package;

        // TODO: Remove and recheck Dependencies Ready if some secondary packages are removed?
        if (package.State >= PackageState.DependenciesReady)
            return;

        log.Verbose($"Process dependencies for {project.Name}...");

        var packageReferences = new Dictionary<string, PackageVersionRange>();

        // Check if there is any package upgrade to do
        var pendingPackageUpgrades = new List<PendingPackageUpgrade>();
        pendingPackageUpgradesPerPackage.Add(package, pendingPackageUpgrades);

        // Load some informations about the project
        try
        {
            var extraProperties = new Dictionary<string, string>();
            if (loadParameters.ExtraCompileProperties != null)
            {
                foreach (var extraProperty in loadParameters.ExtraCompileProperties)
                    extraProperties.Add(extraProperty.Key, extraProperty.Value);
            }
            extraProperties.Add("SkipInvalidConfigurations", "true");
            var msProject = VSProjectHelper.LoadProject(project.FullPath, loadParameters.BuildConfiguration, extraProperties: extraProperties);
            try
            {
                var packageVersion = msProject.GetPropertyValue("PackageVersion");
                if (!string.IsNullOrEmpty(packageVersion))
                    package.Meta.Version = new PackageVersion(packageVersion);

                project.TargetPath = msProject.GetPropertyValue("TargetPath");
                project.AssemblyProcessorSerializationHashFile = msProject.GetProperty("StrideAssemblyProcessorSerializationHashFile")?.EvaluatedValue;
                if (project.AssemblyProcessorSerializationHashFile != null)
                    project.AssemblyProcessorSerializationHashFile = Path.Combine(Path.GetDirectoryName(project.FullPath), project.AssemblyProcessorSerializationHashFile);
                package.Meta.Name = (msProject.GetProperty("PackageId") ?? msProject.GetProperty("AssemblyName"))?.EvaluatedValue ?? package.Meta.Name;

                var outputType = msProject.GetPropertyValue("OutputType");
                project.Type = outputType.Equals("winexe", StringComparison.InvariantCultureIgnoreCase)
                    || outputType.Equals("exe", StringComparison.InvariantCultureIgnoreCase)
                    || outputType.Equals("appcontainerexe", StringComparison.InvariantCultureIgnoreCase) // UWP
                    || msProject.GetPropertyValue("AndroidApplication").Equals("true", StringComparison.InvariantCultureIgnoreCase) // Android
                    ? ProjectType.Executable
                    : ProjectType.Library;

                // Note: Platform might be incorrect if Stride is not restored yet (it won't include Stride targets)
                // Also, if already set, don't try to query it again
                if (project.Type == ProjectType.Executable && project.Platform == PlatformType.Shared)
                    project.Platform = VSProjectHelper.GetPlatformTypeFromProject(msProject) ?? PlatformType.Shared;

                foreach (var packageReference in msProject.GetItems("PackageReference").ToList())
                {
                    if (packageReference.HasMetadata("Version") && PackageVersionRange.TryParse(packageReference.GetMetadataValue("Version"), out var packageRange))
                        packageReferences[packageReference.EvaluatedInclude] = packageRange;
                }

                // Need to go recursively
                foreach (var projectReference in msProject.GetItems("ProjectReference").ToList())
                {
                    var projectFile = new UFile(Path.Combine(Path.GetDirectoryName(project.FullPath), projectReference.EvaluatedInclude));
                    if (File.Exists(projectFile))
                    {
                        var referencedProject = Projects.OfType<SolutionProject>().FirstOrDefault(x => x.FullPath == new UFile(projectFile));
                        if (referencedProject != null)
                        {
                            await PreLoadPackageDependencies(log, referencedProject, loadParameters);

                            // Get package upgrader from dependency (a project might depend on another project rather than referencing Stride directly)
                            // A better system would be to evaluate nuget flattened dependencies WITHOUT doing the actual restore (dry-run).
                            // However I am not sure it's easy/possible to do it (using API) without doing a full restore/download, which we don't want to do
                            // with old version (it might be uninstalled already and we want to avoid re-downloading it again)
                            if (pendingPackageUpgradesPerPackage.TryGetValue(referencedProject.Package, out var dependencyPackageUpgraders))
                            {
                                foreach (var dependencyPackageUpgrader in dependencyPackageUpgraders)
                                {
                                    // Make sure this upgrader is not already added
                                    if (!pendingPackageUpgrades.Any(x => x.DependencyPackage == dependencyPackageUpgrader.DependencyPackage))
                                    {
                                        // Note: it's important to clone because once upgraded, each instance will have its Dependency.Version tested/updated
                                        pendingPackageUpgrades.Add(dependencyPackageUpgrader.Clone());
                                    }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                msProject.ProjectCollection.UnloadAllProjects();
                msProject.ProjectCollection.Dispose();
            }
        }
        catch (Exception ex)
        {
            log.Error($"Unexpected exception while loading project [{project.FullPath.ToOSPath()}]", ex);
        }

        foreach (var packageReference in packageReferences)
        {
            var dependencyName = packageReference.Key;
            var dependencyVersion = packageReference.Value;

            var packageUpgrader = AssetRegistry.GetPackageUpgrader(dependencyName);
            if (packageUpgrader != null)
            {
                // Check if this upgrader has already been added due to another package reference
                if (pendingPackageUpgrades.Any(pendingPackageUpgrade => pendingPackageUpgrade.PackageUpgrader == packageUpgrader))
                    continue;

                // Check if upgrade is necessary
                if (dependencyVersion.MinVersion >= packageUpgrader.Attribute.UpdatedVersionRange.MinVersion)
                {
                    continue;
                }

                // Check if upgrade is allowed
                if (dependencyVersion.MinVersion < packageUpgrader.Attribute.PackageMinimumVersion)
                {
                    // Throw an exception, because the package update is not allowed and can't be done
                    throw new InvalidOperationException($"Upgrading project [{project.Name}] to use [{dependencyName}] from version [{dependencyVersion}] to [{packageUpgrader.Attribute.UpdatedVersionRange.MinVersion}] is not supported (supported only from version [{packageUpgrader.Attribute.PackageMinimumVersion}]");
                }

                log.Info($"Upgrading project [{project.Name}] to use [{dependencyName}] from version [{dependencyVersion}] to [{packageUpgrader.Attribute.UpdatedVersionRange.MinVersion}] will be required");

                pendingPackageUpgrades.Add(new PendingPackageUpgrade(packageUpgrader, new PackageDependency(dependencyName, dependencyVersion), null));
            }
        }

        if (pendingPackageUpgrades.Count > 0)
        {
            var upgradeAllowed = packageUpgradeAllowed != false ? PackageUpgradeRequestedAnswer.Upgrade : PackageUpgradeRequestedAnswer.DoNotUpgrade;

            // Need upgrades, let's ask user confirmation
            if (loadParameters.PackageUpgradeRequested != null && !packageUpgradeAllowed.HasValue)
            {
                upgradeAllowed = loadParameters.PackageUpgradeRequested(package, pendingPackageUpgrades);
                if (upgradeAllowed == PackageUpgradeRequestedAnswer.UpgradeAll)
                    packageUpgradeAllowed = true;
                if (upgradeAllowed == PackageUpgradeRequestedAnswer.DoNotUpgradeAny)
                    packageUpgradeAllowed = false;
            }

            if (!PackageLoadParameters.ShouldUpgrade(upgradeAllowed))
            {
                log.Error($"Necessary package migration for [{package.Meta.Name}] has not been allowed");
                return;
            }

            // Perform pre assembly load upgrade
            foreach (var pendingPackageUpgrade in pendingPackageUpgrades)
            {
                var expectedVersion = pendingPackageUpgrade.PackageUpgrader.Attribute.UpdatedVersionRange?.MinVersion?.ToString();

                // Update NuGet references
                try
                {
                    var projectFile = project.FullPath;
                    var msbuildProject = VSProjectHelper.LoadProject(projectFile.ToOSPath());
                    var isProjectDirty = false;

                    foreach (var packageReference in msbuildProject.GetItems("PackageReference").ToList())
                    {
                        if (packageReference.EvaluatedInclude == pendingPackageUpgrade.Dependency.Name && packageReference.GetMetadataValue("Version") != expectedVersion)
                        {
                            packageReference.SetMetadataValue("Version", expectedVersion);
                            isProjectDirty = true;
                        }
                    }

                    if (isProjectDirty)
                        msbuildProject.Save();

                    msbuildProject.ProjectCollection.UnloadAllProjects();
                    msbuildProject.ProjectCollection.Dispose();
                }
                catch (Exception e)
                {
                    log.Warning($"Unable to load project [{project.FullPath.GetFileName()}]", e);
                }

                var packageUpgrader = pendingPackageUpgrade.PackageUpgrader;
                var dependencyPackage = pendingPackageUpgrade.DependencyPackage;
                if (!packageUpgrader.UpgradeBeforeAssembliesLoaded(loadParameters, package.Session, log, package, pendingPackageUpgrade.Dependency, dependencyPackage))
                {
                    log.Error($"Error while upgrading package [{package.Meta.Name}] for [{dependencyPackage.Meta.Name}] from version [{pendingPackageUpgrade.Dependency.Version}] to [{dependencyPackage.Meta.Version}]");
                    return;
                }
            }
        }

        // Now that our references are upgraded, let's do a real nuget restore (download files)
        log.Verbose($"Restore NuGet packages for {project.Name}...");
        if (loadParameters.AutoCompileProjects)
            await VSProjectHelper.RestoreNugetPackages(log, project.FullPath);

        // If platform was unknown (due to missing nuget packages during first pass), check it again
        if (project.Type == ProjectType.Executable && project.Platform == PlatformType.Shared)
        {
            try
            {
                var msProject = VSProjectHelper.LoadProject(project.FullPath, extraProperties: new Dictionary<string, string> { { "SkipInvalidConfigurations", "true" } });
                try
                {
                    project.Platform = VSProjectHelper.GetPlatformTypeFromProject(msProject) ?? PlatformType.Shared;
                }
                finally
                {
                    msProject.ProjectCollection.UnloadAllProjects();
                    msProject.ProjectCollection.Dispose();
                }
            }
            catch (Exception ex)
            {
                log.Error($"Unexpected exception while loading project [{project.FullPath.ToOSPath()}]", ex);
            }
        }

        UpdateDependencies(project, true, true);

        // 1. Load store package
        foreach (var projectDependency in project.FlattenedDependencies)
        {
            // Make all the assemblies known to the container to ensure that later assembly loads succeed
            foreach (var assembly in projectDependency.Assemblies)
                AssemblyContainer.RegisterDependency(assembly);

            var loadedPackage = packages.Find(projectDependency);
            if (loadedPackage == null)
            {
                string? file = null;
                switch (projectDependency.Type)
                {
                    case DependencyType.Project:
                        if (SupportedProgrammingLanguages.IsProjectExtensionSupported(Path.GetExtension(projectDependency.MSBuildProject).ToLowerInvariant()))
                            file = UPath.Combine(project.FullPath.GetFullDirectory(), (UFile)projectDependency.MSBuildProject);
                        break;
                    case DependencyType.Package:
                        file = PackageStore.Instance.GetPackageFileName(projectDependency.Name, new PackageVersionRange(projectDependency.Version), constraintProvider);
                        break;
                }

                if (file != null && File.Exists(file))
                {
                    // Load package
                    var loadedProject = LoadProject(log, file, loadParameters);
                    loadedProject.Package.Meta.Name = projectDependency.Name;
                    loadedProject.Package.Meta.Version = projectDependency.Version;
                    Projects.Add(loadedProject);

                    if (loadedProject is StandalonePackage standalonePackage)
                    {
                        standalonePackage.Assemblies.AddRange(projectDependency.Assemblies);
                    }

                    loadedPackage = loadedProject.Package;
                }
            }

            if (loadedPackage != null)
                projectDependency.Package = loadedPackage;
        }

        // 2. Load local packages
        /*foreach (var packageReference in package.LocalDependencies)
        {
            // Check that the package was not already loaded, otherwise return the same instance
            if (Packages.ContainsById(packageReference.Id))
            {
                continue;
            }

            // Expand the string of the location
            var newLocation = packageReference.Location;

            var subPackageFilePath = package.RootDirectory != null ? UPath.Combine(package.RootDirectory, newLocation) : newLocation;

            // Recursive load
            var loadedPackage = PreLoadPackage(log, subPackageFilePath.FullPath, false, loadedPackages, loadParameters);

            if (loadedPackage == null || loadedPackage.State < PackageState.DependenciesReady)
                packageDependencyErrors = true;
        }*/

        // 3. Update package state
        if (!packageDependencyErrors)
        {
            package.State = PackageState.DependenciesReady;
        }
    }

    public static void UpdateDependencies(SolutionProject project, bool directDependencies, bool flattenedDependencies)
    {
        if (flattenedDependencies)
            project.FlattenedDependencies.Clear();
        if (directDependencies)
            project.DirectDependencies.Clear();
        var projectAssetsJsonPath = Path.Combine(project.FullPath.GetFullDirectory(), @"obj", LockFileFormat.AssetsFileName);
        if (File.Exists(projectAssetsJsonPath))
        {
            var format = new LockFileFormat();
            var projectAssets = format.Read(projectAssetsJsonPath);

            // Update dependencies
            if (flattenedDependencies)
            {
                var libPaths = new Dictionary<(string?, NuGet.Versioning.NuGetVersion?), LockFileLibrary>();
                foreach (var lib in projectAssets.Libraries)
                {
                    libPaths.Add((lib.Name, lib.Version), lib);
                }

                foreach (var targetLibrary in projectAssets.Targets.Last().Libraries)
                {
                    if (!libPaths.TryGetValue((targetLibrary.Name, targetLibrary.Version), out var library))
                        continue;

                    var projectDependency = new Dependency(library.Name, library.Version.ToPackageVersion(), library.Type == "project" ? DependencyType.Project : DependencyType.Package) { MSBuildProject = library.Type == "project" ? library.MSBuildProject : null };

                    if (library.Type == "package")
                    {
                        // Find library path by testing with each PackageFolders
                        var libraryPath = projectAssets.PackageFolders
                            .Select(packageFolder => Path.Combine(packageFolder.Path, library.Path.Replace('/', Path.DirectorySeparatorChar)))
                            .FirstOrDefault(x => Directory.Exists(x));

                        if (libraryPath != null)
                        {
                            // Build list of assemblies
                            foreach (var a in targetLibrary.RuntimeAssemblies)
                            {
                                if (!a.Path.EndsWith("_._", StringComparison.Ordinal) && !a.Path.Contains("/native/"))
                                {
                                    var assemblyFile = Path.Combine(libraryPath, a.Path.Replace('/', Path.DirectorySeparatorChar));
                                    projectDependency.Assemblies.Add(assemblyFile);
                                }
                            }
                            foreach (var a in targetLibrary.RuntimeTargets)
                            {
                                if (!a.Path.EndsWith("_._", StringComparison.Ordinal) && !a.Path.Contains("/native/"))
                                {
                                    var assemblyFile = Path.Combine(libraryPath, a.Path.Replace('/', Path.DirectorySeparatorChar));
                                    projectDependency.Assemblies.Add(assemblyFile);
                                }
                            }
                        }
                    }

                    project.FlattenedDependencies.Add(projectDependency);
                    // Try to resolve package if already loaded
                    projectDependency.Package = project.Session.Packages.Find(projectDependency);
                }
            }

            if (directDependencies)
            {
                foreach (var projectReference in projectAssets.PackageSpec.RestoreMetadata.TargetFrameworks.First().ProjectReferences)
                {
                    var projectName = new UFile(projectReference.ProjectUniqueName).GetFileNameWithoutExtension();
                    project.DirectDependencies.Add(new DependencyRange(projectName, null, DependencyType.Project) { MSBuildProject = projectReference.ProjectPath });
                }

                foreach (var dependency in projectAssets.PackageSpec.TargetFrameworks.First().Dependencies)
                {
                    if (dependency.AutoReferenced)
                        continue;
                    project.DirectDependencies.Add(new DependencyRange(dependency.Name, dependency.LibraryRange.VersionRange.ToPackageVersionRange(), DependencyType.Package));
                }
            }
        }
    }

    private static ExternalProjectReference ToExternalProjectReference(PackageSpec project)
    {
        return new ExternalProjectReference(
            project.Name,
            project,
            msbuildProjectPath: null,
            projectReferences: []);
    }

    private static List<ExternalProjectReference> GetProjectReferences(RestoreRequest _request, RemoteWalkContext context)
    {
        // External references
        var updatedExternalProjects = new List<ExternalProjectReference>();

        if (_request.ExternalProjects.Count == 0)
        {
            // If no projects exist add the current project.json file to the project
            // list so that it can be resolved.
            updatedExternalProjects.Add(ToExternalProjectReference(_request.Project));
        }
        else if (_request.ExternalProjects.Count > 0)
        {
            // There should be at most one match in the external projects.
            var rootProjectMatches = _request.ExternalProjects.Where(proj =>
                    string.Equals(
                        _request.Project.Name,
                        proj.PackageSpecProjectName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (rootProjectMatches.Count > 1)
            {
                throw new InvalidOperationException($"Ambiguous project name '{_request.Project.Name}'.");
            }

            var rootProject = rootProjectMatches.SingleOrDefault();

            if (rootProject != null)
            {
                // Replace the project spec with the passed in package spec,
                // for installs which are done in memory first this will be
                // different from the one on disk
                updatedExternalProjects.AddRange(_request.ExternalProjects
                    .Where(project =>
                        !project.UniqueName.Equals(rootProject.UniqueName, StringComparison.Ordinal)));

                var updatedReference = new ExternalProjectReference(
                    rootProject.UniqueName,
                    _request.Project,
                    rootProject.MSBuildProjectPath,
                    rootProject.ExternalProjectReferences);

                updatedExternalProjects.Add(updatedReference);
            }
        }
        else
        {
            // External references were passed, but the top level project wasn't found.
            // This is always due to an internal issue and typically caused by errors
            // building the project closure.
            throw new InvalidOperationException($"Missing external reference metadata for {_request.Project.Name}");
        }

        return updatedExternalProjects;
    }
}
