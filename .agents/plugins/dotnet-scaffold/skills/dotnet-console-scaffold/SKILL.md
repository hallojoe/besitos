---
name: dotnet-console-scaffold
description: Scaffold a .NET console application with a Generic Host, scoped dependency injection, configuration, and structured console logging. Use for new console apps, not for adding a command to an existing app.
---

# Console application scaffold

Create a console application with a small composition root. Follow the target repository's .NET version and package convention. When creating a new solution or standalone project, also apply [dotnet-cpm-scaffold](../dotnet-cpm-scaffold/SKILL.md) for package version placement.

## Start from the SDK template

- Create the project with `dotnet new console --name <AppName> --output <ProjectDir> --use-program-main`. Use the SDK template as the starting point, then adapt its generated `Program.cs` and project file to this skill. Choose `--framework` when the target framework is specified or the repository requires one.
- When creating a solution, run `dotnet new sln --name <SolutionName> --output <SolutionDir>`, then add the console project with `dotnet sln <SolutionFile> add <ProjectFile>`. Use the solution format supported by the repository's SDK. A standalone project does not need a solution file.
- When adding a console project to an existing solution, create it with `dotnet new console` and add it with `dotnet sln <ExistingSolutionFile> add <ProjectFile>`. Keep the existing solution's target framework and package conventions.

## Shape

- Use `Host.CreateApplicationBuilder(args)` and `Microsoft.Extensions.Hosting`. Keep `Program` responsible for building the host, opening a scope, invoking the application's entry service, and setting the process exit code. Put application work in an injected service, not in `Program` or a static helper.
- Put host setup in one `HostApplicationBuilder` extension such as `ConfigureApplication()` in `Configuration.cs`. It configures logging and calls focused `IServiceCollection` extensions for each application area. Define those service registration extensions beside the services they register; avoid a growing list of unrelated registrations in `Program`.
- Register the entry service and its dependencies with appropriate lifetimes. Resolve the entry service from an explicit `IServiceScope` and dispose the scope and host after the run. Create a fresh scope for each independent command or run if one process executes several. Never resolve scoped services from `host.Services` or call `BuildServiceProvider()` during registration.
- Use constructor injection in application services. Pass command arguments, input data, and cancellation as method parameters rather than registering invocation-specific values in the root container.

## Configuration and logging

Read [host-configuration.md](references/host-configuration.md) when selecting dependencies or implementing settings, validation, structured logging, or command output. `Host.CreateApplicationBuilder` already supplies default console logging; add logging providers only for a stated need. Pass cancellation to the entry service and return a nonzero exit code for a failed run.

## Reference pattern

The HttpDancer console project illustrates the intended split: [Program.cs](https://github.com/hallojoe/HttpDancer/blob/v1/dev/src/HttpDancer.Console/Program.cs) builds the host and runs a scoped service, while [Configuration.cs](https://github.com/hallojoe/HttpDancer/blob/v1/dev/src/HttpDancer.Console/Configuration.cs) configures services. Adapt the structure to the new application's needs; do not copy HttpDancer-specific services or settings.

