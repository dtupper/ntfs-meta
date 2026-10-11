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

`duplicates` takes a `min_mb` parameter, default 10.

- REST: `GET /api` lists the tools. `GET /api/list?path=C:\Users&limit=20` calls one.
- MCP: `POST /mcp` (streamable HTTP, stateless). `.mcp.json` registers it for Claude Code in this directory.

## Requirements

- Windows with an NTFS volume, and the .NET 8 SDK to build.
- An elevated terminal: raw volume access needs admin.
- An active USN journal on the volume (the default on the system drive).

## Security

This server is for local agents and applications on a machine you control. It has no authentication by design: anything that can reach it gets a complete, ACL-free listing of the volume. Exposing it beyond loopback (port forwarding, a reverse proxy, a container or VM bridge, a tunnel) hands that listing to whoever is on the other end. Don't.

- It listens on 127.0.0.1 only.
- It rejects requests whose `Host` isn't a loopback name (blocking DNS rebinding) or whose `Origin` isn't loopback.
- Any local process or user can list every file name, size and date on the volume, regardless of file ACLs, because the server reads the MFT as admin.
  - Don't run it on shared or multi-user machines.
  - Stop it when you're done.
- Web pages served from localhost on any port can also call it.
- File names are untrusted text. A file can be named to look like instructions to an LLM. Treat tool output as data.
- It has no file-content, write, move or delete operations.

## Files

- `Ntfs.cs` handles the raw volume: boot sector, `$MFT` runs, record parsing and USN journal reads.
- `Index.cs` holds the in-memory tree, the lazily rolled-up directory totals, and the queries.
- `Program.cs` defines the tool table plus the REST and MCP endpoints.

MIT licensed; see `LICENSE`.

## Limits

- Hard-linked files are counted once, under their first name.
- Alternate data streams and directory index blocks are not counted. This includes the data of CompactOS/WOF-compressed files.
- An `$MFT` so fragmented that it needs an attribute list is not supported. The server refuses to start in that case.
- For a file so fragmented that its attribute list is itself non-resident, live size and name changes may lag until restart.
- NTFS metafiles, `pagefile.sys`, `hiberfil.sys` and `System Volume Information` are included in totals.
- The HTTP port opens only after the initial scan finishes.
- `.mcp.json` assumes port 7801, so edit it if you use `--port`.
