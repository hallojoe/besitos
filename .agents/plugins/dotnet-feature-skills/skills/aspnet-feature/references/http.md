# ASP.NET Core HTTP Guidance

## Controllers

Inherit directly from `ControllerBase` by default. Use a feature-specific abstract controller base only when it genuinely centralizes shared attributes or behavior. Beware that route and authorization attributes may vary between controllers.

## Routes and contracts

When extending/adopting existing features, preserve public routes, HTTP methods, status codes, binding behavior, and authorization unless explicitly asked to change them.

## Integration

Keep `AddFeature` (services/options) separate from `UseFeature` or `MapFeature` (pipeline/endpoints) where applicable. Avoid unnecessary builder wrappers.

For a new HTTP feature, apply the base feature-management check at an entry point that covers its requests before feature work starts. Choose the disabled HTTP response deliberately and verify it. Use `IFeatureManagerSnapshot` when repeated checks during a request must agree; otherwise use `IFeatureManager`. Add controller- or action-specific `[FeatureGate]` rules only when the requested behavior calls for that granularity, and avoid duplicate checks for the same boundary.

## Minimal APIs

If the existing application uses Minimal APIs, adapt the feature layout rather than forcing controller scaffolding.
