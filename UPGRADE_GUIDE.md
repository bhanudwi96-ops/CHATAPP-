# .NET 6 EOL — Upgrade Guide to .NET 8

## Current State
This project currently targets **.NET 6.0**, which is **End-of-Life** and no longer receives security updates.

## Recommended Upgrade: .NET 8 (LTS)
.NET 8 is the current Long-Term Support release (supported until November 2026).

## Step-by-Step Upgrade

### 1. Update `TargetFramework` in all `.csproj` files
```xml
<!-- Before -->
<TargetFramework>net6.0</TargetFramework>

<!-- After -->
<TargetFramework>net8.0</TargetFramework>
```

Files to update:
- `ChatApp.API/ChatApp.API.csproj`
- `ChatApp.Application/ChatApp.Application.csproj`
- `ChatApp.Domain/ChatApp.Domain.csproj`
- `ChatApp.Infrastructure/ChatApp.Infrastructure.csproj`

### 2. Update NuGet Packages
```powershell
# Update all EF Core packages
dotnet add ChatApp.Infrastructure package Microsoft.EntityFrameworkCore --version 8.0.*
dotnet add ChatApp.Infrastructure package Microsoft.EntityFrameworkCore.SqlServer --version 8.0.*
dotnet add ChatApp.API package Microsoft.AspNetCore.Authentication.JwtBearer --version 8.0.*
```

### 3. Remove `program.cs` top-level statement changes (if any)
.NET 8 is backward compatible with the existing minimal hosting model. No changes needed.

### 4. Update `global.json` (if present)
```json
{
  "sdk": {
    "version": "8.0.100"
  }
}
```

### 5. Test
```powershell
dotnet restore
dotnet build
dotnet test  # if tests exist
```

## Breaking Changes to Watch For
- `System.IdentityModel.Tokens.Jwt` 7.x has breaking namespace changes — test JWT generation/validation
- EF Core 8 has improved compiled models and JSON column support, but may change some query behavior
- `MinimalApi` changes if you migrate from controllers
