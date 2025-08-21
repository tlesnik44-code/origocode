using Google.Apis.Auth.AspNetCore3;
using Google.Apis.Drive.v3;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.OpenApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// OAuth (Google)
builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    o.DefaultChallengeScheme = GoogleOpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie()
.AddGoogleOpenIdConnect(options =>
{
    options.ClientId = builder.Configuration["Google:ClientId"]!;
    options.ClientSecret = builder.Configuration["Google:ClientSecret"]!;
    // Scopes dołączymy atrybutem przy endpointach (incremental auth)
});

builder.Services.AddAuthorization();

builder.Services.AddOpenApi(opt =>
{
    opt.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1;
    opt.AddDocumentTransformer((doc, ctx, ct) =>
    {
        doc.Info = new()
        {
            Title = "FileAPI (Google Drive text files)",
            Version = "1.0.0",
            Description = "Proxy do plików tekstowych w Google Drive w obrębie FileApi/<projectName>."
        };
        return Task.CompletedTask;
    });
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<GoogleDriveClient>(sp =>
{
    var auth = sp.GetRequiredService<IGoogleAuthProvider>();
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new GoogleDriveClient(auth, cfg["FileApi:RootName"] ?? "FileApi");
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// OpenAPI + Scalar
app.MapOpenApi("/openapi/v1.json");
app.MapScalarApiReference(options =>
{
    options.Title = "FileAPI";
    options.Theme = ScalarTheme.Mars;
});

// Auth helpers
app.MapGet("/auth/login", () =>
    Results.Challenge(new() { RedirectUri = "/" },
        new[] { GoogleOpenIdConnectDefaults.AuthenticationScheme }))
   .WithSummary("Logowanie przez Google (uzyskanie dostępu do Drive).")
   .WithTags("auth")
   .WithOpenApi();

app.MapGet("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Ok(new { ok = true });
})
.WithSummary("Wylogowanie.")
.WithTags("auth")
.WithOpenApi();

// Ping
app.MapGet("/ping", (string projectName) =>
{
    PathUtils.ValidateProjectName(projectName);
    return Results.Ok(new { ok = true, now = DateTimeOffset.UtcNow, projectName });
})
.WithSummary("Ping.")
.WithTags("files")
.WithOpenApi();

// LIST (folder → jeśli fileName kończy się na '/' lub wskazuje folder)
app.MapGet("/files/list", [GoogleScopedAuthorize(DriveService.Scope.DriveFile)]
async Task<IResult> (GoogleDriveClient g, string projectName, string? fileName) =>
{
    try
    {
        PathUtils.ValidateProjectName(projectName);
        var (folder, leaf, isFolder) = PathUtils.SplitFileName(fileName ?? "");
        var folderRel = isFolder ? folder : (string.IsNullOrEmpty(leaf) ? folder : $"{folder}/{leaf}".Trim('/'));
        var res = await g.ListAsync(projectName, folderRel);
        return Results.Ok(res);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
})
.WithSummary("Lista plików/podfolderów w FileApi/<projectName>/<fileName?>.")
.WithTags("files")
.WithOpenApi();

// READ
app.MapGet("/files/read", [GoogleScopedAuthorize(DriveService.Scope.DriveFile)]
async Task<IResult> (GoogleDriveClient g, string projectName, string fileName) =>
{
    try
    {
        PathUtils.ValidateProjectName(projectName);
        var (folder, leaf, isFolder) = PathUtils.SplitFileName(fileName);
        if (isFolder || string.IsNullOrEmpty(leaf)) return Results.BadRequest(new { ok = false, error = "fileName points to a folder" });
        var (found, content, fileId) = await g.ReadTextAsync(projectName, folder, leaf);
        return found ? Results.Ok(new { ok = true, name = leaf, content, fileId })
                     : Results.NotFound(new { ok = false, error = "file not found" });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
})
.WithSummary("Odczyt pliku tekstowego.")
.WithTags("files")
.WithOpenApi();

// SAVE (POST; content w body)
app.MapPost("/files/save", [GoogleScopedAuthorize(DriveService.Scope.DriveFile)]
async Task<IResult> (GoogleDriveClient g, SaveRequest body) =>
{
    try
    {
        PathUtils.ValidateProjectName(body.ProjectName);
        var (folder, leaf, isFolder) = PathUtils.SplitFileName(body.FileName);
        if (isFolder || string.IsNullOrEmpty(leaf)) return Results.BadRequest(new { ok = false, error = "fileName must be a file" });
        var res = await g.SaveTextAsync(body.ProjectName, folder, leaf, body.Content ?? "");
        return Results.Ok(res);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
})
.WithSummary("Zapis pliku tekstowego (create or replace).")
.WithTags("files")
.WithOpenApi();

// APPEND (POST)
app.MapPost("/files/append", [GoogleScopedAuthorize(DriveService.Scope.DriveFile)]
async Task<IResult> (GoogleDriveClient g, AppendRequest body) =>
{
    try
    {
        PathUtils.ValidateProjectName(body.ProjectName);
        var (folder, leaf, isFolder) = PathUtils.SplitFileName(body.FileName);
        if (isFolder || string.IsNullOrEmpty(leaf)) return Results.BadRequest(new { ok = false, error = "fileName must be a file" });
        var res = await g.AppendTextAsync(body.ProjectName, folder, leaf, body.ContentToAppend ?? "", body.Newline ?? true);
        return Results.Ok(res);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
})
.WithSummary("Dopisanie tekstu na końcu pliku (tworzy jeśli nie ma).")
.WithTags("files")
.WithOpenApi();

// REMOVE (DELETE)
app.MapDelete("/files/remove", [GoogleScopedAuthorize(DriveService.Scope.DriveFile)]
async Task<IResult> (GoogleDriveClient g, string projectName, string fileName) =>
{
    try
    {
        PathUtils.ValidateProjectName(projectName);
        var (folder, leaf, isFolder) = PathUtils.SplitFileName(fileName);
        if (isFolder || string.IsNullOrEmpty(leaf)) return Results.BadRequest(new { ok = false, error = "fileName must be a file" });
        var ok = await g.RemoveAsync(projectName, folder, leaf);
        return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { ok = false, error = "file not found" });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
})
.WithSummary("Przeniesienie pliku do kosza.")
.WithTags("files")
.WithOpenApi();

// RENAME (POST; newName bez ścieżki)
app.MapPost("/files/rename", [GoogleScopedAuthorize(DriveService.Scope.DriveFile)]
async Task<IResult> (GoogleDriveClient g, RenameRequest body) =>
{
    try
    {
        PathUtils.ValidateProjectName(body.ProjectName);
        var (folder, leaf, isFolder) = PathUtils.SplitFileName(body.FileName);
        if (isFolder || string.IsNullOrEmpty(leaf)) return Results.BadRequest(new { ok = false, error = "fileName must be a file" });
        if (string.IsNullOrWhiteSpace(body.NewName) || body.NewName.Contains('/')) return Results.BadRequest(new { ok = false, error = "newName must be a plain file name" });
        var ok = await g.RenameAsync(body.ProjectName, folder, leaf, body.NewName);
        return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { ok = false, error = "file not found" });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
})
.WithSummary("Zmiana nazwy pliku (w tym samym folderze).")
.WithTags("files")
.WithOpenApi();

// MOVE (POST; destFolder to folder docelowy, może kończyć się '/')
app.MapPost("/files/move", [GoogleScopedAuthorize(DriveService.Scope.DriveFile)]
async Task<IResult> (GoogleDriveClient g, MoveRequest body) =>
{
    try
    {
        PathUtils.ValidateProjectName(body.ProjectName);
        var (folder, leaf, isFolder) = PathUtils.SplitFileName(body.FileName);
        if (isFolder || string.IsNullOrEmpty(leaf)) return Results.BadRequest(new { ok = false, error = "fileName must be a file" });

        var (destFolderRel, destLeaf, destIsFolder) = PathUtils.SplitFileName(body.DestFolder);
        if (!destIsFolder && !string.IsNullOrEmpty(destLeaf)) // jeśli ktoś podał "folder/plik"
            return Results.BadRequest(new { ok = false, error = "destFolder must be a folder path (end with '/')" });

        var ok = await g.MoveAsync(body.ProjectName, folder, leaf, destFolderRel, body.CreateParents ?? true);
        return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { ok = false, error = "file or dest folder not found" });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
})
.WithSummary("Przeniesienie pliku do innego folderu projektu.")
.WithTags("files")
.WithOpenApi();

// MKDIR (POST; fileName musi kończyć się '/')
app.MapPost("/files/mkdir", [GoogleScopedAuthorize(DriveService.Scope.DriveFile)]
async Task<IResult> (GoogleDriveClient g, MkdirRequest body) =>
{
    try
    {
        PathUtils.ValidateProjectName(body.ProjectName);
        var (folderRel, leaf, isFolder) = PathUtils.SplitFileName(body.FileName);
        if (!isFolder) return Results.BadRequest(new { ok = false, error = "fileName must end with '/'" });
        var res = await g.MkdirAsync(body.ProjectName, folderRel);
        return Results.Ok(res);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
})
.WithSummary("Utworzenie folderu (z rodzicami).")
.WithTags("files")
.WithOpenApi();

app.Run();