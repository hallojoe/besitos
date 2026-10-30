# Central Package Management example

The [NuGet CPM guide](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management#enabling-central-package-management) defines this file pairing. The package version below is illustrative; select and verify a version compatible with the generated project's target framework.

`Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
  </ItemGroup>
</Project>
```

Project file:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.Hosting" />
</ItemGroup>
```

The `PackageReference` has no local `Version`; NuGet resolves it from the nearest `Directory.Packages.props`.
