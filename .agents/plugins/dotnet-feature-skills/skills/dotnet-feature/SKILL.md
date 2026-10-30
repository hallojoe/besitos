---
name: dotnet-feature
description: Create, extend, or explicitly adopt a feature-first structure for .NET features, with feature flags, configuration, dependency injection, and extension conventions.
---

# .NET Feature

This is an **opt-in** architectural skill. Apply the [dotnet-code-style conventions](../dotnet-code-style/SKILL.md) when available.

## Modes

- **Create**: implement a new feature with only the necessary structure.
- **Extend**: enhance an existing feature; preserve its structure and behavior.
- **Adopt**: reorganize an existing feature into these conventions **only when explicitly requested**.

Never treat skill invocation alone as permission to migrate unrelated existing code.

## Default structure (conditional)

```text
FeatureName/
├── Configuration/
│   ├── FeatureSettings.cs
│   └── FeatureSettingsValidator.cs
├── Extensions/
│   ├── ServiceCollectionExtensions.cs
│   └── WebApplicationBuilderExtensions.cs
└── FeatureConstants.cs
```

Every directory and file is optional. Do not generate empty scaffolding. ASP.NET-specific controllers are defined by `aspnet-feature`.

## Conventions

- Prefer strongly typed Options settings, with a `SectionName` constant on the settings class.
- Prefer Data Annotations for simple validation, `IValidateOptions<T>` for complex validation, and inline `.Validate(...)` for simple predicates.
- Use `.ValidateOnStart()` when invalid configuration should fail application startup.
- Put DI registrations and Options wiring in `ServiceCollectionExtensions`.
- Add builder convenience extensions only if useful; keep pipeline activation separate from registration.
- Use feature-local constants for true constants, not environment-specific settings.
- In create mode, make a disabled feature flag and an effective runtime check part of a new feature unless the user opts out or specifies another feature-management approach. Choose `IFeatureManager` or `IFeatureManagerSnapshot` for the feature's evaluation lifetime.
- When the task also creates a .NET solution or project, apply [dotnet-cpm-scaffold](../../../dotnet-scaffold/skills/dotnet-cpm-scaffold/SKILL.md) from the `dotnet-scaffold` plugin if available. Adding a feature or package to an existing project does not enable CPM; follow its current package convention.
- Follow existing project infrastructure rather than introducing parallel systems.
- Build and run relevant tests after changes.

Read [workflows](references/workflows.md) for the selected mode.
Read [configuration](references/configuration.md) when the feature has settings or validation, including dependency selection for those APIs.
Read [extensions](references/extensions.md) when adding registration or pipeline extensions.
Read [feature management](references/feature-management.md) when creating a feature or adding feature management to an existing one.
