---
name: dotnet-cpm-scaffold
description: Use NuGet Central Package Management when scaffolding a new .NET solution or standalone project; follow an existing solution's package convention when adding a project to it.
---

# CPM for new .NET scaffolds

Apply when creating a .NET solution or project. This skill governs package version placement, not the choice of project template or application architecture.

1. Determine whether the project belongs to a new solution, is standalone, or is being added to an existing solution. Inspect the effective `ManagePackageVersionsCentrally` value and the nearest `Directory.Packages.props` for existing projects before choosing a convention.
2. For a new solution, create `Directory.Packages.props` at its root with `dotnet new packagesprops --output <SolutionRoot>`. For a new standalone project, use the same command with its project root. Check the generated file and set `ManagePackageVersionsCentrally` to `true`, even when there are no package references yet. Do not place the file where it would silently enable CPM for existing projects outside the new scaffold.
3. Put each direct dependency's version in a `PackageVersion` item and reference the package without `Version` in project files. Preserve other `PackageReference` metadata.
4. For a new project in an existing CPM solution, use the solution's effective central file and add only missing package versions there. For a new project in an existing non-CPM solution, follow that solution's package convention. Do not enable CPM for that project or change the solution's existing projects as a side effect.
5. When adding packages to any existing project, follow its effective package management convention. Do not treat a package addition or feature implementation as permission to convert it to CPM. Leave transitive pinning and global package references off unless requested.

Restore and build the created project or solution. Verify the effective CPM property and that package references and central versions resolve as intended.

NuGet reference: https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management

For the source file pair and a small example, see [CPM example](references/cpm-example.md).
