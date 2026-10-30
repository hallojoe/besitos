# Host configuration and logging

## Dependency selection

Inspect the project's target framework, effective package references, and central package file before adding dependencies.
Use `Microsoft.Extensions.Hosting` for the Generic Host. Reuse compatible versions already selected by the solution.
Check existing host dependencies before adding separate configuration or logging packages.
Add validation support or logging providers only when the selected behavior requires them.
Follow the project's package convention; this reference does not authorize CPM conversion.

## Configuration

- Let `Host.CreateApplicationBuilder(args)` provide the standard appsettings, environment, and command-line sources. Add only app-specific JSON files that the app actually needs, in the builder extension, before binding options. Since later configuration sources win, place extra JSON before the environment and command-line sources or re-add those override sources afterward.
- Bind each feature's settings to an options type from a named configuration section in its service registration extension. Validate required settings at startup or before use. Do not read arbitrary configuration keys throughout the workflow.
- If the app adds JSON files, include them in build and publish output. Resolve them relative to the application's content root so launching from another working directory does not change which configuration is loaded.
- Keep secrets out of checked-in appsettings files. Use environment variables or the host's normal development configuration mechanisms for secrets.

## Logging and command output

- Use `Microsoft.Extensions.Logging` with the console provider. Default to `Information` for application events, but allow configuration to control levels. Use structured message templates in injected services and log exceptions with the exception argument.
- Reserve `Console.Out` for deliberate command results and `Console.Error` for actionable input errors. Use `ILogger<T>` for diagnostics; do not mix ad hoc console diagnostics into application services.
- Return a nonzero exit code for invalid input or a failed run. Preserve cancellation so Ctrl+C can stop long-running work; pass its token into the entry service.

