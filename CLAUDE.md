# Disk space analysis

The `ntfs` MCP server (`.mcp.json`) has live, read-only metadata for every file on the volume. It is built from
the NTFS master file table and kept current with the USN journal. Use it to find where space goes and which data
can be deleted or moved to cold storage.

- Sizes are **on-disk** (allocated) bytes, so compressed, sparse and cloud-placeholder files count only what they occupy.
- For a directory, `modified` is the newest modification of any file inside it. An old date on a big directory makes it a cold-storage candidate.
- `duplicates` matches files on name and size only. Contents are not compared, so present the results as candidates.
- The server cannot read, move or delete anything. Give recommendations and let the user act on them.

Typical flow: `summary` → `list` drilling into the largest dirs → `largest` (with `older_than_days`) / `extensions` /
`duplicates` / `find` (e.g. `node_modules`, `*.iso`, `.venv`) to build a list of savings with sizes and paths.

If the tools are missing, the server isn't running. Ask the user to start it from an elevated terminal:
`dotnet run -c Release` (or `bin\Release\net8.0\ntfs-meta.exe C:`).
