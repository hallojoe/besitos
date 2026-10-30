# Configuration and Validation

1. If no configuration is needed, do not create `Configuration/`.
2. Use a strongly typed `FeatureSettings` class and `public const string SectionName = "Feature";`.
3. For simple required/range rules, use Data Annotations with `.ValidateDataAnnotations()`.
4. For cross-property or complex rules, create `FeatureSettingsValidator : IValidateOptions<FeatureSettings>` and register it with DI.
5. For a short pure predicate, `.Validate(settings => ..., "reason")` is acceptable.
6. Use `.ValidateOnStart()` when the application must not start with invalid configuration.
7. The C# `required` keyword alone does not guarantee runtime Options validation.
8. Avoid putting `Validate()` directly on settings models by default.
9. Avoid creating a separate feature constants class just for the section name.

## Dependency selection

Inspect the target framework, framework references, and effective package references before adding Options dependencies.
Reuse support already supplied by the host or existing packages.
`ValidateDataAnnotations()` requires the appropriate Options DataAnnotations support/package for the target framework.
Add missing support only when the selected validation approach requires it. Preserve the project's version and package-placement conventions.

For new registrations, a typical shape is `services.AddOptions<FeatureSettings>().BindConfiguration(FeatureSettings.SectionName).ValidateDataAnnotations().ValidateOnStart()` when startup validation is needed. Register an `IValidateOptions<FeatureSettings>` implementation through DI when rules depend on multiple settings. Check the project's existing Options setup and package references before copying this pattern.
