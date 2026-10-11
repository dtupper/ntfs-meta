# ntfs-meta

A minimal, read-only metadata server for an NTFS volume. At startup it reads the `$MFT` straight off the disk,
which takes a few seconds for millions of files. It then follows the USN journal to stay current. The same queries
are served over REST and MCP. The server has no file-content, write or delete operations.

```
dotnet build -c Release
bin\Release\net8.0\ntfs-meta.exe C: [--port 7801]     # elevated terminal: raw volume access needs admin
```

| Tool         | Purpose                                                           |
|--------------|-------------------------------------------------------------------|
| `summary`    | capacity / used / free, file and dir counts                       |
| `list`       | children of a dir by recursive size (+ newest modification)       |
| `largest`    | biggest files under a path; `ext`, `older_than_days` filters      |
| `extensions` | size by file extension                                            |
| `duplicates` | same name + size groups, by reclaimable space                     |
| `find`       | wildcard name search, largest first                               |

- REST: `GET /api` lists the tools. `GET /api/list?path=C:\Users&limit=20` calls one.
- MCP: `POST /mcp` (streamable HTTP, stateless). `.mcp.json` registers it for Claude Code in this directory.

The server listens on 127.0.0.1 only and rejects cross-origin browser requests.

## Files

- `Ntfs.cs` handles the raw volume: boot sector, `$MFT` runs, record parsing and USN journal reads.
- `Index.cs` holds the in-memory tree, the lazily rolled-up directory totals, and the queries.
- `Program.cs` defines the tool table plus the REST and MCP endpoints.

## Limits

- Hard-linked files are counted once, under their first name.
- Alternate data streams and directory index blocks are not counted.
- An `$MFT` so fragmented that it needs an attribute list is not supported.
