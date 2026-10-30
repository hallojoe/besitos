# Create / Extend / Adopt

## Create

Inspect the repository and target framework. Identify required services, configuration, and integration. Add only the necessary files and register the feature. Build/test.

## Extend

Inspect the existing feature and follow its current organization. Apply relevant coding guidance without silently reorganizing it. Ask only if the desired architecture is genuinely unclear.

## Adopt

Require explicit intent to restructure. Identify affected files, namespaces, registrations, references, routes, and tests. Make a scoped plan for nontrivial migrations. Preserve behavior and public contracts, including public CLR type names, namespaces, constructors, and method signatures. Moving a file does not require changing its namespace. If the requested structure cannot preserve a contract, explain the proposed breaking change before making it. Build and run relevant tests. Do not migrate unrelated features.
