using Google.Apis.Auth.AspNetCore3;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;

public sealed class GoogleDriveClient
{
    private readonly IGoogleAuthProvider _auth;
    private readonly string _rootName;

    public GoogleDriveClient(IGoogleAuthProvider auth, string rootName)
    {
        _auth = auth;
        _rootName = string.IsNullOrWhiteSpace(rootName) ? "FileApi" : rootName;
    }

    private async Task<DriveService> CreateServiceAsync()
    {
        var cred = await _auth.GetCredentialAsync();
        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = cred,
            ApplicationName = "FileAPI"
        });
    }

    private static string And(params string[] clauses) =>
        string.Join(" and ", clauses.Where(c => !string.IsNullOrWhiteSpace(c)));

    private static FilesResource.ListRequest PrepareList(DriveService svc, string q, string fields) =>
        new FilesResource.ListRequest(svc)
        {
            Q = q,
            Fields = $"files({fields})",
            SupportsAllDrives = false,
            IncludeItemsFromAllDrives = false,
            Spaces = "drive",
            PageSize = 1000
        };

    private async Task<string> EnsureProjectRootAsync(DriveService svc, string projectName)
    {
        // /FileApi
        var rootReq = PrepareList(svc,
            And("mimeType = 'application/vnd.google-apps.folder'",
                $"name = '{_rootName}'",
                "'root' in parents",
                "trashed = false"),
            "id,name");
        var rootId = (await rootReq.ExecuteAsync()).Files?.FirstOrDefault()?.Id;
        if (rootId == null)
        {
            var folder = new File { Name = _rootName, MimeType = "application/vnd.google-apps.folder", Parents = new[] { "root" } };
            rootId = (await svc.Files.Create(folder).ExecuteAsync()).Id;
        }

        // /FileApi/<projectName>
        var projReq = PrepareList(svc,
            And("mimeType = 'application/vnd.google-apps.folder'",
                $"name = '{projectName}'",
                $"'{rootId}' in parents",
                "trashed = false"),
            "id,name");
        var projId = (await projReq.ExecuteAsync()).Files?.FirstOrDefault()?.Id;
        if (projId == null)
        {
            var folder = new File { Name = projectName, MimeType = "application/vnd.google-apps.folder", Parents = new[] { rootId! } };
            projId = (await svc.Files.Create(folder).ExecuteAsync()).Id;
        }
        return projId!;
    }

    private async Task<string> EnsureFolderAsync(DriveService svc, string projectName, string relPath)
    {
        var projId = await EnsureProjectRootAsync(svc, projectName);
        if (string.IsNullOrWhiteSpace(relPath)) return projId;

        var segments = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = projId;
        foreach (var seg in segments)
        {
            var req = PrepareList(svc,
                And("mimeType = 'application/vnd.google-apps.folder'",
                    $"name = '{seg}'",
                    $"'{current}' in parents",
                    "trashed = false"),
                "id,name");
            var id = (await req.ExecuteAsync()).Files?.FirstOrDefault()?.Id;
            if (id == null)
            {
                var folder = new File { Name = seg, MimeType = "application/vnd.google-apps.folder", Parents = new[] { current } };
                id = (await svc.Files.Create(folder).ExecuteAsync()).Id;
            }
            current = id!;
        }
        return current;
    }

    private async Task<string?> FindFolderAsync(DriveService svc, string projectName, string relPath)
    {
        var projId = await EnsureProjectRootAsync(svc, projectName);
        if (string.IsNullOrWhiteSpace(relPath)) return projId;

        var segments = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = projId;
        foreach (var seg in segments)
        {
            var req = PrepareList(svc,
                And("mimeType = 'application/vnd.google-apps.folder'",
                    $"name = '{seg}'",
                    $"'{current}' in parents",
                    "trashed = false"),
                "id,name");
            var id = (await req.ExecuteAsync()).Files?.FirstOrDefault()?.Id;
            if (id == null) return null;
            current = id!;
        }
        return current;
    }

    private async Task<string?> FindFileIdAsync(DriveService svc, string parentId, string name)
    {
        var req = PrepareList(svc,
            And($"name = '{name}'", $"'{parentId}' in parents", "trashed = false"),
            "id,name,webViewLink");
        return (await req.ExecuteAsync()).Files?.FirstOrDefault()?.Id;
    }

    public async Task<ListResponse> ListAsync(string projectName, string folderRel)
    {
        var svc = await CreateServiceAsync();
        var parentId = await EnsureFolderAsync(svc, projectName, folderRel);

        var filesReq = PrepareList(svc,
            And($"'{parentId}' in parents", "trashed = false"),
            "id,name,mimeType,webViewLink");
        var ls = await filesReq.ExecuteAsync();

        var files = ls.Files?
            .Where(f => f.MimeType != "application/vnd.google-apps.folder")
            .Select(f => new FileEntry(f.Name, f.Id, f.MimeType!, f.WebViewLink))
            ?? Enumerable.Empty<FileEntry>();

        var folders = ls.Files?
            .Where(f => f.MimeType == "application/vnd.google-apps.folder")
            .Select(f => new FolderEntry(f.Name, f.Id, f.WebViewLink))
            ?? Enumerable.Empty<FolderEntry>();

        var path = $"{_rootName}/{projectName}" + (string.IsNullOrWhiteSpace(folderRel) ? "" : $"/{folderRel}");
        return new ListResponse(true, path, folders, files);
    }

    public async Task<(bool Found, string? Content, string? FileId)> ReadTextAsync(string projectName, string folderRel, string leafName)
    {
        var svc = await CreateServiceAsync();
        var parentId = await FindFolderAsync(svc, projectName, folderRel);
        if (parentId is null) return (false, null, null);

        var fileId = await FindFileIdAsync(svc, parentId, leafName);
        if (fileId is null) return (false, null, null);

        using var ms = new MemoryStream();
        await svc.Files.Get(fileId).DownloadAsync(ms);
        ms.Position = 0;
        using var reader = new StreamReader(ms);
        var text = await reader.ReadToEndAsync();
        return (true, text, fileId);
    }

    public async Task<object> SaveTextAsync(string projectName, string folderRel, string leafName, string content)
    {
        var svc = await CreateServiceAsync();
        var parentId = await EnsureFolderAsync(svc, projectName, folderRel);
        var fileId = await FindFileIdAsync(svc, parentId, leafName);

        var meta = new File { Name = leafName, Parents = new[] { parentId } };
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        const string mime = "text/plain";

        if (fileId is null)
        {
            var create = svc.Files.Create(meta, ms, mime);
            create.Fields = "id,webViewLink";
            await create.UploadAsync();
            return new { ok = true, created = true, fileId = create.ResponseBody.Id, webUrl = create.ResponseBody.WebViewLink };
        }
        else
        {
            var update = svc.Files.Update(new File { Name = leafName }, fileId, ms, mime);
            update.Fields = "id,webViewLink";
            await update.UploadAsync();
            return new { ok = true, created = false, fileId = update.ResponseBody.Id, webUrl = update.ResponseBody.WebViewLink };
        }
    }

    public async Task<object> AppendTextAsync(string projectName, string folderRel, string leafName, string toAppend, bool newline)
    {
        var (found, content, _) = await ReadTextAsync(projectName, folderRel, leafName);
        var prev = found ? content ?? "" : "";
        var next = prev + (newline ? "\n" : "") + toAppend;
        return await SaveTextAsync(projectName, folderRel, leafName, next);
    }

    public async Task<bool> RemoveAsync(string projectName, string folderRel, string leafName)
    {
        var svc = await CreateServiceAsync();
        var parentId = await FindFolderAsync(svc, projectName, folderRel);
        if (parentId is null) return false;
        var fileId = await FindFileIdAsync(svc, parentId, leafName);
        if (fileId is null) return false;
        await svc.Files.Update(new File { Trashed = true }, fileId).ExecuteAsync();
        return true;
    }

    public async Task<bool> RenameAsync(string projectName, string folderRel, string leafName, string newName)
    {
        var svc = await CreateServiceAsync();
        var parentId = await FindFolderAsync(svc, projectName, folderRel);
        if (parentId is null) return false;
        var fileId = await FindFileIdAsync(svc, parentId, leafName);
        if (fileId is null) return false;
        await svc.Files.Update(new File { Name = newName }, fileId).ExecuteAsync();
        return true;
    }

    public async Task<bool> MoveAsync(string projectName, string folderRel, string leafName, string destFolderRel, bool createParents)
    {
        var svc = await CreateServiceAsync();
        var srcId = await FindFolderAsync(svc, projectName, folderRel);
        if (srcId is null) return false;

        var destId = createParents
            ? await EnsureFolderAsync(svc, projectName, destFolderRel)
            : await FindFolderAsync(svc, projectName, destFolderRel);

        if (destId is null) return false;

        var fileId = await FindFileIdAsync(svc, srcId, leafName);
        if (fileId is null) return false;

        var get = svc.Files.Get(fileId);
        get.Fields = "parents";
        var f = await get.ExecuteAsync();
        var prevParents = string.Join(",", f.Parents ?? new List<string>());

        var upd = svc.Files.Update(new File(), fileId);
        upd.AddParents = destId;
        upd.RemoveParents = prevParents;
        await upd.ExecuteAsync();
        return true;
    }

    public async Task<object> MkdirAsync(string projectName, string folderRel)
    {
        var svc = await CreateServiceAsync();
        var id = await EnsureFolderAsync(svc, projectName, folderRel);
        return new { ok = true, folderId = id };
    }
}