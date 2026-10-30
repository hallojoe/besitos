---
name: dotnet-cpm-convert
description: Explicitly convert a named .NET project or solution from PackageReference versions to NuGet Central Package Management while preserving resolved versions and project behavior.
---

# Convert to Central Package Management

Use only when explicitly invoked for a named project or solution. This is a scoped migration, not a default part of adding a package or feature.

## Inspect before editing

1. Identify all projects in the requested scope, including solution projects outside the solution directory. Inspect each project's effective `ManagePackageVersionsCentrally` value, nearest `Directory.Packages.props`, direct `PackageReference` items, imported props/targets that supply them, target frameworks, and relevant build conditions.
2. Record the currently resolved direct package versions and all package metadata for each target framework and relevant configuration. Preserve version expressions or conditions where they determine different resolved versions; check that any expression still evaluates correctly after moving it to the earlier-imported central file. If a version cannot be determined reliably, explain the blocker rather than guessing.
3. CPM supports `PackageReference`, not `packages.config`. If any selected project uses `packages.config`, report that it needs a separate migration and stop the requested conversion. Do not silently convert only part of a solution.
4. Find a `Directory.Packages.props` placement that affects only the selected projects. Account for the nearest-file rule and existing central files. If a project-level file would affect siblings or descendants, or a solution-level file would affect projects outside scope, stop and explain the scope conflict before editing.

## Convert within the selected scope

- Enable `ManagePackageVersionsCentrally` in the scoped `Directory.Packages.props`. Move direct package versions to `PackageVersion` items and remove `Version` from `PackageReference` items. Keep package IDs, all other `PackageReference` metadata, and conditions intact.
- Preserve different versions of the same package. Use conditional central versions for target-framework differences and project `VersionOverride` for project-specific differences; do not unify versions as part of conversion. If version overrides are disabled by existing policy, do not silently change that policy: use a behavior-preserving conditional central version or report the blocker.
- Do not enable transitive pinning, add global package references, migrate `packages.config`, or change unrelated projects. Preserve existing CPM files and imports unless a scoped change is required for the conversion.

Restore and build all selected projects for their relevant target frameworks/configurations. Compare resolved direct package versions before and after, and confirm projects outside scope keep their effective CPM setting and package resolution. If verification cannot run, report exactly what remains unverified.

NuGet reference: https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management

For conditional versions and explicit overrides, consult [NuGet's version override rules](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management#overriding-package-versions); preserve an existing override only when the converted project needs it.
