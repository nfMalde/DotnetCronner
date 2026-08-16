# EF Core migrations for DotnetCronner

The EF Core store's table (`CronnerJobs`) is created and evolved by **EF Core migrations**. This guide
shows how to generate and apply them.

There are two ways to host the table, and then two project layouts for where the migrations are stored.

- **Context choice** — the built-in `CronnerDbContext` (recommended) or your own `DbContext`.
- **Layout** — migrations in your **main** project, or in a **separate class library**.

## Use the `dotnet ef` CLI tool (recommended)

The `dotnet ef` tool is the recommended way to create and apply the migrations. Install it globally, or
(preferred) as a repo-local tool so the version is pinned for the whole team:

```bash
# Global:
dotnet tool install --global dotnet-ef        # or: dotnet tool update --global dotnet-ef

# Repo-local (recommended) — run once at the solution root:
dotnet new tool-manifest        # skip if .config/dotnet-tools.json already exists
dotnet tool install dotnet-ef
dotnet tool restore             # teammates run this after cloning
```

Keep the tool version aligned with your EF Core version. The project that owns the migrations also needs
a database provider (e.g. `Microsoft.EntityFrameworkCore.SqlServer` or
`Npgsql.EntityFrameworkCore.PostgreSQL`) and `Microsoft.EntityFrameworkCore.Design`.

---

## Context choice

### Recommended: the built-in `CronnerDbContext`

`CronnerDbContext` (shipped in this package) already maps the table — there is nothing to implement and no
`ApplyCronnerModel()` call to remember. Register it with your provider inside `AddDotnetCronner`:

```csharp
builder.Services.AddDotnetCronner(c =>
    c.UseEntityFrameworkStore(o =>
        o.UseNpgsql(connectionString, b => b.MigrationsAssembly("MyApp.Web"))));   // see note below
```

Because `CronnerDbContext` lives in this NuGet package, EF would otherwise try to write its migrations
into the package. **Set `MigrationsAssembly(...)` to the project where you want the migrations stored** —
your main project (Layout A) or a class library (Layout B).

### Advanced: your own `DbContext`

Only if you want the `CronnerJobs` table inside a context you already have. Your `DbContext` must do two
things:

1. **implement the `ICronnerDbContext` interface** — this exposes the `CronnerJobs` `DbSet`; and
2. **call the `ApplyCronnerModel` extension method** in `OnModelCreating` — this maps the table and its
   indexes.

The interface only supplies the signature, so you must call the extension method yourself, or the table is
never configured:

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), ICronnerDbContext
{
    public DbSet<CronnerJobEntity> CronnerJobs => Set<CronnerJobEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyCronnerModel();   // REQUIRED — do not forget this line
    }
}
```

```csharp
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddDotnetCronner(c => c.UseEntityFrameworkStore<AppDbContext>());
```

The commands below are identical for either context — just substitute the context name in `--context`.

---

## Layout A — migrations in the main project

Everything lives in the startup project. From that project's folder:

```bash
# built-in context:
dotnet ef migrations add AddCronner --context CronnerDbContext
dotnet ef database update           --context CronnerDbContext

# (own context: use --context AppDbContext, or drop --context if it is your only DbContext)
```

For the built-in context, keep `MigrationsAssembly("MyApp.Web")` set to this project so the files land in
`Migrations/` here.

## Layout B — migrations in a separate class library

Say the solution is:

```
src/MyApp.Web    (startup project: Program.cs, connection string, DI)
src/MyApp.Data   (class library: migrations are stored here)
```

Reference the provider + `Microsoft.EntityFrameworkCore.Design` in `MyApp.Data`, and point
`MigrationsAssembly` at it:

```csharp
o.UseNpgsql(connectionString, b => b.MigrationsAssembly("MyApp.Data"))
```

EF needs a *startup* project (for configuration/DI) and a *migrations* project (where files are written).
Run from the solution root:

```bash
dotnet ef migrations add AddCronner \
  --context CronnerDbContext \
  --project src/MyApp.Data \
  --startup-project src/MyApp.Web

dotnet ef database update \
  --context CronnerDbContext \
  --project src/MyApp.Data \
  --startup-project src/MyApp.Web
```

The files are written to `src/MyApp.Data/Migrations/`.

### Optional: a design-time factory (own context only)

If you use your **own** context and want the CLI to build it without booting the app, add an
`IDesignTimeDbContextFactory<AppDbContext>` in the migrations project:

```csharp
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=app;Username=postgres;Password=postgres",
                b => b.MigrationsAssembly("MyApp.Data"))
            .Options);
}
```

Then `--startup-project` becomes optional.

---

## Applying migrations at runtime

Instead of `dotnet ef database update`, apply pending migrations on startup:

```csharp
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CronnerDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();     // applies any pending migrations
}
```

Use `MigrateAsync()` in production (it applies migrations). `EnsureCreatedAsync()` is convenient for
throwaway/dev databases but does **not** use migrations and cannot evolve an existing schema — don't mix
the two on one database.

## Notes

- **What the migration contains:** the `CronnerJobs` table plus indexes on `(State, NextRunUtc)` and
  `NextRunUtc`. With your own context, your other entities share the same migration — that is expected.
  There is **no** concurrency-token column: claiming a due task uses a single conditional `UPDATE` that
  re-asserts eligibility in the statement, which is correct on every provider without a row version.
