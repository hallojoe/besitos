---
name: aspnet-feature
description: Build or evolve ASP.NET Core HTTP features using the .NET feature conventions plus controllers, routing, authorization, and pipeline integration.
---

# ASP.NET Core Feature

Apply the conventions from [dotnet-feature](../dotnet-feature/SKILL.md) and, through it, [dotnet-code-style](../dotnet-code-style/SKILL.md) when those files are available. These are explicit cross-references, not automatic inheritance.

Follow the base skill's **create / extend / adopt** scope rules.

## Additional optional structure

```text
FeatureName/
└── Controllers/
    ├── FeatureControllerBase.cs
    └── FeatureController.cs
```

Only create `Controllers/` when using controllers. Respect projects using Minimal APIs or another existing HTTP style.

## HTTP conventions

- Use `[ApiController]`, routing, authorization, and response metadata as appropriate to the application.
- Introduce a feature controller base only when multiple controllers share meaningful attributes or behavior.
- Keep controllers thin and delegate business logic to feature services.
- Preserve existing route contracts and authorization behavior during changes.
- Separate DI registration from HTTP pipeline/endpoint mapping.
- Do not create redundant controllers or base classes.

Read [HTTP guidance](references/http.md) when adding or changing HTTP endpoints, controllers, routes, or pipeline integration.
