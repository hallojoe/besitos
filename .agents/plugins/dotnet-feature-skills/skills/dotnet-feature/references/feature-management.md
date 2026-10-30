# Feature management

Check the selected `Microsoft.FeatureManagement` release against the [official feature reference](https://learn.microsoft.com/en-us/azure/azure-app-configuration/feature-management-dotnet-reference) and [console sample](https://github.com/microsoft/FeatureManagement-Dotnet/tree/main/examples/FeatureFlagDemo) before using version-sensitive registration or schema details.

For a new feature, make the flag operational rather than merely declaring it. Identify every entry point that can start the feature's work, evaluate the flag before side effects, and define the disabled behavior for each entry point. Prefer one shared check when entry points already converge. Do not conditionally register the feature's services at startup to implement a flag that should change at runtime.

## Dependency selection

Inspect existing feature-management packages, registration, and configuration before adding dependencies.
Reuse compatible packages and the project's package-placement convention.
Add `Microsoft.FeatureManagement` and register `AddFeatureManagement()` at the composition root if the application does not already do so.
Keep configuration in the host that owns it.
If the host is outside scope, report the required flag definition and registration instead of creating unused library configuration.
For requested HTTP-specific capabilities, check the selected release's integration requirements before adding supporting packages.

## Flag and integration

- Add one flag, initially disabled unless the user requests another initial state. Respect an existing flag name, provider, and schema. For a new setup, use the `feature_management.feature_flags` schema, for example `{ "id": "InvoiceExport", "enabled": false }`.
- Evaluate flags through `IFeatureManager` or `IFeatureManagerSnapshot`. Do not replace the library with a custom `IConfiguration` lookup. If package restore is unavailable, keep the intended package reference and implementation, then report the verification limit.
- Use a feature-local flag-name constant when the name appears in multiple code locations. Do not add a constants class solely to hold one string.
- Use advanced filters, targeting, or variants only when the requested feature behavior requires them. Check the project's package version and registration needs before adding those capabilities. Use scoped feature-management registration when a filter depends on scoped services.

## Evaluation lifetime

Choose the interface for the operation, not by application type:

| Need | Interface |
| --- | --- |
| One check at an entry point, or later calls should be able to observe refreshed configuration | `IFeatureManager` |
| Repeated checks of the same flag must agree throughout one DI scope, typically an HTTP request | `IFeatureManagerSnapshot` |

The snapshot caches the first evaluation in its scope. Do not inject it into a singleton or hold it across operations. For a long-running job that needs a stable decision, evaluate once at the start of the job and carry that result through the job. If one scope evaluates the same flag for different contexts, consider whether the snapshot's cached result is appropriate; use `IFeatureManager` when each context needs its own evaluation. Neither interface makes a configuration provider refresh on its own.

Verify that disabled means the feature does no work and that enabling the flag allows the intended behavior. Test the disabled contract at the entry point, not just the configuration value.
