public record SaveRequest(
    string ProjectName,
    string FileName,
    string Content
);

public record AppendRequest(
    string ProjectName,
    string FileName,
    string ContentToAppend,
    bool? Newline
);

public record RenameRequest(
    string ProjectName,
    string FileName,
    string NewName
);

public record MoveRequest(
    string ProjectName,
    string FileName,
    string DestFolder,       // np. "archive/2025/"
    bool? CreateParents
);

public record MkdirRequest(
    string ProjectName,
    string FileName          // kończy się na "/" → folder
);

public record FileEntry(string Name, string Id, string MimeType, string? WebUrl);
public record FolderEntry(string Name, string Id, string? WebUrl);

public record ListResponse(bool Ok, string Path, IEnumerable<FolderEntry> Folders, IEnumerable<FileEntry> Files);