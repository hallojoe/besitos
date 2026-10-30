# Extensions

- `ServiceCollectionExtensions`: DI registrations and Options setup, e.g. `AddMyFeature(...)`.
- `WebApplicationBuilderExtensions`: optional composition convenience, e.g. `builder.AddMyFeature()` calling `builder.Services.AddMyFeature(...)`.
- `WebApplicationExtensions`: optional HTTP pipeline middleware or endpoint mapping, e.g. `app.UseMyFeature()`.

For new features, name registration methods `AddFeatureName`, endpoint mapping methods `MapFeatureName`, and middleware methods `UseFeatureName`. Place extensions in a namespace that follows the host project's existing root and feature naming. Existing conventions take precedence during extend mode.

Do not register the same services independently in multiple entry points. Respect existing project naming and namespace conventions.
