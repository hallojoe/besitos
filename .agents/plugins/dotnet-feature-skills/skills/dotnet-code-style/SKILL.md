---
name: dotnet-code-style
description: Write and refactor readable, idiomatic C# with clear naming, cohesive methods, straightforward control flow, and useful comments.
---

# .NET Code Style

Apply when explicitly requested, or when a composing skill directs you to apply it.

1. Respect `.editorconfig`, compiler/language version, and established repository conventions.
2. Favor clarity over cleverness. Follow `.editorconfig` and nearby code when choosing `var` or an explicit type. Use modern syntax only when the project's C# version supports it and it makes the code easier to read.
3. Extract methods to expose intent, isolate responsibilities, or reduce complexity—not to satisfy line-count limits.
4. Prefer meaningful domain names; use PascalCase for types/methods, camelCase for locals/parameters, `_camelCase` for private fields, `I` for interfaces, and `Async` for async methods where applicable.
5. Prefer guard clauses and shallow nesting. Avoid unnecessary abstraction and indirection.
6. Inline comments should explain why, constraints, or surprising behavior—not narrate obvious code.
7. Write XML documentation for public contracts where it adds useful behavioral detail; avoid repetitive boilerplate on private helpers.
8. Preserve behavior during refactoring and avoid unrelated changes.

Read [readability guidance](references/readability.md) when a style choice is not obvious from the surrounding code.
